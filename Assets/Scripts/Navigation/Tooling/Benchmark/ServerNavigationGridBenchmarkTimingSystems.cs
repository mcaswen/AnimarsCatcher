using System.Diagnostics;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
    internal struct NavigationGridMovementStageClock : IComponentData
    {
        public NavigationGridBenchmarkStage Stage;
        public int Tick;
        public long StartTimestamp;
        public byte Recording;
    }

    internal static class NavigationGridMovementStageTiming
    {
        internal static void Begin(
            ref SystemState state,
            Entity benchmarkEntity,
            NavigationGridBenchmarkConfig config,
            NavigationGridMovementBenchmarkState benchmarkState,
            NavigationGridBenchmarkStage stage)
        {
            int sampleTick = benchmarkState.Tick - config.WarmupTicks;
            bool shouldRecord = config.Workload ==
                                NavigationGridBenchmarkWorkload.FreeCohortMovement &&
                                benchmarkState.Initialized != 0 &&
                                benchmarkState.ResultExported == 0 &&
                                sampleTick >= 0 &&
                                sampleTick < config.SampleTicks;
            if (!shouldRecord)
            {
                return;
            }

            if (!state.EntityManager.HasComponent<NavigationGridMovementStageClock>(
                    benchmarkEntity))
            {
                state.EntityManager.AddComponentData(
                    benchmarkEntity,
                    default(NavigationGridMovementStageClock));
            }

            // 计时边界先收拢上一阶段 Job，避免异步重叠把成本记到错误阶段
            state.EntityManager.CompleteAllTrackedJobs();
            state.EntityManager.SetComponentData(
                benchmarkEntity,
                new NavigationGridMovementStageClock
                {
                    Stage = stage,
                    Tick = sampleTick,
                    StartTimestamp = Stopwatch.GetTimestamp(),
                    Recording = 1,
                });
        }

        internal static void End(
            ref SystemState state,
            Entity benchmarkEntity,
            NavigationGridBenchmarkStage expectedStage)
        {
            if (!state.EntityManager.HasComponent<NavigationGridMovementStageClock>(
                    benchmarkEntity))
            {
                return;
            }

            NavigationGridMovementStageClock clock = state.EntityManager.GetComponentData<
                NavigationGridMovementStageClock>(benchmarkEntity);
            if (clock.Recording == 0 || clock.Stage != expectedStage)
            {
                return;
            }

            // 完成当前阶段产生的 Job 后记录墙钟耗时，样本同时包含调度和 Worker 关键路径
            state.EntityManager.CompleteAllTrackedJobs();
            long elapsedTimestamp = Stopwatch.GetTimestamp() - clock.StartTimestamp;
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> samples =
                state.EntityManager.GetBuffer<NavigationGridBenchmarkStageTimingSample>(
                    benchmarkEntity);
            samples.Add(new NavigationGridBenchmarkStageTimingSample
            {
                Stage = expectedStage,
                Tick = clock.Tick,
                WorkerMilliseconds = elapsedTimestamp * 1000.0 / Stopwatch.Frequency,
            });
            clock.Recording = 0;
            state.EntityManager.SetComponentData(benchmarkEntity, clock);
        }
    }

    /// <summary>
    /// 在正式采样窗口内，记录 Flow Field 系统更新前的时间点
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    public partial struct ServerNavigationGridBenchmarkTimingStartSystem : ISystem
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
            if (config.Workload != NavigationGridBenchmarkWorkload.PathAndField)
            {
                return;
            }

            RefRW<NavigationGridBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridBenchmarkState>();
            // 基准系统已经递增帧数，因此这里用开区间排除全部预热帧
            // 报告在下一帧导出，闭区间会保留最后一个正式采样帧
            bool shouldRecord =
                benchmarkState.ValueRO.ResultExported == 0 &&
                benchmarkState.ValueRO.Tick >
                config.WarmupTicks &&
                benchmarkState.ValueRO.Tick <=
                config.WarmupTicks + config.SampleTicks;
            benchmarkState.ValueRW.RecordFlowFieldTiming = (byte)(shouldRecord ? 1 : 0);
            if (shouldRecord)
            {
                benchmarkState.ValueRW.FlowFieldStartTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// 在 Flow Field 系统更新后记录本次主线程耗时
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridFlowFieldSystem))]
    [UpdateBefore(typeof(AniSquadAnchorAdvanceSystem))]
    public partial struct ServerNavigationGridBenchmarkTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>().Workload !=
                NavigationGridBenchmarkWorkload.PathAndField)
            {
                return;
            }

            RefRW<NavigationGridBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<NavigationGridBenchmarkState>();
            if (benchmarkState.ValueRO.RecordFlowFieldTiming == 0)
            {
                return;
            }

            long elapsedTimestamp =
                Stopwatch.GetTimestamp() - benchmarkState.ValueRO.FlowFieldStartTimestamp;
            // 样本只统计 Flow Field 系统在主线程上的调度和写回，不包含后台任务运行时间
            DynamicBuffer<NavigationGridBenchmarkTimingSample> samples =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkTimingSample>();
            samples.Add(new NavigationGridBenchmarkTimingSample
            {
                FlowFieldMilliseconds = elapsedTimestamp * 1000.0 / Stopwatch.Frequency,
            });
            benchmarkState.ValueRW.RecordFlowFieldTiming = 0;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(AniMovementCohortPartitionSystem))]
    internal partial struct ServerNavigationCohortPlanningTimingStartSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<
                NavigationGridMovementBenchmarkState>();
            NavigationGridMovementStageTiming.Begin(
                ref state,
                benchmarkEntity,
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>(),
                state.EntityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                    benchmarkEntity),
                NavigationGridBenchmarkStage.CohortPartition);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniMovementCohortPathRequestSystem))]
    [UpdateBefore(typeof(ServerNavigationFieldTimingStartSystem))]
    internal partial struct ServerNavigationCohortPlanningTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridMovementStageTiming.End(
                ref state,
                SystemAPI.GetSingletonEntity<NavigationGridMovementBenchmarkState>(),
                NavigationGridBenchmarkStage.CohortPartition);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationCohortPlanningTimingEndSystem))]
    [UpdateBefore(typeof(ServerNavigationSharedFlowFieldSystem))]
    internal partial struct ServerNavigationFieldTimingStartSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<
                NavigationGridMovementBenchmarkState>();
            NavigationGridMovementStageTiming.Begin(
                ref state,
                benchmarkEntity,
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>(),
                state.EntityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                    benchmarkEntity),
                NavigationGridBenchmarkStage.FieldBuildAndPublish);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationSharedFlowFieldSystem))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    internal partial struct ServerNavigationFieldTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridMovementStageTiming.End(
                ref state,
                SystemAPI.GetSingletonEntity<NavigationGridMovementBenchmarkState>(),
                NavigationGridBenchmarkStage.FieldBuildAndPublish);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationFieldTimingEndSystem))]
    [UpdateAfter(typeof(ServerNavigationGridFlowFieldSystem))]
    [UpdateBefore(typeof(AniFreePreferredVelocitySystem))]
    internal partial struct ServerNavigationMovementTimingStartSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<
                NavigationGridMovementBenchmarkState>();
            NavigationGridMovementStageTiming.Begin(
                ref state,
                benchmarkEntity,
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>(),
                state.EntityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                    benchmarkEntity),
                NavigationGridBenchmarkStage.CommitAndProgress);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(AniFreeMovementProgressSystem))]
    internal partial struct ServerNavigationMovementTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridMovementBenchmarkState>();
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationGridMovementStageTiming.End(
                ref state,
                SystemAPI.GetSingletonEntity<NavigationGridMovementBenchmarkState>(),
                NavigationGridBenchmarkStage.CommitAndProgress);
        }
    }
}
