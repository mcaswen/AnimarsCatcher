using System.Diagnostics;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在 Grid Benchmark 的采样窗口开始记录 Flow Field 系统计时
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
            // Benchmark System 已递增 Tick，因此开区间排除全部预热 Tick
            // 结果导出发生在下一 Tick，闭区间仍会保留最后一个采样 Tick
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
    /// 在 Flow Field 系统更新后保存主线程样本
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
            // 样本只包围 Flow Field System 的主线程调度与写回，不包含 Worker Job 执行时间
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
