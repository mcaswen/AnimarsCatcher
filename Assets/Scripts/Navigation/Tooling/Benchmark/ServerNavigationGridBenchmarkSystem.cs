using System;
using System.IO;
using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 按固定回放创建寻路和 Flow Field 请求，收集正确性统计与主线程性能样本
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    public partial struct ServerNavigationGridBenchmarkSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            state.RequireForUpdate<NavigationGridReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 每帧从单例读取基准配置和当前回放进度
            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<NavigationGridBenchmarkConfig>();
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            if (config.Workload != NavigationGridBenchmarkWorkload.PathAndField)
            {
                return;
            }

            NavigationGridBenchmarkState benchmarkState =
                SystemAPI.GetSingleton<NavigationGridBenchmarkState>();
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true);

            if (benchmarkState.Completed != 0)
            {
                if (benchmarkState.ResultExported == 0)
                {
                    ExportResult(
                        config,
                        benchmarkState,
                        SystemAPI.GetSingleton<NavigationGridReference>().Value.Value.DataHash.ToString(),
                        SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkTimingSample>());
                    benchmarkState.ResultExported = 1;
                    state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
                    state.Enabled = false;
                }

                return;
            }

            if (benchmarkState.Initialized == 0)
            {
                // 第一条回放命令同时用作预热目标；没有命令就无法建立有效工作负载
                if (commands.IsEmpty)
                {
                    FailBenchmark(ref state, benchmarkEntity, "Grid Benchmark 命令回放为空");
                    return;
                }

                // 请求 Entity 只创建一次，后续命令通过递增 Version 复用组件和 Buffer
                CreateRequests(ref state, config, commands[0]);
                benchmarkState.Initialized = 1;
                benchmarkState.NextCommandIndex = 0;
            }

            // 先统计刚完成的请求，再提交新版本，避免完成状态被覆盖
            CountCompletedRequests(ref state, ref benchmarkState);

            // 预热结束后，正式采样帧从 0 开始计数
            int sampleTick = benchmarkState.Tick - config.WarmupTicks;
            if (sampleTick >= 0 && benchmarkState.NextCommandIndex < commands.Length)
            {
                NavigationGridBenchmarkCommand command = commands[benchmarkState.NextCommandIndex];
                // 只在回放指定帧提交新目标；命令帧严格递增，因此每帧最多处理一条
                if (command.Tick == sampleTick)
                {
                    SubmitCommand(ref state, config, command, ref benchmarkState);
                    benchmarkState.NextCommandIndex++;
                }
            }

            benchmarkState.Tick++;
            int totalTicks = config.WarmupTicks + config.SampleTicks;
            if (benchmarkState.Tick >= totalTicks && AllRequestsTerminal(ref state))
            {
                // 采样窗口结束后仍等待最后一批异步请求完成，避免报告缺少结果
                benchmarkState.Completed = 1;
            }

            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
        }

        private static void CreateRequests(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkCommand firstCommand)
        {
            int count = math.max(1, config.AgentCount);
            // AgentIndex 决定请求起点在阵列中的位置，Entity 只在初始化时创建
            for (int agentIndex = 0; agentIndex < count; agentIndex++)
            {
                Entity entity = state.EntityManager.CreateEntity(
                    typeof(NavigationFlowFieldRequest),
                    typeof(NavigationFlowFieldState),
                    typeof(NavigationGridBenchmarkRequestTag));
                state.EntityManager.AddBuffer<NavigationCorridorCluster>(entity);
                state.EntityManager.AddBuffer<NavigationCorridorPortal>(entity);
                state.EntityManager.AddBuffer<NavigationHierarchicalWaypoint>(entity);
                state.EntityManager.AddBuffer<NavigationFlowFieldCell>(entity);
                float3 start = CalculateSpawnPosition(config, agentIndex);
                // 第一条命令只用于预热，不计入正式采样的提交数
                NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                    start,
                    config.SpawnOrigin + firstCommand.TargetOffset,
                    config.AgentRadius,
                    1,
                    maximumProjectionRadiusInCells: 16);
                state.EntityManager.SetComponentData(
                    entity,
                    NavigationFlowFieldRequest.Create(pathRequest));
                state.EntityManager.SetComponentData(
                    entity,
                    NavigationFlowFieldState.CreatePending(pathRequest.Version));
                state.EntityManager.SetComponentData(entity, new NavigationGridBenchmarkRequestTag
                {
                    AgentIndex = agentIndex,
                });
            }
        }

        private void SubmitCommand(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkCommand command,
            ref NavigationGridBenchmarkState benchmarkState)
        {
            foreach (var (request, fieldState, tag) in
                     SystemAPI.Query<
                         RefRW<NavigationFlowFieldRequest>,
                         RefRW<NavigationFlowFieldState>,
                         RefRO<NavigationGridBenchmarkRequestTag>>())
            {
                // 每次目标变化都递增版本，让 Flow Field 系统识别为新请求
                uint version = request.ValueRO.PathRequest.Version + 1;
                float3 start = CalculateSpawnPosition(config, tag.ValueRO.AgentIndex);
                // 所有请求共享目标但保留不同起点，可以直接测量 Flow Field 缓存复用效果
                NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                    start,
                    config.SpawnOrigin + command.TargetOffset,
                    config.AgentRadius,
                    version,
                    maximumProjectionRadiusInCells: 16);
                request.ValueRW = NavigationFlowFieldRequest.Create(pathRequest);
                fieldState.ValueRW = NavigationFlowFieldState.CreatePending(version);
                benchmarkState.SubmittedRequestCount++;
            }
        }

        private void CountCompletedRequests(
            ref SystemState state,
            ref NavigationGridBenchmarkState benchmarkState)
        {
            foreach (var (fieldState, tag) in SystemAPI.Query<
                         RefRO<NavigationFlowFieldState>,
                         RefRW<NavigationGridBenchmarkRequestTag>>())
            {
                NavigationPathStatus status = fieldState.ValueRO.Status;
                uint version = fieldState.ValueRO.RequestVersion;
                // 每个成功或失败版本只在首次观察到时统计
                if ((status != NavigationPathStatus.Succeeded &&
                     status != NavigationPathStatus.Failed) ||
                    tag.ValueRO.CountedVersion == version)
                {
                    continue;
                }

                // CountedVersion 记录已统计版本，后续帧不会重复累加
                tag.ValueRW.CountedVersion = version;
                benchmarkState.CompletedRequestCount++;
                if (fieldState.ValueRO.CacheHit != 0)
                {
                    benchmarkState.CacheHitCount++;
                }
                if (status == NavigationPathStatus.Succeeded)
                {
                    benchmarkState.SucceededRequestCount++;
                }
                else
                {
                    benchmarkState.FailedRequestCount++;
                }
                benchmarkState.TotalAbstractExpandedNodeCount +=
                    fieldState.ValueRO.AbstractExpandedNodeCount;
                benchmarkState.TotalIntegrationExpandedCellCount +=
                    fieldState.ValueRO.IntegrationExpandedCellCount;
            }
        }

        private bool AllRequestsTerminal(ref SystemState state)
        {
            foreach (RefRO<NavigationFlowFieldState> fieldState in
                     SystemAPI.Query<RefRO<NavigationFlowFieldState>>())
            {
                NavigationPathStatus status = fieldState.ValueRO.Status;
                // 仍有请求等待或计算时延迟导出结果
                if (status == NavigationPathStatus.Pending ||
                    status == NavigationPathStatus.Searching)
                {
                    return false;
                }
            }

            return true;
        }

        private static float3 CalculateSpawnPosition(
            NavigationGridBenchmarkConfig config,
            int agentIndex)
        {
            return NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                agentIndex,
                config.AgentCount,
                config.SpawnColumnCount,
                config.SpawnSpacing,
                config.SpawnOrigin,
                config.RandomSeed);
        }

        private static void ExportResult(
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkState benchmarkState,
            string gridBakeHash,
            DynamicBuffer<NavigationGridBenchmarkTimingSample> timingSamples)
        {
            double[] sortedTimingSamples = new double[timingSamples.Length];
            // 排序副本用于计算百分位，原始样本顺序用于定位具体帧的尖峰
            for (int index = 0; index < timingSamples.Length; index++)
            {
                sortedTimingSamples[index] = timingSamples[index].FlowFieldMilliseconds;
            }

            Array.Sort(sortedTimingSamples);
            double[] timingSamplesInTickOrder = new double[timingSamples.Length];
            for (int index = 0; index < timingSamples.Length; index++)
            {
                timingSamplesInTickOrder[index] = timingSamples[index].FlowFieldMilliseconds;
            }
            var report = new NavigationGridBenchmarkReport
            {
                FormatVersion = 5,
                Backend = AniMovementBackend.ClearanceGrid.ToString(),
                Workload = NavigationGridBenchmarkWorkload.PathAndField.ToString(),
                Failed = benchmarkState.FailedRequestCount > 0,
                PerformanceGateEligible = false,
                BudgetVersion = NavigationGridBenchmarkScaleProfile.BudgetVersion,
                SystemTimingCoverage = "仅记录 Flow Field 主线程范围",
                WorkerTimingAvailable = false,
                RequestQueueTimingAvailable = false,
                TrackedNativeBytes = -1,
                AgentCount = config.AgentCount,
                SubmittedRequestCount = benchmarkState.SubmittedRequestCount,
                CompletedRequestCount = benchmarkState.CompletedRequestCount,
                CacheHitCount = benchmarkState.CacheHitCount,
                FieldBuildCount = benchmarkState.CompletedRequestCount -
                                  benchmarkState.CacheHitCount,
                SucceededRequestCount = benchmarkState.SucceededRequestCount,
                FailedRequestCount = benchmarkState.FailedRequestCount,
                TotalAbstractExpandedNodeCount =
                    benchmarkState.TotalAbstractExpandedNodeCount,
                TotalIntegrationExpandedCellCount =
                    benchmarkState.TotalIntegrationExpandedCellCount,
                // 该模式只产生寻路请求，不创建或移动 Ani
                TransformWriteCount = 0,
                FlowFieldMainThreadSampleCount = sortedTimingSamples.Length,
                FlowFieldMainThreadP50Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTimingSamples, 0.50),
                FlowFieldMainThreadP95Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTimingSamples, 0.95),
                FlowFieldMainThreadP99Milliseconds =
                    StatisticsMath.CalculateNearestRankPercentile(sortedTimingSamples, 0.99),
                FlowFieldMainThreadMaxMilliseconds =
                    sortedTimingSamples.Length == 0 ? 0.0 : sortedTimingSamples[^1],
                FlowFieldMainThreadMilliseconds = timingSamplesInTickOrder,
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
                GridBakeHash = gridBakeHash,
                TimestampUtc = DateTime.UtcNow.ToString("O"),
            };
            // 结果统一写到项目的 BenchmarkResults 目录，方便批处理归档和比较
            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");
            Directory.CreateDirectory(directory);
            // 文件名包含请求规模和 UTC 时间，避免覆盖已有样本
            string path = Path.Combine(
                directory,
                $"GridNavigation_{config.AgentCount}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log($"[NavigationBenchmark] Grid 路径与 Field 结果已生成：{path}");
        }

        private static void FailBenchmark(
            ref SystemState state,
            Entity benchmarkEntity,
            string reason)
        {
            NavigationGridBenchmarkState benchmarkState =
                state.EntityManager.GetComponentData<NavigationGridBenchmarkState>(benchmarkEntity);
            benchmarkState.Completed = 1;
            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
            Debug.LogError($"[NavigationBenchmark] {reason}");
            state.Enabled = false;
            // 批处理失败时返回非零退出码；编辑器手动运行时只记录错误日志
            if (Application.isBatchMode)
            {
                Application.Quit(1);
            }
        }

        [Serializable]
        private sealed class NavigationGridBenchmarkReport
        {
            public int FormatVersion;
            public string Backend;
            public string Workload;
            public bool Failed;
            public bool PerformanceGateEligible;
            public string BudgetVersion;
            public string SystemTimingCoverage;
            public bool WorkerTimingAvailable;
            public bool RequestQueueTimingAvailable;
            public long TrackedNativeBytes;
            public int AgentCount;
            public int SubmittedRequestCount;
            public int CompletedRequestCount;
            public int CacheHitCount;
            public int FieldBuildCount;
            public int SucceededRequestCount;
            public int FailedRequestCount;
            public int TotalAbstractExpandedNodeCount;
            public int TotalIntegrationExpandedCellCount;
            public int TransformWriteCount;
            public int FlowFieldMainThreadSampleCount;
            public double FlowFieldMainThreadP50Milliseconds;
            public double FlowFieldMainThreadP95Milliseconds;
            public double FlowFieldMainThreadP99Milliseconds;
            public double FlowFieldMainThreadMaxMilliseconds;
            public double[] FlowFieldMainThreadMilliseconds;
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
        }
    }
}
