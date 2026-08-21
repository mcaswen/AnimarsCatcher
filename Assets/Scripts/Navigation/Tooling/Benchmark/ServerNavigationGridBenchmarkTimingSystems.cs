using System.Diagnostics;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
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
}
