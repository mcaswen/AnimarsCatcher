using System;
using System.IO;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 复用统一 Benchmark 场景参数生成纯路径、Corridor 和 Field 工作负载
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
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
            // 从单例恢复本 Tick 的配置和回放状态
            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<NavigationGridBenchmarkConfig>();
            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            NavigationGridBenchmarkState benchmarkState =
                SystemAPI.GetSingleton<NavigationGridBenchmarkState>();
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true);

            if (benchmarkState.Initialized == 0)
            {
                if (commands.IsEmpty)
                {
                    FailBenchmark(ref state, benchmarkEntity, "Grid Benchmark 命令回放为空");
                    return;
                }

                // 请求实体只创建一次，后续命令通过递增 Version 复用组件和 Buffer
                CreateRequests(ref state, config, commands[0]);
                benchmarkState.Initialized = 1;
                benchmarkState.NextCommandIndex = 0;
            }

            // 先统计上一批终态，避免和本 Tick 新提交的版本混淆
            CountCompletedRequests(ref state, ref benchmarkState);

            // Warmup 结束后将采样 Tick 从零开始计数
            int sampleTick = benchmarkState.Tick - config.WarmupTicks;
            if (sampleTick >= 0 && benchmarkState.NextCommandIndex < commands.Length)
            {
                NavigationGridBenchmarkCommand command = commands[benchmarkState.NextCommandIndex];
                // 只在命令指定的采样 Tick 广播新目标
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
                // 采样期结束后仍等待最后一个异步批次进入终态，避免导出缺失结果
                benchmarkState.Completed = 1;
                ExportResult(
                    config,
                    benchmarkState,
                    SystemAPI.GetSingleton<NavigationGridReference>().Value.Value.DataHash.ToString());
                state.Enabled = false;
                if (Application.isBatchMode && config.AutoQuit != 0)
                {
                    Application.Quit(0);
                }
            }

            state.EntityManager.SetComponentData(benchmarkEntity, benchmarkState);
        }

        private static void CreateRequests(
            ref SystemState state,
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkCommand firstCommand)
        {
            int count = math.max(1, config.AgentCount);
            // AgentIndex 决定稳定阵列位置，实体只在初始化时创建
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
                // 首条命令只提供 Warmup 目标，不计入采样提交量
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
                // 递增版本让 Flow Field System 把命令识别为新请求
                uint version = request.ValueRO.PathRequest.Version + 1;
                float3 start = CalculateSpawnPosition(config, tag.ValueRO.AgentIndex);
                // 全部 Agent 共享目标但保留独立起点，可直接测量 Field 缓存复用
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
                // 只统计首次观察到的 Succeeded 或 Failed 版本
                if ((status != NavigationPathStatus.Succeeded &&
                     status != NavigationPathStatus.Failed) ||
                    tag.ValueRO.CountedVersion == version)
                {
                    continue;
                }

                // CountedVersion 让每个请求版本只进入统计一次，后续 Tick 只观察不重复累加
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
                // 任一 Pending 或 Searching 请求都会延迟结果导出
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
            // 列数至少为一，避免错误配置导致除零
            int columns = math.max(1, config.SpawnColumnCount);
            int x = agentIndex % columns;
            int z = agentIndex / columns;
            // AgentIndex 按 XZ 行主序映射到生成阵列
            return config.SpawnOrigin + new float3(
                x * config.SpawnSpacing,
                0f,
                z * config.SpawnSpacing);
        }

        private static void ExportResult(
            NavigationGridBenchmarkConfig config,
            NavigationGridBenchmarkState benchmarkState,
            string gridBakeHash)
        {
            var report = new NavigationGridBenchmarkReport
            {
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
                // 本 Benchmark 只驱动路径请求，不创建或更新 Ani Transform
                TransformWriteCount = 0,
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
            // 结果统一写入项目 BenchmarkResults，便于批处理归档比较
            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");
            Directory.CreateDirectory(directory);
            // 文件名包含请求规模和 UTC 时间，避免覆盖既有样本
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
            // 批处理通过非零退出码传播失败，本地编辑器只保留错误日志
            if (Application.isBatchMode)
            {
                Application.Quit(1);
            }
        }

        [Serializable]
        private sealed class NavigationGridBenchmarkReport
        {
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
