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
    /// 按固定回放创建队伍移动指令，验证到达结果并记录完整服务器帧性能
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

        private EntityQuery _agentQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            state.RequireForUpdate<NavigationGridReference>();

            // 初始查询只要求基准标记和 Transform，队伍生命周期系统处理指令时会补齐移动组件
            // EntityQuery 由 SystemState 管理，不需要在 OnDestroy 中手动释放
            _agentQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridMovementBenchmarkAni>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            if (config.Workload != NavigationGridBenchmarkWorkload.StrictFormationBaseline)
            {
                // PathAndField 模式由另一个基准系统处理，两个入口不会同时推进同一份状态
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
                if (!TryGetTerminalState(ref state, out bool failed) || failed)
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

                CreateAgents(ref state, config);

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
            int terminationTick = sampleEndTick + MaximumSettlementTicks;
            // 采样和等待队伍站稳共用同一个服务器帧计数，不受渲染帧率影响
            // 帧数在末尾递增，确保第 0 帧提交的命令属于采样窗口
            if (benchmarkState.Tick >= sampleEndTick)
            {
                bool terminal = TryGetTerminalState(ref state, out bool failed);
                if (failed)
                {
                    // 一旦队伍进入失败状态，就保留原因并停止后续采样
                    FailBenchmark(ref state, benchmarkEntity, "Grid 群体移动出现失败的 Squad 路径");
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

        private static void CreateAgents(
            ref SystemState state,
            NavigationGridBenchmarkConfig config)
        {
            int count = math.max(1, config.AgentCount);

            // 起点由共享算法生成，保证 Legacy 与 Grid 后端使用完全相同的位置
            // 即使配置人数为 0 也至少创建一个成员，以便输出可诊断结果
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
                // 先把 Transform 放到固定起点，移动组件由队伍生命周期系统处理指令时添加
                state.EntityManager.SetComponentData(
                    aniEntity,
                    LocalTransform.FromPositionRotation(position, quaternion.identity));
                state.EntityManager.SetComponentData(
                    aniEntity,
                    // AgentIndex 用于跨运行识别同一成员，不能改用可能变化的 Entity.Index
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

            Entity commandEntity = entityManager.CreateEntity(
                typeof(AniSquadCommandRequest),
                typeof(AniSquadCommand));
            // 创建 Entity 时同时写入请求标记和指令数据，避免生命周期系统读到不完整的指令
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

            // 成员按 AgentIndex 排序加入指令，使不同 World 中的初始槽位分配一致
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

            // AppliedCommandCount 统计队伍指令数，不会按成员数重复累加
            benchmarkState.AppliedCommandCount++;
        }

        private NativeArray<Entity> GetSortedAgents(ref SystemState state)
        {
            NativeArray<Entity> agents = _agentQuery.ToEntityArray(Allocator.Temp);

            // 查询顺序会受 Archetype 和创建时机影响，不能直接用作成员固定顺序
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

            // 临时成员数组交给 SubmitCommand 使用，调用方在指令创建后负责释放
            return agents;
        }

        private bool TryGetTerminalState(ref SystemState state, out bool failed)
        {
            failed = false;
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

            // 直接从成员结果汇总到达率和阵型误差，不再重复遍历队伍缓冲区
            // 只统计带基准标记的 Ani，不混入场景中的正式单位
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
                    // 到达成员必须同时靠近槽位并基本停稳，单纯经过目标不算完成
                    arrivedCount++;
                }
            }

            // 寻路请求按队伍汇总，用于确认一条队伍指令只创建一份寻路上下文
            int squadCount = 0;
            int pathRequestCount = 0;
            int pathSuccessCount = 0;
            int pathFailureCount = 0;
            int cacheHitCount = 0;
            // PathState 只挂在队伍 Entity 上，因此这些计数本身就是按队汇总的
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

            // 同时写出百分位和原始样本，避免平均值掩盖偶发的慢帧
            var report = new NavigationGridMovementBenchmarkReport
            {
                // FormatVersion 用于以后扩展字段时选择兼容的解析方式
                // 报告只包含普通值，不序列化 Entity 或 NativeContainer
                FormatVersion = 5,
                Backend = AniMovementBackend.ClearanceGrid.ToString(),
                Workload = NavigationGridBenchmarkWorkload.StrictFormationBaseline.ToString(),
                PerformanceGateEligible = false,
                BudgetVersion = NavigationGridBenchmarkScaleProfile.BudgetVersion,
                SystemTimingCoverage = "记录完整 Server Tick，不含逐 System Worker 时间",
                WorkerTimingAvailable = false,
                RequestQueueTimingAvailable = schedulerReport.Available,
                TrackedNativeBytes = schedulerReport.Available
                    ? schedulerReport.StoreByteCount
                    : -1,
                FieldQueueLength = schedulerReport.QueueLength,
                FieldQueueWaitP50Ticks = schedulerReport.WaitP50Ticks,
                FieldQueueWaitP95Ticks = schedulerReport.WaitP95Ticks,
                FieldQueueWaitP99Ticks = schedulerReport.WaitP99Ticks,
                FieldCancelledCount = schedulerReport.CancelledCount,
                FieldTimeoutCount = schedulerReport.TimeoutCount,
                UniqueFieldBuildCount = schedulerReport.UniqueBuildCount,
                SharedFieldHitCount = schedulerReport.SharedHitCount,
                SharedFieldRecordCount = schedulerReport.StoreRecordCount,
                AgentCount = config.AgentCount,
                RandomSeed = config.RandomSeed,
                WarmupTicks = config.WarmupTicks,
                SampleTicks = config.SampleTicks,
                MovementTraceRecorded = config.RecordMovementTrace != 0,
                StateTrace = stateTrace,
                AgentTrace = agentTrace,
                FirstCompletionTick = benchmarkState.CompletionTick,
                TerminationTick = config.WarmupTicks + config.SampleTicks +
                                  MaximumSettlementTicks,
                Failed = benchmarkState.Failed != 0,
                FailureReason = benchmarkState.FailureReason.ToString(),
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
                Notes =
                    "严格矩形阵型历史基线，不包含自由 Cohort、ORCA、世界碰撞或受阻恢复",
            };

            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");

            // 结果目录不属于 Unity 资产，使用时间戳区分同一提交的多次运行
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"GridNavigation_{config.AgentCount}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            // 文件名包含 Ani 数量和 UTC 时间，便于并列归档不同后端结果
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log(
                $"[NavigationBenchmark] Grid 群体移动结果已生成：" +
                $"到达={arrivedCount}/{config.AgentCount}，结果={path}");
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
            public bool PerformanceGateEligible;
            public string BudgetVersion;
            public string SystemTimingCoverage;
            public bool WorkerTimingAvailable;
            public bool RequestQueueTimingAvailable;
            public long TrackedNativeBytes;
            public int FieldQueueLength;
            public int FieldQueueWaitP50Ticks;
            public int FieldQueueWaitP95Ticks;
            public int FieldQueueWaitP99Ticks;
            public int FieldCancelledCount;
            public int FieldTimeoutCount;
            public int UniqueFieldBuildCount;
            public int SharedFieldHitCount;
            public int SharedFieldRecordCount;
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
