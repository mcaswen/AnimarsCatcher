using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Benchmarks.LegacyNavigation;
using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Player;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 生成固定规模 Ani，并按 Tick 回放已经通过权限校验的移动命令
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AniMovementPlannerSystem))]
    public partial struct LegacyNavigationBenchmarkHarnessSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate<LegacyNavigationBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            LegacyNavigationBenchmarkState benchmarkStateValue =
                SystemAPI.GetSingleton<LegacyNavigationBenchmarkState>();
            LegacyNavigationBenchmarkPhase phase = benchmarkStateValue.Phase;

            if (phase == LegacyNavigationBenchmarkPhase.WaitingForScene)
            {
                // 等待场景烘焙的 Prefab 和 FSM 上下文就绪，避免把加载时间计入预热
                if (!SystemAPI.TryGetSingleton<AniGhostPrefabRegistry>(out var prefabRegistry) ||
                    !SystemAPI.HasSingleton<FsmContext>())
                {
                    return;
                }

                Entity benchmarkEntity =
                    SystemAPI.GetSingletonEntity<LegacyNavigationBenchmarkState>();
                InitializeBenchmark(
                    ref state,
                    prefabRegistry,
                    benchmarkEntity,
                    benchmarkStateValue);
                return;
            }

            RefRW<LegacyNavigationBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<LegacyNavigationBenchmarkState>();

            if (phase == LegacyNavigationBenchmarkPhase.Warmup)
            {
                AdvanceWarmup(benchmarkState, SystemAPI.GetSingleton<LegacyNavigationBenchmarkConfig>());
                return;
            }

            if (phase != LegacyNavigationBenchmarkPhase.Sampling)
            {
                return;
            }

            ReplayCurrentTick(ref state, benchmarkState);
        }

        private void InitializeBenchmark(
            ref SystemState state,
            AniGhostPrefabRegistry prefabRegistry,
            Entity benchmarkEntity,
            LegacyNavigationBenchmarkState benchmarkState)
        {
            LegacyNavigationBenchmarkConfig config =
                SystemAPI.GetSingleton<LegacyNavigationBenchmarkConfig>();
            EntityManager entityManager = state.EntityManager;

            // 合成队长只提供所有权和阵型锚点，不参与导航负载
            Entity leaderEntity = entityManager.CreateEntity(
                typeof(CharacterTag),
                typeof(LocalTransform),
                typeof(GhostOwner),
                typeof(LegacyNavigationBenchmarkOwnedTag));
            entityManager.SetComponentData(
                leaderEntity,
                LocalTransform.FromPositionRotation(config.SpawnOrigin, quaternion.identity));
            entityManager.SetComponentData(leaderEntity, new GhostOwner { NetworkId = 1 });

            for (int index = 0; index < config.AgentCount; index++)
            {
                Entity aniEntity = entityManager.Instantiate(prefabRegistry.PickerAniPrefabEntity);
                // 固定索引、规模和种子共同决定生成位置，重复运行保持完全一致
                float3 spawnPosition = LegacyNavigationBenchmarkAlgorithms.CalculateSpawnPosition(
                    index,
                    config.AgentCount,
                    config.SpawnColumnCount,
                    config.SpawnSpacing,
                    config.SpawnOrigin,
                    config.RandomSeed);
                entityManager.SetComponentData(
                    aniEntity,
                    LocalTransform.FromPositionRotation(spawnPosition, quaternion.identity));
                SetOrAdd(entityManager, aniEntity, new GhostOwner { NetworkId = 1 });
                SetOrAdd(entityManager, aniEntity, new Camp { Value = CampType.Alpha });
                SetOrAdd(entityManager, aniEntity, new AniFormationMember
                {
                    leader = leaderEntity,
                    slotIndex = index
                });
                entityManager.AddComponent<LegacyNavigationBenchmarkAniTag>(aniEntity);

            // 预热前检查被测 Prefab 的必需组件，组件缺失时不能以较轻负载继续测试
                if (!entityManager.HasBuffer<FsmVar>(aniEntity) ||
                    !entityManager.HasComponent<NavAgent>(aniEntity) ||
                    !entityManager.HasComponent<NavSteering>(aniEntity) ||
                    !entityManager.HasComponent<AniMoveIntent>(aniEntity))
                {
                    FailBenchmark(
                        entityManager,
                        benchmarkEntity,
                        benchmarkState,
                        $"Ani Prefab 缺少 Legacy 导航组件，Entity={aniEntity.Index}");
                    return;
                }
            }

            benchmarkState.LeaderEntity = leaderEntity;
            // 零预热配置直接进入采样，避免额外消耗一个状态切换 Tick
            benchmarkState.Phase = config.WarmupTicks > 0
                ? LegacyNavigationBenchmarkPhase.Warmup
                : LegacyNavigationBenchmarkPhase.Sampling;
            benchmarkState.PhaseTick = 0;
            benchmarkState.NextCommandIndex = 0;
            entityManager.SetComponentData(benchmarkEntity, benchmarkState);

            Debug.Log(
                $"[LegacyNavigationBenchmark] 已创建 {config.AgentCount} 个 Ani，" +
                $"开始 {config.WarmupTicks} Tick 预热");
        }

        private static void AdvanceWarmup(
            RefRW<LegacyNavigationBenchmarkState> benchmarkState,
            LegacyNavigationBenchmarkConfig config)
        {
            benchmarkState.ValueRW.PhaseTick++;
            if (benchmarkState.ValueRO.PhaseTick < config.WarmupTicks)
            {
                return;
            }

            // 采样从独立的零基 Tick 开始，使回放命令时间不受预热长度影响
            benchmarkState.ValueRW.Phase = LegacyNavigationBenchmarkPhase.Sampling;
            benchmarkState.ValueRW.PhaseTick = 0;
            Debug.Log(
                $"[LegacyNavigationBenchmark] 预热结束，开始采样 {config.SampleTicks} Tick");
        }

        private void ReplayCurrentTick(
            ref SystemState state,
            RefRW<LegacyNavigationBenchmarkState> benchmarkState)
        {
            LegacyNavigationBenchmarkConfig config =
                SystemAPI.GetSingleton<LegacyNavigationBenchmarkConfig>();
            DynamicBuffer<LegacyNavigationBenchmarkCommandElement> commands =
                SystemAPI.GetSingletonBuffer<LegacyNavigationBenchmarkCommandElement>(isReadOnly: true);
            int commandIndex = benchmarkState.ValueRO.NextCommandIndex;
            int phaseTick = benchmarkState.ValueRO.PhaseTick;

            // 同一 Tick 可包含多条命令，必须全部提交后再推进回放游标
            while (commandIndex < commands.Length && commands[commandIndex].Tick == phaseTick)
            {
                ApplyCommand(
                    ref state,
                    commands[commandIndex],
                    config,
                    benchmarkState);
                commandIndex++;
            }

            benchmarkState.ValueRW.NextCommandIndex = commandIndex;
            benchmarkState.ValueRW.PhaseTick = phaseTick + 1;
            if (benchmarkState.ValueRO.PhaseTick >= config.SampleTicks)
            {
                benchmarkState.ValueRW.Phase = LegacyNavigationBenchmarkPhase.Completed;
            }
        }

        private void ApplyCommand(
            ref SystemState state,
            LegacyNavigationBenchmarkCommandElement command,
            LegacyNavigationBenchmarkConfig config,
            RefRW<LegacyNavigationBenchmarkState> benchmarkState)
        {
            float3 targetPosition = config.SpawnOrigin + command.TargetOffset;
            float3 forward = targetPosition - config.SpawnOrigin;
            // 目标与原点重合时使用固定前向，避免阵型旋转出现无效四元数
            forward = PlanarMath.NormalizeXZOrDefault(
                forward,
                new float3(0f, 0f, 1f));
            quaternion formationRotation = quaternion.LookRotationSafe(forward, math.up());

            foreach (var (_, entity) in
                     SystemAPI.Query<RefRO<AniAttributes>>()
                         .WithAll<LegacyNavigationBenchmarkAniTag>()
                         .WithEntityAccess())
            {
                DynamicBuffer<FsmVar> blackboard = SystemAPI.GetBuffer<FsmVar>(entity);
                Blackboard.SetInt(
                    ref blackboard,
                    AniMovementBlackboardKeys.CommandMode,
                    (int)AniMovementCommandMode.MoveTo);
                Blackboard.SetFloat3(
                    ref blackboard,
                    AniMovementBlackboardKeys.MoveToPosition,
                    targetPosition);
                Blackboard.SetFloat3(
                    ref blackboard,
                    AniMovementBlackboardKeys.MoveFormationTargetPoint,
                    targetPosition);
                Blackboard.SetFloat3(
                    ref blackboard,
                    AniMovementBlackboardKeys.MoveFormationForward,
                    forward);
                Blackboard.SetEntity(
                    ref blackboard,
                    AniMovementBlackboardKeys.TargetEntity,
                    Entity.Null);
                Blackboard.SetBool(
                    ref blackboard,
                    AniMovementBlackboardKeys.MoveArrived,
                    false);
                Blackboard.SetBool(
                    ref blackboard,
                    AniMovementBlackboardKeys.NavStop,
                    false);
            }

            benchmarkState.ValueRW.AppliedCommandCount++;
            // 最终空间指标按最后一次命令的阵型姿态计算
            benchmarkState.ValueRW.LastFormationCenter = targetPosition;
            benchmarkState.ValueRW.LastFormationRotation = formationRotation;
        }

        private static void SetOrAdd<T>(
            EntityManager entityManager,
            Entity entity,
            T value)
            where T : unmanaged, IComponentData
        {
            if (entityManager.HasComponent<T>(entity))
            {
                entityManager.SetComponentData(entity, value);
            }
            else
            {
                entityManager.AddComponentData(entity, value);
            }
        }

        private static void FailBenchmark(
            EntityManager entityManager,
            Entity benchmarkEntity,
            LegacyNavigationBenchmarkState benchmarkState,
            string reason)
        {
            benchmarkState.Phase = LegacyNavigationBenchmarkPhase.Failed;
            entityManager.SetComponentData(benchmarkEntity, benchmarkState);
            Debug.LogError($"[LegacyNavigationBenchmark] {reason}");

            // 批处理失败必须传播非零退出码，交互模式则保留现场供检查
            if (Application.isBatchMode)
            {
                Application.Quit(1);
            }
        }
    }
}
