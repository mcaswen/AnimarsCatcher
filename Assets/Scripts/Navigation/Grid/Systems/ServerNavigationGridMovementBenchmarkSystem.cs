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
    /// 把统一 Benchmark 回放适配为阶段四 Squad 移动指令
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridBenchmarkSystem))]
    public partial struct ServerNavigationGridMovementBenchmarkSystem : ISystem
    {
        private const float AgentMaximumSpeed = 8f;
        private const float AgentMaximumAcceleration = 32f;
        private const int FormationColumnCount = 8;
        private const int MaximumSettlementTicks = 600;

        private EntityQuery _agentQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            state.RequireForUpdate<NavigationGridReference>();

            // 查询只保存 Benchmark 标记和 Transform，成员组件会在指令消费时结构化添加
            // 查询本身由 SystemState 持有，不在 OnDestroy 手工释放
            _agentQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridMovementBenchmarkAni>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            if (config.Workload != NavigationGridBenchmarkWorkload.SquadMovement)
            {
                // PathAndField 工作负载由旧系统处理，两个 Benchmark 入口互不消费状态
                // 共享配置实体允许两个 System 同时注册而不互相推进 Tick
                return;
            }

            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<NavigationGridBenchmarkConfig>();
            NavigationGridMovementBenchmarkState benchmarkState =
                SystemAPI.GetSingleton<NavigationGridMovementBenchmarkState>();
            if (benchmarkState.Completed != 0)
            {
                if (benchmarkState.ResultExported == 0)
                {
                    // 结果只导出一次，避免后续 Tick 覆盖同一时间戳文件
                    ExportResult(ref state, config, benchmarkState);
                    benchmarkState.ResultExported = 1;
                    state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
                    // 导出完成后禁用 System，避免相同状态重复写文件
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
                    // 没有回放命令时无法定义目标，直接生成失败结果并停止系统
                    FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动 Benchmark 命令回放为空");
                    return;
                }

                CreateAgents(ref state, config);

                // 先写回初始化状态，再创建指令，防止结构变更后下一 Tick 重复生成成员
                // 该写回必须早于第一个 SubmitCommand 产生的指令实体
                benchmarkState.Initialized = 1;
                benchmarkState.NextCommandSequence = 1;
                state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
                Debug.Log($"[NavigationBenchmark] 已创建 {config.AgentCount} 个 Grid Benchmark Ani");
            }

            // 创建 Ani 会改变 Archetype，复制 Buffer 后再进行指令结构变更
            // NativeArray 副本的生命周期覆盖整个命令提交循环
            using NativeArray<NavigationGridBenchmarkCommand> commands =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true)
                    .ToNativeArray(Allocator.Temp);
            int sampleTick = benchmarkState.Tick - config.WarmupTicks;
            // Replay Tick 从 Warmup 归零，命令时间不会受启动预热影响
            while (sampleTick >= 0 &&
                   benchmarkState.NextCommandIndex < commands.Length &&
                   commands[benchmarkState.NextCommandIndex].Tick == sampleTick)
            {
                // 一个回放 Tick 可以包含多个命令，按原序号逐个转入 Squad 指令
                SubmitCommand(
                    ref state,
                    config,
                    commands[benchmarkState.NextCommandIndex],
                    ref benchmarkState);
                benchmarkState.NextCommandIndex++;
            }

            benchmarkState.Tick++;
            int sampleEndTick = config.WarmupTicks + config.SampleTicks;
            // 采样计数只负责结束窗口，实际完成还需等待 Squad 状态稳定
            // Tick 先递增再比较，使 Tick=0 的命令包含在采样窗口
            if (benchmarkState.Tick >= sampleEndTick)
            {
                // 采样窗口结束后仍等待 Anchor 和成员进入稳定态，避免只统计路径完成
                // 最长等待由 MaximumSettlementTicks 限制，超时按功能失败处理
                if (TryGetTerminalState(ref state, out bool failed))
                {
                    if (failed)
                    {
                        // 终态失败必须保留原因并停止后续写入
                        FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动出现失败的 Squad 路径");
                        return;
                    }

                    benchmarkState.Completed = 1;
                }
                else if (benchmarkState.Tick >= sampleEndTick + MaximumSettlementTicks)
                {
                    // 收敛超时说明移动链路卡住，不能把未完成结果当作性能样本
                    FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动在收敛期限内未完成");
                    return;
                }
            }

            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
        }

        private static void CreateAgents(
            ref SystemState state,
            NavigationGridBenchmarkConfig config)
        {
            int count = math.max(1, config.AgentCount);

            // 位置由共享输入算法生成，保证 Legacy 与 Grid 使用完全相同的起点
            // count 的下限保证零 Ani 配置仍能产生可诊断的最小夹具
            for (int agentIndex = 0; agentIndex < count; agentIndex++)
            {
                float3 position = NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                    agentIndex,
                    count,
                    config.SpawnColumnCount,
                    config.SpawnSpacing,
                    config.SpawnOrigin,
                    config.RandomSeed);
                Entity aniEntity = state.EntityManager.CreateEntity(
                    typeof(LocalTransform),
                    typeof(NavigationGridMovementBenchmarkAni));
                // Transform 先写入确定性起点，指令消费后才添加移动组件
                state.EntityManager.SetComponentData(
                    aniEntity,
                    LocalTransform.FromPositionRotation(position, quaternion.identity));
                state.EntityManager.SetComponentData(
                    aniEntity,
                    // AgentIndex 是跨查询稳定键，不能改用 Entity.Index
                    new NavigationGridMovementBenchmarkAni { AgentIndex = agentIndex });
            }
        }

        private void SubmitCommand(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkCommand command,
            ref NavigationGridMovementBenchmarkState benchmarkState)
        {
            EntityManager entityManager = state.EntityManager;
            using NativeArray<Entity> agents = GetSortedAgents(ref state);
            if (agents.Length < config.AgentCount)
            {
                // 少于配置数量会改变回放负载，必须终止而不能静默降档
                throw new InvalidOperationException(
                    $"Grid Benchmark Ani 数量不足，期望 {config.AgentCount}，实际 {agents.Length}");
            }

            float3 targetPosition = config.SpawnOrigin + command.TargetOffset;

            // Benchmark 只提交 MoveTo，Forward 由起点到目标的水平投影确定
            float3 forward = targetPosition - config.SpawnOrigin;
            forward = PlanarMath.NormalizeXZOrDefault(
                forward,
                new float3(0f, 0f, 1f));

            Entity commandEntity = entityManager.CreateEntity(
                typeof(AniSquadCommandRequest),
                typeof(AniSquadCommand));
            // Request Tag 与 Command 同时创建，生命周期查询不会看到半成品指令
            entityManager.SetComponentData(commandEntity, new AniSquadCommand
            {
                Sequence = NextCommandSequence(ref benchmarkState),
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

            // 成员按 AgentIndex 排序写入，确保回放跨 World 的槽位分配一致
            for (int index = 0; index < config.AgentCount; index++)
            {
                int stableId = entityManager
                    .GetComponentData<NavigationGridMovementBenchmarkAni>(agents[index])
                    .AgentIndex;
                members.Add(new AniSquadCommandMember
                {
                    Ani = agents[index],
                    StableId = stableId,
                    MaxSpeed = AgentMaximumSpeed,
                    MaxAcceleration = AgentMaximumAcceleration,
                    AgentRadius = config.AgentRadius,
                });
            }

            // AppliedCommandCount 与回放命令一一对应，不按成员数量放大
            benchmarkState.AppliedCommandCount++;
        }

        private NativeArray<Entity> GetSortedAgents(ref SystemState state)
        {
            NativeArray<Entity> agents = _agentQuery.ToEntityArray(Allocator.Temp);

            // 查询顺序受 Archetype 和创建时机影响，不能直接作为稳定成员顺序
            for (int index = 1; index < agents.Length; index++)
            {
                Entity value = agents[index];
                int valueIndex = state.EntityManager
                    .GetComponentData<NavigationGridMovementBenchmarkAni>(value)
                    .AgentIndex;
                int insertion = index - 1;
                while (insertion >= 0 &&
                       state.EntityManager
                           .GetComponentData<NavigationGridMovementBenchmarkAni>(agents[insertion])
                           .AgentIndex > valueIndex)
                {
                    agents[insertion + 1] = agents[insertion];
                    insertion--;
                }

                agents[insertion + 1] = value;
            }

            // 返回临时数组的所有权给 SubmitCommand，调用方在指令完成后释放
            return agents;
        }

        private bool TryGetTerminalState(ref SystemState state, out bool failed)
        {
            failed = false;
            bool foundSquad = false;

            // 所有 Squad 都必须进入完成或 Follow Holding，任一失败立即终止
            // 当前夹具只创建一队，但扫描结构支持将来多队伍回放
            foreach (RefRO<AniSquadPathState> pathState in
                     SystemAPI.Query<RefRO<AniSquadPathState>>())
            {
                foundSquad = true;
                if (pathState.ValueRO.Status == AniSquadMovementStatus.Failed)
                {
                    // 发现任一失败 Squad 即可结束检查，不等待其他 Squad
                    failed = true;
                    return true;
                }

                if (pathState.ValueRO.Status != AniSquadMovementStatus.Completed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Holding)
                {
                    // 仍有活动 Squad，继续收敛而不是提前导出部分结果
                    return false;
                }
            }

            // 没有生成任何 Squad 时不能视为成功终态
            return foundSquad;
        }

        private static uint NextCommandSequence(
            ref NavigationGridMovementBenchmarkState benchmarkState)
        {
            uint sequence = benchmarkState.NextCommandSequence++;

            // 零保留给未初始化序号，计数器环绕时跳过零
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

            // 从成员结果聚合到达率、间距和阵型误差，避免再次遍历 Squad Buffer
            // 统计只选择带 Benchmark 标记的 Ani，不混入场景中的正式单位
            foreach (var (transform, movementConfig, result) in
                     SystemAPI.Query<
                             RefRO<LocalTransform>,
                             RefRO<AniMovementConfig>,
                             RefRO<AniMovementResult>>()
                         .WithAll<NavigationGridMovementBenchmarkAni>())
            {
                positions.Add(transform.ValueRO.Position);
                transformWriteCount += result.ValueRO.CommitCount;
                totalFormationError += result.ValueRO.DistanceToSlot;
                if (result.ValueRO.DistanceToSlot <= movementConfig.ValueRO.ArrivalRadius &&
                    math.lengthsq(result.ValueRO.AppliedVelocity) <= 0.0225f)
                {
                    // 到达率同时要求位置和速度满足门限，单纯穿过目标不算完成
                    arrivedCount++;
                }
            }

            // 路径请求统计按 Squad 汇总，验证一条指令只创建一个上下文
            int squadCount = 0;
            int pathRequestCount = 0;
            int pathSuccessCount = 0;
            int pathFailureCount = 0;
            int cacheHitCount = 0;
            // PathState 只存在于 Squad 实体，因此这些计数天然按队伍聚合
            foreach (RefRO<AniSquadPathState> pathState in
                     SystemAPI.Query<RefRO<AniSquadPathState>>())
            {
                squadCount++;
                pathRequestCount += pathState.ValueRO.FieldRequestCount;
                pathSuccessCount += pathState.ValueRO.SuccessfulFieldRequestCount;
                pathFailureCount += pathState.ValueRO.FailedFieldRequestCount;
                cacheHitCount += pathState.ValueRO.CacheHitCount;
            }

            float minimumUnitSpacing = StatisticsMath.CalculateMinimumPairwiseDistance(positions);

            // Tick 样本复制后排序，原始顺序仍保留在 JSON 供回放分析
            // 排序副本用于百分位，原数组维持发生顺序用于尖峰定位
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

            // 百分位和原始样本同时写出，避免只看平均值掩盖长尾 Tick
            var report = new NavigationGridMovementBenchmarkReport
            {
                // FormatVersion 用于后续字段扩展时区分兼容解析方式
                // 报告字段保持纯值，避免序列化 Entity 或 NativeContainer
                FormatVersion = 1,
                Backend = AniMovementBackend.ClearanceGrid.ToString(),
                Workload = NavigationGridBenchmarkWorkload.SquadMovement.ToString(),
                AgentCount = config.AgentCount,
                RandomSeed = config.RandomSeed,
                WarmupTicks = config.WarmupTicks,
                SampleTicks = config.SampleTicks,
                AppliedCommandCount = benchmarkState.AppliedCommandCount,
                SquadCount = squadCount,
                PathRequestCount = pathRequestCount,
                PathSuccessCount = pathSuccessCount,
                PathFailureCount = pathFailureCount,
                CacheHitCount = cacheHitCount,
                ArrivedCount = arrivedCount,
                ArrivalRate = config.AgentCount == 0
                    ? 0f
                    : (float)arrivedCount / config.AgentCount,
                MinimumUnitSpacing = minimumUnitSpacing,
                AverageFormationError = positions.Count == 0
                    ? 0f
                    : totalFormationError / positions.Count,
                TransformWriteCount = transformWriteCount,
                ServerTickP50Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.50),
                ServerTickP95Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.95),
                ServerTickP99Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTickMilliseconds, 0.99),
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
                Notes = "阶段四开阔地移动，不包含 ORCA、世界碰撞或受阻恢复",
            };

            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");

            // 结果目录不属于 Unity 资产，使用时间戳区分同一提交的多次测量
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"GridNavigation_{config.AgentCount}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            // 文件名带 Ani 数量和 UTC 时间，便于跨后端结果并列归档
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log(
                $"[NavigationBenchmark] Grid 群体移动结果已生成：" +
                $"到达={arrivedCount}/{config.AgentCount}，结果={path}");
        }

        private static void FailBenchmark(
            ref SystemState state,
            Entity benchmarkEntity,
            string reason)
        {
            NavigationGridMovementBenchmarkState benchmarkState =
                state.EntityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                    benchmarkEntity);

            // Failed 与 Completed 同时写入，让验证器能读取失败原因后停止等待
            benchmarkState.Failed = 1;
            benchmarkState.Completed = 1;
            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
            Debug.LogError($"[NavigationBenchmark] {reason}");
            state.Enabled = false;
            if (Application.isBatchMode)
            {
                // Batchmode 需要非零退出码，Editor 手工运行则只停止当前 Benchmark System
                Application.Quit(1);
            }
        }

        [Serializable]
        private sealed class NavigationGridMovementBenchmarkReport
        {
            public int FormatVersion;
            public string Backend;
            public string Workload;
            public int AgentCount;
            public int RandomSeed;
            public int WarmupTicks;
            public int SampleTicks;
            public int AppliedCommandCount;
            public int SquadCount;
            public int PathRequestCount;
            public int PathSuccessCount;
            public int PathFailureCount;
            public int CacheHitCount;
            public int ArrivedCount;
            public float ArrivalRate;
            public float MinimumUnitSpacing;
            public float AverageFormationError;
            public long TransformWriteCount;
            public double ServerTickP50Milliseconds;
            public double ServerTickP95Milliseconds;
            public double ServerTickP99Milliseconds;
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
    }

    /// <summary>
    /// 在完整 Server Simulation Tick 起点记录阶段四样本
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
            if (config.Workload != NavigationGridBenchmarkWorkload.SquadMovement)
            {
                return;
            }

            RefRW<NavigationGridMovementBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridMovementBenchmarkState>();
            int sampleTick = benchmarkState.ValueRO.Tick - config.WarmupTicks;

            // 只在初始化后且处于采样窗口记录，Warmup 不混入性能统计
            bool shouldRecord = benchmarkState.ValueRO.Initialized != 0 &&
                                sampleTick >= 0 &&
                                sampleTick < config.SampleTicks;
            benchmarkState.ValueRW.RecordCurrentTick = (byte)(shouldRecord ? 1 : 0);
            if (!shouldRecord)
            {
                return;
            }

            benchmarkState.ValueRW.FrameStartTimestamp = Stopwatch.GetTimestamp();

            // 同一线程的分配差值比全局 GC 计数更接近服务器 Tick 成本
            benchmarkState.ValueRW.FrameStartAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread();
        }
    }

    /// <summary>
    /// 在完整 Server Simulation Tick 末尾保存阶段四样本
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
            if (SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>().Workload !=
                NavigationGridBenchmarkWorkload.SquadMovement)
            {
                return;
            }

            RefRW<NavigationGridMovementBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridMovementBenchmarkState>();
            if (benchmarkState.ValueRO.RecordCurrentTick == 0)
            {
                // Start 未标记的 Tick 可能处于 Warmup 或结束状态，不能写入空样本
                return;
            }

            long elapsedTimestamp =
                Stopwatch.GetTimestamp() - benchmarkState.ValueRO.FrameStartTimestamp;
            // Stopwatch 和 GC 采样必须来自同一线程，避免把其他线程开销混入结果
            long allocatedBytes = Math.Max(
                0L,
                GC.GetAllocatedBytesForCurrentThread() -
                benchmarkState.ValueRO.FrameStartAllocatedBytes);

            // 结束采样只追加不可变样本，报告导出时再计算百分位
            // RecordCurrentTick 在写入后清零，保证每次 Start 只配对一次 End
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
    }
}
