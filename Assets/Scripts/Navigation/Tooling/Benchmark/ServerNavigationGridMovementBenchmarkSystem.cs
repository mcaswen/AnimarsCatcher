using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 按固定回放创建严格阵型或自由 Cohort 移动请求，并记录完整服务器帧性能
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridBenchmarkSystem))]
    public partial struct ServerNavigationGridMovementBenchmarkSystem : ISystem
    {
        private const float AgentMaximumSpeed = 8f;
        private const float AgentMaximumAcceleration = 32f;
        private const int FormationColumnCount = 8;
        internal const int MaximumSettlementTicks = 600;
        internal const int StageSixMaximumSettlementTicks = 900;
        internal const int ExtendedStressMaximumSettlementTicks = 2700;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            state.RequireForUpdate<NavigationGridReference>();

        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            bool supportsMovement =
                config.Workload == NavigationGridBenchmarkWorkload.StrictFormationBaseline ||
                config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement;
            if (!supportsMovement)
            {
                // 其他工作负载由各自的基准系统推进，不会共用移动生命周期状态
                return;
            }

            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<NavigationGridBenchmarkConfig>();
            if (config.RecordMovementTrace != 0)
            {
                EnsureTraceBuffers(ref state, benchmarkEntity);
            }

            NavigationGridMovementBenchmarkState benchmarkState =
                SystemAPI.GetSingleton<NavigationGridMovementBenchmarkState>();
            if (benchmarkState.Completed != 0)
            {
                // Completed 表示固定时间窗口已经结束，导出前仍要读取队伍的最新状态
                // 结束帧后运行时又更新了一轮，因此再次确认完成状态没有回退
                if (!TryGetTerminalState(ref state, config.Workload, out bool failed) || failed)
                {
                    FailBenchmark(
                        ref state,
                        benchmarkEntity,
                        "Grid 群体移动在固定终止 Tick 后未保持完成状态");
                    return;
                }

                if (benchmarkState.ResultExported == 0)
                {
                    // 每次运行只导出一次，避免后续帧重复写入同一结果
                    ExportResult(ref state, config, benchmarkState);
                    benchmarkState.ResultExported = 1;
                    state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
                    // 导出完成后禁用系统，防止重复写文件
                    state.Enabled = false;
                }

                return;
            }

            if (benchmarkState.Initialized == 0)
            {
                DynamicBuffer<NavigationGridBenchmarkCommand> initialCommands =
                    SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true);
                if (initialCommands.IsEmpty)
                {
                    // 回放没有任何命令时无法确定移动目标，直接生成失败报告并停止
                    FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动 Benchmark 命令回放为空");
                    return;
                }

                CreateAgents(ref state, benchmarkEntity, config);

                // 先记录已经初始化，再创建指令 Entity，避免结构变化后下一帧重复生成成员
                benchmarkState.Initialized = 1;
                benchmarkState.NextCommandSequence = 1;
                state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
                Debug.Log($"[NavigationBenchmark] 已创建 {config.AgentCount} 个 Grid Benchmark Ani");
            }

            // 创建 Ani 会改变 Entity 结构，因此先复制回放命令，再进行后续结构变更
            // NativeArray 副本在本轮所有指令提交完成后释放
            using NativeArray<NavigationGridBenchmarkCommand> commands =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true)
                    .ToNativeArray(Allocator.Temp);
            int sampleTick = benchmarkState.Tick - config.WarmupTicks;
            // 预热结束后回放帧从 0 开始，命令时间不受启动过程影响
            while (sampleTick >= 0 &&
                   benchmarkState.NextCommandIndex < commands.Length &&
                   commands[benchmarkState.NextCommandIndex].Tick == sampleTick)
            {
                // 同一回放帧可以包含多条命令，按原顺序逐条转成队伍指令
                SubmitCommand(
                    ref state,
                    config,
                    commands[benchmarkState.NextCommandIndex],
                    ref benchmarkState);
                benchmarkState.NextCommandIndex++;
            }

            benchmarkState.Tick++;
            int sampleEndTick = config.WarmupTicks + config.SampleTicks;
            int settlementTicks = GetSettlementTicks(config);
            int terminationTick = sampleEndTick + settlementTicks;
            // 采样和等待队伍站稳共用同一个服务器帧计数，不受渲染帧率影响
            // 帧数在末尾递增，确保第 0 帧提交的命令属于采样窗口
            if (benchmarkState.Tick >= sampleEndTick)
            {
                bool terminal = TryGetTerminalState(ref state, config.Workload, out bool failed);
                if (failed)
                {
                    // 一旦队伍进入失败状态，就保留原因并停止后续采样
                    FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动路径失败");
                    return;
                }

                if (terminal && benchmarkState.CompletionTick == 0)
                {
                    // 首次完成帧仅用于诊断，不会因异步任务完成早晚而提前结束固定窗口
                    benchmarkState.CompletionTick = benchmarkState.Tick;
                }

                if (benchmarkState.Tick >= terminationTick)
                {
                    if (!terminal)
                    {
                        // 固定窗口结束后队伍仍未站稳属于功能失败，不能当作有效性能样本
                        FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动在固定终止 Tick 未完成");
                        return;
                    }

                    // 只在固定结束帧标记 Completed，下一轮运行时更新后再复核和导出
                    benchmarkState.Completed = 1;
                }
            }

            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
        }

        private static void EnsureTraceBuffers(ref SystemState state, Entity benchmarkEntity)
        {
            if (!state.EntityManager.HasBuffer<NavigationGridMovementBenchmarkStateTrace>(
                    benchmarkEntity))
            {
                state.EntityManager.AddBuffer<NavigationGridMovementBenchmarkStateTrace>(
                    benchmarkEntity);
            }

            if (!state.EntityManager.HasBuffer<NavigationGridMovementBenchmarkAgentTrace>(
                    benchmarkEntity))
            {
                state.EntityManager.AddBuffer<NavigationGridMovementBenchmarkAgentTrace>(
                    benchmarkEntity);
            }
        }

        private static int GetSettlementTicks(NavigationGridBenchmarkConfig config)
        {
            if (config.Workload != NavigationGridBenchmarkWorkload.FreeCohortMovement)
            {
                return MaximumSettlementTicks;
            }

            // 10 万实验会生成更多 Cohort，收尾窗口需覆盖分时分配，但不改变正式采样时长
            return NavigationGridBenchmarkScaleProfile.IsExtendedStressAgentCount(
                config.AgentCount)
                ? ExtendedStressMaximumSettlementTicks
                : StageSixMaximumSettlementTicks;
        }

        private static void CreateAgents(
            ref SystemState state,
            Entity benchmarkEntity,
            NavigationGridBenchmarkConfig config)
        {
            int count = math.max(1, config.AgentCount);
            EntityArchetype archetype = state.EntityManager.CreateArchetype(
                typeof(LocalTransform),
                typeof(NavigationGridMovementBenchmarkAni));
            using var agents = new NativeArray<Entity>(count, Allocator.Temp);
            // 批量创建避免万人入口把重复结构变更混入第一个采样 Tick
            state.EntityManager.CreateEntity(archetype, agents);
            using EntityQuery gridQuery = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            NavigationGridReference gridReference = gridQuery.GetSingleton<
                NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            int spawnColumnCount = config.SpawnColumnCount;
            float spawnSpacing = config.SpawnSpacing;
            if (config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement &&
                NavigationGridBenchmarkScaleProfile.UsesLargeScaleGrid(count))
            {
                // 大规模压力档使用近似方形布局，不能沿用历史基线固定 16 列无限向外扩张
                spawnColumnCount = (int)math.ceil(math.sqrt(count));
                spawnSpacing = math.max(config.AgentRadius * 2f + 0.05f, 0.75f);
            }

            for (int agentIndex = 0; agentIndex < count; agentIndex++)
            {
                float3 position = NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                    agentIndex,
                    count,
                    spawnColumnCount,
                    spawnSpacing,
                    config.SpawnOrigin,
                    config.RandomSeed);
                if (config.Scenario != NavigationGridBenchmarkScenario.Open &&
                    NavigationGridQuery.TryWorldToCell(
                        ref grid,
                        position,
                        out _,
                        out int spawnCellIndex) &&
                    !NavigationGridQuery.CanAgentOccupy(
                        ref grid,
                        spawnCellIndex,
                        config.AgentRadius,
                        0.05f) &&
                    NavigationGridQuery.TryProjectToNearestCell(
                        ref grid,
                        position,
                        config.AgentRadius,
                        0.05f,
                        8,
                        out int projectedCellIndex))
                {
                    // 合成障碍可能穿过规则出生阵列，先投影到最近安全 Cell 再进入真实导航链路
                    position = NavigationGridQuery.GetCellWorldPosition(
                        ref grid,
                        projectedCellIndex);
                }
                // 先把 Transform 放到固定起点，移动组件由队伍生命周期系统处理指令时添加
                state.EntityManager.SetComponentData(
                    agents[agentIndex],
                    LocalTransform.FromPositionRotation(position, quaternion.identity));
                state.EntityManager.SetComponentData(
                    agents[agentIndex],
                    // AgentIndex 用于跨运行识别同一成员，不能改用可能变化的 Entity.Index
                    new NavigationGridMovementBenchmarkAni { AgentIndex = agentIndex });
            }

            DynamicBuffer<NavigationGridMovementBenchmarkAgent> stableAgents =
                state.EntityManager.HasBuffer<NavigationGridMovementBenchmarkAgent>(benchmarkEntity)
                    ? state.EntityManager.GetBuffer<NavigationGridMovementBenchmarkAgent>(
                        benchmarkEntity)
                    : state.EntityManager.AddBuffer<NavigationGridMovementBenchmarkAgent>(
                        benchmarkEntity);
            stableAgents.Clear();
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                stableAgents.Add(new NavigationGridMovementBenchmarkAgent
                {
                    Ani = agents[agentIndex],
                    StableId = agentIndex + 1,
                });
            }
        }

        private void SubmitCommand(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkCommand command,
            ref NavigationGridMovementBenchmarkState benchmarkState)
        {
            EntityManager entityManager = state.EntityManager;
            using NativeArray<NavigationGridMovementBenchmarkAgent> agents =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkAgent>(true)
                    .ToNativeArray(Allocator.Temp);
            if (agents.Length < config.AgentCount)
            {
                // 实际成员少于配置值会改变工作负载，因此明确失败而不是悄悄降低规模
                throw new InvalidOperationException(
                    $"Grid Benchmark Ani 数量不足，期望 {config.AgentCount}，实际 {agents.Length}");
            }

            float3 targetPosition = config.SpawnOrigin + command.TargetOffset;

            // 基准只提交 MoveTo，队伍朝向由起点指向目标的水平方向确定
            float3 forward = targetPosition - config.SpawnOrigin;
            forward = PlanarMath.NormalizeXZOrDefault(
                forward,
                new float3(0f, 0f, 1f));

            if (config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement)
            {
                if (config.Scenario == NavigationGridBenchmarkScenario.ObstacleLowReuse)
                {
                    benchmarkState.AppliedCommandCount += SubmitLowReuseMovementOrders(
                        entityManager,
                        config,
                        agents,
                        targetPosition,
                        ref benchmarkState);
                }
                else
                {
                    uint freeSequence = NextCommandSequence(ref benchmarkState);
                    SubmitFreeMovementOrder(
                        entityManager,
                        config,
                        agents,
                        targetPosition,
                        freeSequence,
                        benchmarkState.Tick);
                    benchmarkState.AppliedCommandCount++;
                }
                return;
            }

            uint sequence = NextCommandSequence(ref benchmarkState);
            Entity commandEntity = entityManager.CreateEntity(
                typeof(AniSquadCommandRequest),
                typeof(AniSquadCommand));
            // 创建 Entity 时同时写入请求标记和指令数据，避免生命周期系统读到不完整的指令
            entityManager.SetComponentData(commandEntity, new AniSquadCommand
            {
                Sequence = sequence,
                OwnerNetworkId = 1,
                Mode = AniSquadCommandMode.MoveTo,
                Formation = AniSquadFormationKind.CompactRectangle,
                TargetPosition = targetPosition,
                TargetEntity = Entity.Null,
                FormationColumnCount = FormationColumnCount,
                TargetStoppingDistance = 0.7f,
                DesiredForward = forward,
            });

            DynamicBuffer<AniSquadCommandMember> members =
                entityManager.AddBuffer<AniSquadCommandMember>(commandEntity);

            // 成员按 AgentIndex 排序加入指令，使不同 World 中的初始槽位分配一致
            for (int index = 0; index < config.AgentCount; index++)
            {
                int stableId = entityManager
                    .GetComponentData<NavigationGridMovementBenchmarkAni>(agents[index].Ani)
                    .AgentIndex;
                members.Add(new AniSquadCommandMember
                {
                    Ani = agents[index].Ani,
                    StableId = stableId,
                    MaxSpeed = AgentMaximumSpeed,
                    MaxAcceleration = AgentMaximumAcceleration,
                    AgentRadius = config.AgentRadius,
                });
            }

            // AppliedCommandCount 统计队伍指令数，不会按成员数重复累加
            benchmarkState.AppliedCommandCount++;
        }

        private static int SubmitLowReuseMovementOrders(
            EntityManager entityManager,
            NavigationGridBenchmarkConfig config,
            NativeArray<NavigationGridMovementBenchmarkAgent> agents,
            float3 targetPosition,
            ref NavigationGridMovementBenchmarkState benchmarkState)
        {
            const int targetCount = 8;
            for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                int column = targetIndex & 3;
                int row = targetIndex >> 2;
                // 多目标全部位于障碍墙左侧，差异只改变目标场复用率而不改变可达性
                float3 targetOffset = new float3(-12f + column * 4f, 0f, row == 0 ? -6f : 6f);
                uint sequence = NextCommandSequence(ref benchmarkState);
                SubmitFreeMovementOrder(
                    entityManager,
                    config,
                    agents,
                    targetPosition + targetOffset,
                    sequence,
                    benchmarkState.Tick,
                    targetIndex,
                    targetCount);
            }
            return targetCount;
        }

        private static void SubmitFreeMovementOrder(
            EntityManager entityManager,
            NavigationGridBenchmarkConfig config,
            NativeArray<NavigationGridMovementBenchmarkAgent> agents,
            float3 targetPosition,
            uint sequence,
            int tick,
            int groupIndex = 0,
            int groupCount = 1)
        {
            Entity orderEntity = entityManager.CreateEntity(
                typeof(AniMovementOrderRequest),
                typeof(AniMovementOrder));
            entityManager.SetComponentData(orderEntity, new AniMovementOrder
            {
                Sequence = sequence,
                OwnerNetworkId = 1,
                SelectionVersion = sequence,
                SelectionHash = sequence,
                CreatedTick = unchecked((uint)math.max(0, tick)),
                CancellationVersion = sequence,
                Priority = 0,
                Mode = AniSquadCommandMode.MoveTo,
                TargetPosition = targetPosition,
                TargetEntity = Entity.Null,
                TargetStoppingDistance = 0.7f,
                GoalCellCapacityScale = 1f,
                GoalInfluenceRadius = 4f,
            });
            DynamicBuffer<AniMovementOrderMember> members =
                entityManager.AddBuffer<AniMovementOrderMember>(orderEntity);
            // 稳定引用 Buffer 已按创建编号排列，万人命令不再执行 O(N²) 插入排序
            for (int index = groupIndex; index < config.AgentCount; index += groupCount)
            {
                NavigationGridMovementBenchmarkAgent agent = agents[index];
                members.Add(new AniMovementOrderMember
                {
                    Ani = agent.Ani,
                    GhostId = agent.StableId,
                    MaxSpeed = AgentMaximumSpeed,
                    MaxAcceleration = AgentMaximumAcceleration,
                    AgentRadius = config.AgentRadius,
                    AgentProfile = 1,
                });
            }
        }

        private bool TryGetTerminalState(
            ref SystemState state,
            NavigationGridBenchmarkWorkload workload,
            out bool failed)
        {
            failed = false;
            if (workload == NavigationGridBenchmarkWorkload.FreeCohortMovement)
            {
                bool foundOrder = false;
                foreach (RefRO<AniMovementOrderState> orderState in
                         SystemAPI.Query<RefRO<AniMovementOrderState>>())
                {
                    foundOrder = true;
                    if (orderState.ValueRO.Status == AniMovementOrderStatus.Failed)
                    {
                        failed = true;
                        return true;
                    }

                    if (orderState.ValueRO.Status == AniMovementOrderStatus.Active ||
                        orderState.ValueRO.Status == AniMovementOrderStatus.Pending)
                    {
                        return false;
                    }
                }

                return foundOrder;
            }

            bool foundSquad = false;

            // 所有队伍都必须完成或进入 Follow 等待状态，任一队伍失败都会终止基准
            // 当前只创建一支队伍，但检查流程也支持以后扩展为多队回放
            foreach (RefRO<AniSquadPathState> pathState in
                     SystemAPI.Query<RefRO<AniSquadPathState>>())
            {
                foundSquad = true;
                if (pathState.ValueRO.Status == AniSquadMovementStatus.Failed)
                {
                    // 发现失败队伍后立即返回，不必等待其他队伍
                    failed = true;
                    return true;
                }

                if (pathState.ValueRO.Status != AniSquadMovementStatus.Completed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Holding)
                {
                    // 仍有队伍在移动时继续等待，不提前导出部分结果
                    return false;
                }
            }

            // 没有创建出任何队伍时不能判定为成功
            return foundSquad;
        }

        private static uint NextCommandSequence(
            ref NavigationGridMovementBenchmarkState benchmarkState)
        {
            uint sequence = benchmarkState.NextCommandSequence++;

            // 0 保留为未初始化序号，计数溢出后跳过 0
            if (benchmarkState.NextCommandSequence == 0)
            {
                benchmarkState.NextCommandSequence = 1;
            }

            return sequence == 0 ? NextCommandSequence(ref benchmarkState) : sequence;
        }

        private void ExportResult(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridMovementBenchmarkState benchmarkState)
        {
            var positions = new List<float3>(config.AgentCount);
            long transformWriteCount = 0;
            int arrivedCount = 0;
            float totalFormationError = 0f;
            int measuredAgentCount = 0;
            int unsettledAgentCount = 0;
            int minimumUnsettledAgentIndex = int.MaxValue;
            int maximumUnsettledAgentIndex = -1;
            float maximumTargetDistance = 0f;
            uint minimumCommitCount = uint.MaxValue;
            uint maximumCommitCount = 0;
            var finalAgentSamples = new List<FinalAgentSample>(config.AgentCount);
            var unsettledAgents = new List<NavigationGridMovementUnsettledAgentReport>(64);
            NavigationGridReference gridReference = SystemAPI.GetSingleton<NavigationGridReference>();

            // 直接从成员结果汇总到达率和阵型误差，不再重复遍历队伍缓冲区
            // 只统计带基准标记的 Ani，不混入场景中的正式单位
            foreach (var (transform, result, benchmarkAni) in
                     SystemAPI.Query<
                             RefRO<LocalTransform>,
                             RefRO<AniMovementResult>,
                             RefRO<NavigationGridMovementBenchmarkAni>>())
            {
                positions.Add(transform.ValueRO.Position);
                finalAgentSamples.Add(new FinalAgentSample
                {
                    AgentIndex = benchmarkAni.ValueRO.AgentIndex,
                    Position = transform.ValueRO.Position,
                });
                measuredAgentCount++;
                transformWriteCount += result.ValueRO.CommitCount;
                totalFormationError += result.ValueRO.DistanceToSlot;
                maximumTargetDistance = math.max(
                    maximumTargetDistance,
                    result.ValueRO.DistanceToSlot);
                minimumCommitCount = math.min(minimumCommitCount, result.ValueRO.CommitCount);
                maximumCommitCount = math.max(maximumCommitCount, result.ValueRO.CommitCount);
                if (result.ValueRO.Settled != 0)
                {
                    // 到达成员必须同时靠近槽位并基本停稳，单纯经过目标不算完成
                    arrivedCount++;
                }
                else
                {
                    unsettledAgentCount++;
                    minimumUnsettledAgentIndex = math.min(
                        minimumUnsettledAgentIndex,
                        benchmarkAni.ValueRO.AgentIndex);
                    maximumUnsettledAgentIndex = math.max(
                        maximumUnsettledAgentIndex,
                        benchmarkAni.ValueRO.AgentIndex);
                }
            }
            if (config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement &&
                unsettledAgentCount > 0)
            {
                // 自由移动失败时才查询 Cohort 专用组件，不改变历史严格阵型的通用统计
                foreach (var (transform, result, benchmarkAni, goal, membership, movementConfig) in
                         SystemAPI.Query<
                             RefRO<LocalTransform>,
                             RefRO<AniMovementResult>,
                             RefRO<NavigationGridMovementBenchmarkAni>,
                             RefRO<AniGoalAssignment>,
                             RefRO<AniMovementCohortMembership>,
                             RefRO<AniMovementConfig>>())
                {
                    if (result.ValueRO.Settled == 0)
                    {
                        unsettledAgents.Add(BuildUnsettledAgentReport(
                            state.EntityManager,
                            ref gridReference.Value.Value,
                            transform.ValueRO,
                            result.ValueRO,
                            benchmarkAni.ValueRO,
                            goal.ValueRO,
                            membership.ValueRO,
                            movementConfig.ValueRO));
                    }
                }
            }
            if (measuredAgentCount == 0)
            {
                minimumCommitCount = 0;
            }
            if (unsettledAgentCount == 0)
            {
                minimumUnsettledAgentIndex = -1;
            }

            // 最终位置按稳定编号排序后计算，Entity 查询顺序不会影响跨运行 Hash
            finalAgentSamples.Sort(
                (left, right) => left.AgentIndex.CompareTo(right.AgentIndex));
            ulong finalPositionHash = 14695981039346656037UL;
            for (int index = 0; index < finalAgentSamples.Count; index++)
            {
                FinalAgentSample sample = finalAgentSamples[index];
                finalPositionHash = MixFinalPositionHash(
                    finalPositionHash,
                    sample.AgentIndex);
                finalPositionHash = MixFinalPositionHash(
                    finalPositionHash,
                    (int)math.round(sample.Position.x * 1000f));
                finalPositionHash = MixFinalPositionHash(
                    finalPositionHash,
                    (int)math.round(sample.Position.z * 1000f));
            }

            bool freeMovement =
                config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement;
            int squadCount = 0;
            int cohortCount = 0;
            int pathRequestCount = 0;
            int pathSuccessCount = 0;
            int pathFailureCount = 0;
            int cacheHitCount = 0;
            int directRouteCount = 0;
            int awaitingCohortCount = 0;
            int movingCohortCount = 0;
            int holdingCohortCount = 0;
            int completedCohortCount = 0;
            int failedCohortCount = 0;
            if (freeMovement)
            {
                // 正式链路按 Cohort 汇总请求，构建次数由共享 Field 调度器另行记录
                foreach (RefRO<AniMovementCohortPathState> pathState in
                         SystemAPI.Query<RefRO<AniMovementCohortPathState>>())
                {
                    cohortCount++;
                    pathRequestCount += pathState.ValueRO.FieldRequestCount;
                    pathSuccessCount += pathState.ValueRO.SuccessfulFieldRequestCount;
                    pathFailureCount += pathState.ValueRO.FailedFieldRequestCount;
                    cacheHitCount += pathState.ValueRO.CacheHitCount;
                    directRouteCount += pathState.ValueRO.DirectRouteCount;
                    switch (pathState.ValueRO.Status)
                    {
                        case AniMovementCohortStatus.AwaitingPath:
                            awaitingCohortCount++;
                            break;
                        case AniMovementCohortStatus.Moving:
                            movingCohortCount++;
                            break;
                        case AniMovementCohortStatus.Holding:
                            holdingCohortCount++;
                            break;
                        case AniMovementCohortStatus.Completed:
                            completedCohortCount++;
                            break;
                        case AniMovementCohortStatus.Failed:
                            failedCohortCount++;
                            break;
                    }
                }
            }
            else
            {
                foreach (RefRO<AniSquadPathState> pathState in
                         SystemAPI.Query<RefRO<AniSquadPathState>>())
                {
                    squadCount++;
                    pathRequestCount += pathState.ValueRO.FieldRequestCount;
                    pathSuccessCount += pathState.ValueRO.SuccessfulFieldRequestCount;
                    pathFailureCount += pathState.ValueRO.FailedFieldRequestCount;
                    cacheHitCount += pathState.ValueRO.CacheHitCount;
                }
            }

            // 万人自由移动在 ORCA 接入前不执行 O(N²) 间距统计，避免导出阶段冻结服务器
            float minimumUnitSpacing = freeMovement
                ? -1f
                : StatisticsMath.CalculateMinimumPairwiseDistance(positions);
            bool transformWriteCountMatches =
                measuredAgentCount == config.AgentCount &&
                minimumCommitCount == maximumCommitCount &&
                transformWriteCount == (long)measuredAgentCount * minimumCommitCount;

            // 复制并排序样本用于计算百分位，原始顺序仍写入 JSON 以定位具体帧尖峰
            DynamicBuffer<NavigationGridMovementBenchmarkTimingSample> samples =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkTimingSample>();
            double[] tickMilliseconds = new double[samples.Length];
            long[] allocatedBytes = new long[samples.Length];
            double[] sortedTickMilliseconds = new double[samples.Length];
            double[] sortedAllocatedBytes = new double[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                tickMilliseconds[index] = samples[index].ServerSimulationMilliseconds;
                allocatedBytes[index] = samples[index].MainThreadAllocatedBytes;
                sortedTickMilliseconds[index] = tickMilliseconds[index];
                sortedAllocatedBytes[index] = allocatedBytes[index];
            }

            Array.Sort(sortedTickMilliseconds);
            Array.Sort(sortedAllocatedBytes);

            NavigationGridMovementBenchmarkStateTraceReport[] stateTrace =
                BuildStateTraceReport(config.RecordMovementTrace != 0);
            NavigationGridMovementBenchmarkAgentTraceReport[] agentTrace =
                BuildAgentTraceReport(config.RecordMovementTrace != 0);
            // 只有开启逐帧诊断时才把数据复制到报告，正式性能采样不会增加这部分序列化开销
            FlowSchedulerReport schedulerReport = BuildFlowSchedulerReport(state.EntityManager);
            NavigationStageTimingReport[] stageTimings = BuildStageTimingReports();
            double navigationWorkerCriticalPathP95 = 0.0;
            for (int index = 0; index < stageTimings.Length; index++)
            {
                navigationWorkerCriticalPathP95 = Math.Max(
                    navigationWorkerCriticalPathP95,
                    stageTimings[index].P95Milliseconds);
            }

            // 同时写出百分位和原始样本，避免平均值掩盖偶发的慢帧
            var report = new NavigationGridMovementBenchmarkReport
            {
                // FormatVersion 用于以后扩展字段时选择兼容的解析方式
                // 报告只包含普通值，不序列化 Entity 或 NativeContainer
                FormatVersion = 11,
                Backend = AniMovementBackend.ClearanceGrid.ToString(),
                Workload = config.Workload.ToString(),
                Scenario = config.Scenario.ToString(),
                PerformanceGateEligible = freeMovement &&
                                          NavigationGridBenchmarkScaleProfile
                                              .IsStageSixAgentCount(config.AgentCount),
                BudgetVersion = NavigationGridBenchmarkScaleProfile.BudgetVersion,
                SystemTimingCoverage = "记录完整 Server Tick，并在诊断边界完成 Job 后统计导航阶段墙钟时间",
                WorkerTimingAvailable = stageTimings.Length > 0,
                NavigationWorkerCriticalPathP95Milliseconds =
                    navigationWorkerCriticalPathP95,
                StageTimings = stageTimings,
                RequestQueueTimingAvailable = schedulerReport.Available,
                TrackedNativeBytes = schedulerReport.Available
                    ? schedulerReport.StoreByteCount + schedulerReport.WorkspaceByteCount
                    : -1,
                FieldStoreNativeBytes = schedulerReport.StoreByteCount,
                FieldWorkspaceNativeBytes = schedulerReport.WorkspaceByteCount,
                FieldQueueLength = schedulerReport.QueueLength,
                FieldQueueWaitP50Ticks = schedulerReport.WaitP50Ticks,
                FieldQueueWaitP95Ticks = schedulerReport.WaitP95Ticks,
                FieldQueueWaitP99Ticks = schedulerReport.WaitP99Ticks,
                FieldCancelledCount = schedulerReport.CancelledCount,
                FieldTimeoutCount = schedulerReport.TimeoutCount,
                UniqueFieldBuildCount = schedulerReport.UniqueBuildCount,
                SharedFieldHitCount = schedulerReport.SharedHitCount,
                SharedFieldRecordCount = schedulerReport.StoreRecordCount,
                CorridorResolveCount = schedulerReport.CorridorResolveCount,
                TargetFlowRecordBuildCount = schedulerReport.TargetRecordBuildCount,
                CoverageTileInvalidationCount = schedulerReport.CoverageTileInvalidationCount,
                CoverageTileBuildCount = schedulerReport.CoverageTileBuildCount,
                CoverageTileReuseCount = schedulerReport.CoverageTileReuseCount,
                FieldBudgetThrottleCount = schedulerReport.BudgetThrottleCount,
                LastFieldBuildBatchMilliseconds = schedulerReport.LastBuildBatchMilliseconds,
                MaximumFieldBuildBatchMilliseconds =
                    schedulerReport.MaximumBuildBatchMilliseconds,
                AgentCount = config.AgentCount,
                RandomSeed = config.RandomSeed,
                WarmupTicks = config.WarmupTicks,
                SampleTicks = config.SampleTicks,
                MovementTraceRecorded = config.RecordMovementTrace != 0,
                StateTrace = stateTrace,
                AgentTrace = agentTrace,
                FirstCompletionTick = benchmarkState.CompletionTick,
                TerminationTick = config.WarmupTicks + config.SampleTicks +
                                  GetSettlementTicks(config),
                Failed = benchmarkState.Failed != 0,
                FailureReason = benchmarkState.FailureReason.ToString(),
                AppliedCommandCount = benchmarkState.AppliedCommandCount,
                SquadCount = squadCount,
                CohortCount = cohortCount,
                PathRequestCount = pathRequestCount,
                PathSuccessCount = pathSuccessCount,
                PathFailureCount = pathFailureCount,
                CacheHitCount = cacheHitCount,
                DirectRouteCount = directRouteCount,
                ArrivedCount = arrivedCount,
                UnsettledAgentCount = unsettledAgentCount,
                MinimumUnsettledAgentIndex = minimumUnsettledAgentIndex,
                MaximumUnsettledAgentIndex = maximumUnsettledAgentIndex,
                MaximumTargetDistance = maximumTargetDistance,
                UnsettledAgents = unsettledAgents.ToArray(),
                FinalPositionHash = $"{finalPositionHash:X16}",
                ArrivalRate = config.AgentCount == 0
                    ? 0f
                    : (float)arrivedCount / config.AgentCount,
                MinimumUnitSpacing = minimumUnitSpacing,
                AverageFormationError = positions.Count == 0
                    ? 0f
                    : totalFormationError / positions.Count,
                TransformWriteCount = transformWriteCount,
                MinimumCommitCount = minimumCommitCount,
                MaximumCommitCount = maximumCommitCount,
                TransformWriteCountMatches = transformWriteCountMatches,
                AwaitingCohortCount = awaitingCohortCount,
                MovingCohortCount = movingCohortCount,
                HoldingCohortCount = holdingCohortCount,
                CompletedCohortCount = completedCohortCount,
                FailedCohortCount = failedCohortCount,
                ServerTickP50Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.50),
                ServerTickP95Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.95),
                ServerTickP99Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.99),
                ServerTickMaxMilliseconds = sortedTickMilliseconds.Length == 0
                    ? 0.0
                    : sortedTickMilliseconds[^1],
                MainThreadAllocP50Bytes =
                    StatisticsMath.CalculateNearestRankPercentile(sortedAllocatedBytes, 0.50),
                MainThreadAllocP95Bytes =
                    StatisticsMath.CalculateNearestRankPercentile(sortedAllocatedBytes, 0.95),
                MainThreadAllocP99Bytes =
                    StatisticsMath.CalculateNearestRankPercentile(sortedAllocatedBytes, 0.99),
                GitCommit = config.GitCommit.ToString(),
                UnityVersion = Application.unityVersion,
                EntitiesAssemblyVersion = typeof(Entity).Assembly.GetName().Version?.ToString(),
                Platform = Application.platform.ToString(),
                OperatingSystem = SystemInfo.operatingSystem,
                Processor = SystemInfo.processorType,
                ProcessorCount = SystemInfo.processorCount,
                SystemMemoryMegabytes = SystemInfo.systemMemorySize,
                GraphicsDevice = SystemInfo.graphicsDeviceName,
                MapSceneHash = config.MapSceneHash.ToString(),
                ReplayScriptHash = config.ReplayScriptHash.ToString(),
                GridBakeHash = SystemAPI.GetSingleton<NavigationGridReference>()
                    .Value.Value.DataHash.ToString(),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                TickMilliseconds = tickMilliseconds,
                MainThreadAllocatedBytes = allocatedBytes,
                Notes = freeMovement
                    ? "自由 Cohort 与并行单位移动，不包含 ORCA、世界碰撞或受阻恢复"
                    : "严格矩形阵型历史基线，不包含自由 Cohort、ORCA、世界碰撞或受阻恢复",
            };

            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");

            // 结果目录不属于 Unity 资产，使用时间戳区分同一提交的多次运行
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"GridNavigation_{config.AgentCount}_{config.Scenario}_" +
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            // 文件名包含 Ani 数量和 UTC 时间，便于并列归档不同后端结果
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log(
                $"[NavigationBenchmark] Grid {config.Workload} 结果已生成：" +
                $"到达={arrivedCount}/{config.AgentCount}，提交一致={transformWriteCountMatches}，" +
                $"结果={path}");
        }

        private NavigationStageTimingReport[] BuildStageTimingReports()
        {
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> samples =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkStageTimingSample>();
            var reports = new List<NavigationStageTimingReport>();
            foreach (NavigationGridBenchmarkStage stage in
                     Enum.GetValues(typeof(NavigationGridBenchmarkStage)))
            {
                var values = new List<double>();
                for (int index = 0; index < samples.Length; index++)
                {
                    NavigationGridBenchmarkStageTimingSample sample = samples[index];
                    if (sample.Stage == stage)
                    {
                        values.Add(sample.WorkerMilliseconds);
                    }
                }

                if (values.Count == 0)
                {
                    continue;
                }

                values.Sort();
                double[] sortedValues = values.ToArray();
                reports.Add(new NavigationStageTimingReport
                {
                    Stage = stage.ToString(),
                    SampleCount = sortedValues.Length,
                    P50Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                        sortedValues,
                        0.50),
                    P95Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                        sortedValues,
                        0.95),
                    P99Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                        sortedValues,
                        0.99),
                    MaximumMilliseconds = sortedValues[^1],
                });
            }

            return reports.ToArray();
        }

        private static FlowSchedulerReport BuildFlowSchedulerReport(
            EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationFlowFieldSchedulerState>(),
                ComponentType.ReadOnly<NavigationFlowFieldQueueWaitSample>());
            if (query.CalculateEntityCount() != 1)
            {
                return default;
            }

            Entity storeEntity = query.GetSingletonEntity();
            NavigationFlowFieldSchedulerState schedulerState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            DynamicBuffer<NavigationFlowFieldQueueWaitSample> samples = entityManager.GetBuffer<
                NavigationFlowFieldQueueWaitSample>(storeEntity, true);
            var waits = new int[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                waits[index] = samples[index].WaitTicks;
            }
            Array.Sort(waits);

            return new FlowSchedulerReport
            {
                Available = true,
                SampleCount = waits.Length,
                QueueLength = schedulerState.QueueLength,
                WaitP50Ticks = GetNearestRank(waits, 0.50),
                WaitP95Ticks = GetNearestRank(waits, 0.95),
                WaitP99Ticks = GetNearestRank(waits, 0.99),
                CancelledCount = schedulerState.CumulativeCancelledCount,
                TimeoutCount = schedulerState.CumulativeTimeoutCount,
                UniqueBuildCount = schedulerState.CumulativeUniqueBuildCount,
                SharedHitCount = schedulerState.CumulativeSharedHitCount,
                StoreRecordCount = schedulerState.StoreRecordCount,
                StoreByteCount = schedulerState.StoreByteCount,
                WorkspaceByteCount = schedulerState.WorkspaceByteCount,
                CorridorResolveCount = schedulerState.CumulativeCorridorResolveCount,
                TargetRecordBuildCount = schedulerState.CumulativeTargetRecordBuildCount,
                CoverageTileInvalidationCount =
                    schedulerState.CumulativeCoverageTileInvalidationCount,
                CoverageTileBuildCount = schedulerState.CumulativeCoverageTileBuildCount,
                CoverageTileReuseCount = schedulerState.CumulativeCoverageTileReuseCount,
                BudgetThrottleCount = schedulerState.CumulativeBudgetThrottleCount,
                LastBuildBatchMilliseconds = schedulerState.LastBuildBatchMilliseconds,
                MaximumBuildBatchMilliseconds = schedulerState.MaximumBuildBatchMilliseconds,
            };
        }

        private static int GetNearestRank(int[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 0)
            {
                return 0;
            }
            int index = math.clamp(
                (int)math.ceil((float)(sortedValues.Length * percentile)) - 1,
                0,
                sortedValues.Length - 1);
            return sortedValues[index];
        }

        private static ulong MixFinalPositionHash(ulong hash, int value)
        {
            hash ^= unchecked((uint)value);
            return hash * 1099511628211UL;
        }

        private static NavigationGridMovementUnsettledAgentReport BuildUnsettledAgentReport(
            EntityManager entityManager,
            ref NavigationGridBlob grid,
            LocalTransform transform,
            AniMovementResult result,
            NavigationGridMovementBenchmarkAni benchmarkAni,
            AniGoalAssignment goal,
            AniMovementCohortMembership membership,
            AniMovementConfig movementConfig)
        {
            bool hasCurrentCell = NavigationGridQuery.TryWorldToCell(
                ref grid,
                transform.Position,
                out _,
                out int currentCellIndex);
            bool canApproachDirectly = hasCurrentCell &&
                                       NavigationGridQuery.TryCalculateLineCost(
                                           ref grid,
                                           currentCellIndex,
                                           goal.TargetCellIndex,
                                           movementConfig.AgentRadius,
                                           0.05f,
                                           0.2f,
                                           out _);
            Entity cohortEntity = membership.Cohort;
            NavigationFlowFieldState fieldState =
                entityManager.HasComponent<NavigationFlowFieldState>(cohortEntity)
                    ? entityManager.GetComponentData<NavigationFlowFieldState>(cohortEntity)
                    : default;
            Entity fieldEntity = entityManager.HasComponent<NavigationFlowFieldHandle>(cohortEntity)
                ? entityManager.GetComponentData<NavigationFlowFieldHandle>(cohortEntity).Record
                : cohortEntity;
            bool hasField = fieldEntity != Entity.Null &&
                            entityManager.HasBuffer<NavigationFlowFieldCell>(fieldEntity);
            DynamicBuffer<NavigationFlowFieldCell> field = hasField
                ? entityManager.GetBuffer<NavigationFlowFieldCell>(fieldEntity, true)
                : default;
            bool currentCellInField = hasCurrentCell && hasField &&
                                      AniMovementCohortAlgorithms.TryGetFlowDirection(
                                          field,
                                          currentCellIndex,
                                          out _);

            // 失败报告只保留未到达成员的最终快照，不会污染正式计时窗口
            return new NavigationGridMovementUnsettledAgentReport
            {
                AgentIndex = benchmarkAni.AgentIndex,
                CohortId = membership.CohortId,
                Position = transform.Position,
                TargetPosition = goal.TargetPosition,
                AppliedVelocity = result.AppliedVelocity,
                DistanceToTarget = result.DistanceToSlot,
                CurrentCellIndex = hasCurrentCell ? currentCellIndex : -1,
                TargetCellIndex = goal.TargetCellIndex,
                ProjectedStartCellIndex = fieldState.ProjectedStartCellIndex,
                ProjectedEndCellIndex = fieldState.ProjectedEndCellIndex,
                FieldCellCount = hasField ? field.Length : 0,
                CurrentCellInField = currentCellInField,
                CanApproachDirectly = canApproachDirectly,
            };
        }

        private NavigationGridMovementBenchmarkStateTraceReport[] BuildStateTraceReport(
            bool enabled)
        {
            if (!enabled)
            {
                return Array.Empty<NavigationGridMovementBenchmarkStateTraceReport>();
            }

            DynamicBuffer<NavigationGridMovementBenchmarkStateTrace> trace =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkStateTrace>();
            var report = new NavigationGridMovementBenchmarkStateTraceReport[trace.Length];
            for (int index = 0; index < trace.Length; index++)
            {
                // 轨迹缓冲区只保存普通值，导出时不再依赖 Entity 或 Native 容器的生命周期
                NavigationGridMovementBenchmarkStateTrace sample = trace[index];
                report[index] = new NavigationGridMovementBenchmarkStateTraceReport
                {
                    Tick = sample.Tick,
                    SquadId = sample.SquadId,
                    PathStatus = sample.PathStatus,
                    BenchmarkFailed = sample.BenchmarkFailed != 0,
                    ActiveRequestVersion = sample.ActiveRequestVersion,
                    SettledTicks = sample.SettledTicks,
                    MemberCount = sample.MemberCount,
                    AssignedSlotCount = sample.AssignedSlotCount,
                    InvalidSlotCount = sample.InvalidSlotCount,
                    ArrivedCount = sample.ArrivedCount,
                    AnchorArrived = sample.AnchorArrived != 0,
                    MembersArrived = sample.MembersArrived != 0,
                    AnchorDistanceToTarget = sample.AnchorDistanceToTarget,
                    MaximumMemberDistance = sample.MaximumMemberDistance,
                    MaximumMemberSpeedSquared = sample.MaximumMemberSpeedSquared,
                    TransformWriteCount = sample.TransformWriteCount,
                    TargetPosition = sample.TargetPosition,
                    AnchorPosition = sample.AnchorPosition,
                    AnchorVelocity = sample.AnchorVelocity,
                    AnchorCellIndex = sample.AnchorCellIndex,
                };
            }

            Array.Sort(
                report,
                (left, right) => left.Tick != right.Tick
                    ? left.Tick.CompareTo(right.Tick)
                    : left.SquadId.CompareTo(right.SquadId));
            return report;
        }

        private NavigationGridMovementBenchmarkAgentTraceReport[] BuildAgentTraceReport(
            bool enabled)
        {
            if (!enabled)
            {
                return Array.Empty<NavigationGridMovementBenchmarkAgentTraceReport>();
            }

            DynamicBuffer<NavigationGridMovementBenchmarkAgentTrace> trace =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkAgentTrace>();
            var report = new NavigationGridMovementBenchmarkAgentTraceReport[trace.Length];
            for (int index = 0; index < trace.Length; index++)
            {
                // 成员轨迹按 AgentIndex 排序，便于跨运行比较同一成员
                NavigationGridMovementBenchmarkAgentTrace sample = trace[index];
                report[index] = new NavigationGridMovementBenchmarkAgentTraceReport
                {
                    Tick = sample.Tick,
                    AgentIndex = sample.AgentIndex,
                    SlotIndex = sample.SlotIndex,
                    SlotTargetPosition = sample.SlotTargetPosition,
                    TransformPosition = sample.TransformPosition,
                    AppliedVelocity = sample.AppliedVelocity,
                    DistanceToSlot = sample.DistanceToSlot,
                    CommitCount = sample.CommitCount,
                };
            }

            Array.Sort(
                report,
                (left, right) => left.Tick != right.Tick
                    ? left.Tick.CompareTo(right.Tick)
                    : left.AgentIndex.CompareTo(right.AgentIndex));
            return report;
        }

        private void FailBenchmark(
            ref SystemState state,
            Entity benchmarkEntity,
            string reason)
        {
            // 失败时仍导出已经采集的样本，便于定位问题发生在哪一帧
            NavigationGridMovementBenchmarkState benchmarkState =
                state.EntityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                    benchmarkEntity);

            // 同时写入 Failed 和 Completed，让验证器读取原因后立即停止等待
            benchmarkState.Failed = 1;
            benchmarkState.Completed = 1;
            benchmarkState.FailureReason = new FixedString128Bytes(reason);
            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
            // Editor Play Mode 中 Application.Quit 不一定关闭编辑器，因此先写失败报告供运行器收尾
            if (benchmarkState.ResultExported == 0)
            {
                // 先写报告再禁用系统，批处理运行器才能可靠读取失败原因
                ExportResult(
                    ref state,
                    SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>(),
                    benchmarkState);
                benchmarkState.ResultExported = 1;
                state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
            }
            Debug.LogError($"[NavigationBenchmark] {reason}");
            state.Enabled = false;
            if (Application.isBatchMode)
            {
                // Batchmode 失败时返回非零退出码，编辑器手动运行只停止当前基准系统
                Application.Quit(1);
            }
        }

        [Serializable]
        private sealed class NavigationGridMovementBenchmarkReport
        {
            public int FormatVersion;
            public string Backend;
            public string Workload;
            public string Scenario;
            public bool PerformanceGateEligible;
            public string BudgetVersion;
            public string SystemTimingCoverage;
            public bool WorkerTimingAvailable;
            public double NavigationWorkerCriticalPathP95Milliseconds;
            public NavigationStageTimingReport[] StageTimings;
            public bool RequestQueueTimingAvailable;
            public long TrackedNativeBytes;
            public long FieldStoreNativeBytes;
            public long FieldWorkspaceNativeBytes;
            public int FieldQueueLength;
            public int FieldQueueWaitP50Ticks;
            public int FieldQueueWaitP95Ticks;
            public int FieldQueueWaitP99Ticks;
            public int FieldCancelledCount;
            public int FieldTimeoutCount;
            public int UniqueFieldBuildCount;
            public int SharedFieldHitCount;
            public int SharedFieldRecordCount;
            public int CorridorResolveCount;
            public int TargetFlowRecordBuildCount;
            public int CoverageTileInvalidationCount;
            public int CoverageTileBuildCount;
            public int CoverageTileReuseCount;
            public int FieldBudgetThrottleCount;
            public double LastFieldBuildBatchMilliseconds;
            public double MaximumFieldBuildBatchMilliseconds;
            public int AgentCount;
            public int RandomSeed;
            public int WarmupTicks;
            public int SampleTicks;
            public bool MovementTraceRecorded;
            // 诊断轨迹为空数组表示本次运行没有开启逐帧额外采样
            public NavigationGridMovementBenchmarkStateTraceReport[] StateTrace;
            public NavigationGridMovementBenchmarkAgentTraceReport[] AgentTrace;
            public int FirstCompletionTick;
            // 结束帧由回放输入窗口和固定的站稳等待上限共同决定
            public int TerminationTick;
            public bool Failed;
            public string FailureReason;
            public int AppliedCommandCount;
            public int SquadCount;
            public int CohortCount;
            public int PathRequestCount;
            public int PathSuccessCount;
            public int PathFailureCount;
            public int CacheHitCount;
            public int DirectRouteCount;
            public int ArrivedCount;
            public int UnsettledAgentCount;
            public int MinimumUnsettledAgentIndex;
            public int MaximumUnsettledAgentIndex;
            public float MaximumTargetDistance;
            public NavigationGridMovementUnsettledAgentReport[] UnsettledAgents;
            public string FinalPositionHash;
            public float ArrivalRate;
            public float MinimumUnitSpacing;
            public float AverageFormationError;
            public long TransformWriteCount;
            public uint MinimumCommitCount;
            public uint MaximumCommitCount;
            public bool TransformWriteCountMatches;
            public int AwaitingCohortCount;
            public int MovingCohortCount;
            public int HoldingCohortCount;
            public int CompletedCohortCount;
            public int FailedCohortCount;
            public double ServerTickP50Milliseconds;
            public double ServerTickP95Milliseconds;
            public double ServerTickP99Milliseconds;
            public double ServerTickMaxMilliseconds;
            public double MainThreadAllocP50Bytes;
            public double MainThreadAllocP95Bytes;
            public double MainThreadAllocP99Bytes;
            public string GitCommit;
            public string UnityVersion;
            public string EntitiesAssemblyVersion;
            public string Platform;
            public string OperatingSystem;
            public string Processor;
            public int ProcessorCount;
            public int SystemMemoryMegabytes;
            public string GraphicsDevice;
            public string MapSceneHash;
            public string ReplayScriptHash;
            public string GridBakeHash;
            public string TimestampUtc;
            public double[] TickMilliseconds;
            public long[] MainThreadAllocatedBytes;
            public string Notes;
        }

        [Serializable]
        private sealed class NavigationStageTimingReport
        {
            public string Stage;
            public int SampleCount;
            public double P50Milliseconds;
            public double P95Milliseconds;
            public double P99Milliseconds;
            public double MaximumMilliseconds;
        }

        private struct FlowSchedulerReport
        {
            public bool Available;
            public int SampleCount;
            public int QueueLength;
            public int WaitP50Ticks;
            public int WaitP95Ticks;
            public int WaitP99Ticks;
            public int CancelledCount;
            public int TimeoutCount;
            public int UniqueBuildCount;
            public int SharedHitCount;
            public int StoreRecordCount;
            public long StoreByteCount;
            public long WorkspaceByteCount;
            public int CorridorResolveCount;
            public int TargetRecordBuildCount;
            public int CoverageTileInvalidationCount;
            public int CoverageTileBuildCount;
            public int CoverageTileReuseCount;
            public int BudgetThrottleCount;
            public double LastBuildBatchMilliseconds;
            public double MaximumBuildBatchMilliseconds;
        }

        private struct FinalAgentSample
        {
            public int AgentIndex;
            public float3 Position;
        }

        [Serializable]
        private sealed class NavigationGridMovementUnsettledAgentReport
        {
            public int AgentIndex;
            public uint CohortId;
            public float3 Position;
            public float3 TargetPosition;
            public float3 AppliedVelocity;
            public float DistanceToTarget;
            public int CurrentCellIndex;
            public int TargetCellIndex;
            public int ProjectedStartCellIndex;
            public int ProjectedEndCellIndex;
            public int FieldCellCount;
            public bool CurrentCellInField;
            public bool CanApproachDirectly;
        }

        [Serializable]
        private sealed class NavigationGridMovementBenchmarkStateTraceReport
        {
            public int Tick;
            public uint SquadId;
            public byte PathStatus;
            public bool BenchmarkFailed;
            public uint ActiveRequestVersion;
            public int SettledTicks;
            public int MemberCount;
            public int AssignedSlotCount;
            public int InvalidSlotCount;
            public int ArrivedCount;
            public bool AnchorArrived;
            public bool MembersArrived;
            public float AnchorDistanceToTarget;
            public float MaximumMemberDistance;
            public float MaximumMemberSpeedSquared;
            public long TransformWriteCount;
            public float3 TargetPosition;
            public float3 AnchorPosition;
            public float3 AnchorVelocity;
            public int AnchorCellIndex;
        }

        [Serializable]
        private sealed class NavigationGridMovementBenchmarkAgentTraceReport
        {
            public int Tick;
            public int AgentIndex;
            public int SlotIndex;
            public float3 SlotTargetPosition;
            public float3 TransformPosition;
            public float3 AppliedVelocity;
            public float DistanceToSlot;
            public uint CommitCount;
        }
    }

    /// <summary>
    /// 在一帧完整服务器模拟开始时记录时间和托管内存基线
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ServerNavigationGridMovementTimingStartSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            if (!NavigationGridBenchmarkScaleProfile.RecordsFullServerTick(config.Workload))
            {
                return;
            }

            RefRW<NavigationGridMovementBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridMovementBenchmarkState>();
            int sampleTick = benchmarkState.ValueRO.Tick - config.WarmupTicks;

            // 只在初始化完成且处于正式窗口时记录，预热帧不计入性能结果
            bool shouldRecord = benchmarkState.ValueRO.Initialized != 0 &&
                                sampleTick >= 0 &&
                                sampleTick < config.SampleTicks;
            benchmarkState.ValueRW.RecordCurrentTick = (byte)(shouldRecord ? 1 : 0);
            if (!shouldRecord)
            {
                return;
            }

            benchmarkState.ValueRW.FrameStartTimestamp = Stopwatch.GetTimestamp();

            // 使用同一线程的分配增量，比全局 GC 计数更接近本帧实际开销
            benchmarkState.ValueRW.FrameStartAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread();
        }
    }

    /// <summary>
    /// 在一帧完整服务器模拟结束时记录耗时和托管内存增量
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct ServerNavigationGridMovementTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            if (!NavigationGridBenchmarkScaleProfile.RecordsFullServerTick(config.Workload))
            {
                return;
            }

            RefRW<NavigationGridMovementBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridMovementBenchmarkState>();
            bool recordTiming = benchmarkState.ValueRO.RecordCurrentTick != 0;
            int traceEndTick = config.WarmupTicks + config.SampleTicks +
                               ServerNavigationGridMovementBenchmarkSystem.MaximumSettlementTicks;
            bool recordTrace =
                               config.Workload ==
                               NavigationGridBenchmarkWorkload.StrictFormationBaseline &&
                               config.RecordMovementTrace != 0 &&
                               benchmarkState.ValueRO.Initialized != 0 &&
                               benchmarkState.ValueRO.ResultExported == 0 &&
                               benchmarkState.ValueRO.Tick > 0 &&
                               benchmarkState.ValueRO.Tick <= traceEndTick;
            if (!recordTiming && !recordTrace)
            {
                return;
            }

            if (recordTrace)
            {
                RecordMovementTrace(ref state, benchmarkState.ValueRO);
            }

            if (!recordTiming)
            {
                return;
            }

            long elapsedTimestamp =
                Stopwatch.GetTimestamp() - benchmarkState.ValueRO.FrameStartTimestamp;
            // 计时和内存分配采样必须来自同一线程，避免混入其他线程开销
            long allocatedBytes = Math.Max(
                0L,
                GC.GetAllocatedBytesForCurrentThread() -
                benchmarkState.ValueRO.FrameStartAllocatedBytes);

            // 每帧结束时只追加原始样本，百分位留到导出报告时计算
            // 写入后清除 RecordCurrentTick，保证每次开始采样只对应一次结束
            DynamicBuffer<NavigationGridMovementBenchmarkTimingSample> samples =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkTimingSample>();
            samples.Add(new NavigationGridMovementBenchmarkTimingSample
            {
                ServerSimulationMilliseconds =
                    elapsedTimestamp * 1000.0 / Stopwatch.Frequency,
                MainThreadAllocatedBytes = allocatedBytes,
            });
            benchmarkState.ValueRW.RecordCurrentTick = 0;
        }

        private void RecordMovementTrace(
            ref SystemState state,
            NavigationGridMovementBenchmarkState benchmarkState)
        {
            int tick = benchmarkState.Tick;
            DynamicBuffer<NavigationGridMovementBenchmarkStateTrace> stateTrace =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkStateTrace>();
            int squadCount = 0;

            foreach (var (squad, command, pathState, anchor, members) in
                     SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadCommand>,
                         RefRO<AniSquadPathState>,
                         RefRO<AniSquadAnchor>,
                         DynamicBuffer<AniSquadMember>>())
            {
                // 当前回放只创建一支队伍，槽位索引越界会直接记录到诊断结果
                if (squadCount++ > 0)
                {
                    continue;
                }

                int assignedSlotCount = 0;
                int invalidSlotCount = 0;
                long transformWriteCount = 0;
                int arrivedCount = 0;
                float maximumMemberDistance = 0f;
                float maximumMemberSpeedSquared = 0f;

                // 诊断统计使用与到达判断相同的位置和速度阈值，保证两者定义一致
                for (int index = 0; index < members.Length; index++)
                {
                    int slotIndex = members[index].SlotIndex;
                    if (slotIndex >= 0 && slotIndex < members.Length)
                    {
                        assignedSlotCount++;
                    }
                    else
                    {
                        invalidSlotCount++;
                    }

                    Entity aniEntity = members[index].Ani;
                    if (!state.EntityManager.HasComponent<AniMovementResult>(aniEntity))
                    {
                        continue;
                    }

                    AniMovementResult result =
                        state.EntityManager.GetComponentData<AniMovementResult>(aniEntity);
                    transformWriteCount += result.CommitCount;
                    maximumMemberDistance = math.max(
                        maximumMemberDistance,
                        result.DistanceToSlot);
                    maximumMemberSpeedSquared = math.max(
                        maximumMemberSpeedSquared,
                        math.lengthsq(result.AppliedVelocity));
                    if (state.EntityManager.HasComponent<AniMovementConfig>(aniEntity))
                    {
                        AniMovementConfig movementConfig =
                            state.EntityManager.GetComponentData<AniMovementConfig>(aniEntity);
                        if (result.DistanceToSlot <= movementConfig.ArrivalRadius &&
                            math.lengthsq(result.AppliedVelocity) <= 0.0225f)
                        {
                            arrivedCount++;
                        }
                    }
                }

                // 锚点和成员状态分开记录，可以看出队伍中心已到目标但成员尚未站稳的情况
                float anchorDistanceToTarget = math.length(
                    PlanarMath.FlattenY(
                        pathState.ValueRO.ResolvedTargetPosition - anchor.ValueRO.Position));
                bool anchorArrived = anchorDistanceToTarget <=
                                     math.max(0.1f, command.ValueRO.TargetStoppingDistance);
                bool membersArrived = arrivedCount == members.Length && members.Length > 0;

                stateTrace.Add(new NavigationGridMovementBenchmarkStateTrace
                {
                    Tick = tick,
                    SquadId = squad.ValueRO.SquadId,
                    PathStatus = (byte)pathState.ValueRO.Status,
                    BenchmarkFailed = benchmarkState.Failed,
                    ActiveRequestVersion = pathState.ValueRO.ActiveRequestVersion,
                    SettledTicks = pathState.ValueRO.SettledTicks,
                    MemberCount = members.Length,
                    AssignedSlotCount = assignedSlotCount,
                    InvalidSlotCount = invalidSlotCount,
                    ArrivedCount = arrivedCount,
                    AnchorArrived = (byte)(anchorArrived ? 1 : 0),
                    MembersArrived = (byte)(membersArrived ? 1 : 0),
                    AnchorDistanceToTarget = anchorDistanceToTarget,
                    MaximumMemberDistance = maximumMemberDistance,
                    MaximumMemberSpeedSquared = maximumMemberSpeedSquared,
                    TransformWriteCount = transformWriteCount,
                    TargetPosition = pathState.ValueRO.ResolvedTargetPosition,
                    AnchorPosition = anchor.ValueRO.Position,
                    AnchorVelocity = anchor.ValueRO.Velocity,
                    AnchorCellIndex = anchor.ValueRO.CurrentCellIndex,
                });
            }

            if (squadCount == 0)
            {
                stateTrace.Add(new NavigationGridMovementBenchmarkStateTrace
                {
                    Tick = tick,
                    BenchmarkFailed = benchmarkState.Failed,
                });
            }

            DynamicBuffer<NavigationGridMovementBenchmarkAgentTrace> agentTrace =
                SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkAgentTrace>();
            foreach (var (benchmarkAni, membership, slotTarget, transform, result) in
                     SystemAPI.Query<
                         RefRO<NavigationGridMovementBenchmarkAni>,
                         RefRO<AniSquadMembership>,
                         RefRO<AniSlotTarget>,
                         RefRO<LocalTransform>,
                         RefRO<AniMovementResult>>())
            {
                // AgentIndex 是成员轨迹的固定排序键，Entity 查询顺序不用于比较回放结果
                agentTrace.Add(new NavigationGridMovementBenchmarkAgentTrace
                {
                    Tick = tick,
                    AgentIndex = benchmarkAni.ValueRO.AgentIndex,
                    SlotIndex = membership.ValueRO.SlotIndex,
                    SlotTargetPosition = slotTarget.ValueRO.Position,
                    TransformPosition = transform.ValueRO.Position,
                    AppliedVelocity = result.ValueRO.AppliedVelocity,
                    DistanceToSlot = result.ValueRO.DistanceToSlot,
                    CommitCount = result.ValueRO.CommitCount,
                });
            }
        }
    }
}
