using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AnimarsCatcher.Benchmarks.LegacyNavigation;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 在 Server Simulation Tick 起点记录墙钟时间与主线程累计分配量
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct LegacyNavigationBenchmarkTimingStartSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate<LegacyNavigationBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            RefRW<LegacyNavigationBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<LegacyNavigationBenchmarkState>();
            bool shouldRecord = benchmarkState.ValueRO.Phase ==
                                LegacyNavigationBenchmarkPhase.Sampling;
            // 将本 Tick 的采样决定传到组末，避免 Harness 在中途切换阶段改变测量边界
            benchmarkState.ValueRW.RecordCurrentTick = (byte)(shouldRecord ? 1 : 0);

            if (!shouldRecord)
            {
                return;
            }

            benchmarkState.ValueRW.FrameStartTimestamp = Stopwatch.GetTimestamp();
            // 使用当前主线程累计值，组末做差后不会包含 Worker 线程分配
            benchmarkState.ValueRW.FrameStartAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread();
        }
    }

    /// <summary>
    /// 在 Server Simulation Tick 末尾保存样本并导出完整 JSON 结果
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial struct LegacyNavigationBenchmarkTimingEndSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate<LegacyNavigationBenchmarkConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            RefRW<LegacyNavigationBenchmarkState> benchmarkState =
                SystemAPI.GetSingletonRW<LegacyNavigationBenchmarkState>();
            if (benchmarkState.ValueRO.RecordCurrentTick != 0)
            {
                // 组首标记过的 Tick 必须在状态完成切换后仍记录到同一采样窗口
                RecordSample(ref state, benchmarkState);
            }

            LegacyNavigationBenchmarkConfig config =
                SystemAPI.GetSingleton<LegacyNavigationBenchmarkConfig>();
            DynamicBuffer<LegacyNavigationBenchmarkSampleElement> samples =
                SystemAPI.GetSingletonBuffer<LegacyNavigationBenchmarkSampleElement>();
            if (benchmarkState.ValueRO.Phase != LegacyNavigationBenchmarkPhase.Completed ||
                benchmarkState.ValueRO.ResultExported != 0 ||
                samples.Length < config.SampleTicks)
            {
                return;
            }

            // 导出标记在成功写盘后设置，防止后续 Tick 生成重复结果
            ExportResult(ref state, config, benchmarkState.ValueRO, samples);
            benchmarkState.ValueRW.ResultExported = 1;

#if !UNITY_EDITOR
            if (Application.isBatchMode && config.AutoQuit != 0)
            {
                Application.Quit(0);
            }
#endif
        }

        private void RecordSample(
            ref SystemState state,
            RefRW<LegacyNavigationBenchmarkState> benchmarkState)
        {
            long elapsedTimestamp =
                Stopwatch.GetTimestamp() - benchmarkState.ValueRO.FrameStartTimestamp;
            double elapsedMilliseconds =
                elapsedTimestamp * 1000.0 / Stopwatch.Frequency;
            long allocatedBytes = Math.Max(
                0L,
                GC.GetAllocatedBytesForCurrentThread() -
                benchmarkState.ValueRO.FrameStartAllocatedBytes);

            // 原始样本保留完整顺序，百分位排序只发生在导出副本上
            DynamicBuffer<LegacyNavigationBenchmarkSampleElement> samples =
                SystemAPI.GetSingletonBuffer<LegacyNavigationBenchmarkSampleElement>();
            samples.Add(new LegacyNavigationBenchmarkSampleElement
            {
                ServerSimulationMilliseconds = elapsedMilliseconds,
                MainThreadAllocatedBytes = allocatedBytes
            });
            benchmarkState.ValueRW.RecordCurrentTick = 0;
        }

        private void ExportResult(
            ref SystemState state,
            LegacyNavigationBenchmarkConfig config,
            LegacyNavigationBenchmarkState benchmarkState,
            DynamicBuffer<LegacyNavigationBenchmarkSampleElement> samples)
        {
            double[] tickMilliseconds = new double[samples.Length];
            long[] allocatedBytes = new long[samples.Length];
            double[] sortedTickMilliseconds = new double[samples.Length];
            double[] sortedAllocatedBytes = new double[samples.Length];
            // 同时保留原始时间序列和排序副本，结果文件既可画时序图也可复算百分位
            for (int i = 0; i < samples.Length; i++)
            {
                tickMilliseconds[i] = samples[i].ServerSimulationMilliseconds;
                allocatedBytes[i] = samples[i].MainThreadAllocatedBytes;
                sortedTickMilliseconds[i] = tickMilliseconds[i];
                sortedAllocatedBytes[i] = allocatedBytes[i];
            }

            Array.Sort(sortedTickMilliseconds);
            Array.Sort(sortedAllocatedBytes);

            // 空间质量在采样结束后的同一最终快照上统一计算
            CalculateFinalSpatialMetrics(
                ref state,
                benchmarkState,
                out int arrivedCount,
                out float minimumUnitSpacing,
                out float averageFormationError);
            LegacyNavigationBenchmarkCounters counters =
                SystemAPI.GetSingleton<LegacyNavigationBenchmarkCounters>();

            ClientServerTickRate resolvedTickRate = default;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
            {
                resolvedTickRate = tickRate;
            }

            resolvedTickRate.ResolveDefaults();

            var result = new LegacyNavigationBenchmarkResult
            {
                FormatVersion = 1,
                Backend = AniMovementBackend.LegacyNavMesh.ToString(),
                BaselineVariant = config.BaselineVariant.ToString(),
                BaselineVersion = config.BaselineVersion.ToString(),
                ScenarioName = config.ScenarioName.ToString(),
                AgentCount = config.AgentCount,
                RandomSeed = config.RandomSeed,
                WarmupTicks = config.WarmupTicks,
                SampleTicks = config.SampleTicks,
                SimulationTickRate = resolvedTickRate.SimulationTickRate,
                NetworkTickRate = resolvedTickRate.NetworkTickRate,
                MaxSimulationStepsPerFrame = resolvedTickRate.MaxSimulationStepsPerFrame,
                MaxSimulationStepBatchSize = resolvedTickRate.MaxSimulationStepBatchSize,
                AppliedCommandCount = benchmarkState.AppliedCommandCount,
                PathRequestCount = counters.PathRequestCount,
                PathSuccessCount = counters.PathSuccessCount,
                PathFailureCount = counters.PathFailureCount,
                ArrivedCount = arrivedCount,
                ArrivalRate = config.AgentCount == 0
                    ? 0f
                    : (float)arrivedCount / config.AgentCount,
                MinimumUnitSpacing = minimumUnitSpacing,
                AverageFormationError = averageFormationError,
                ServerTickP50Milliseconds =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedTickMilliseconds,
                        0.50),
                ServerTickP95Milliseconds =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedTickMilliseconds,
                        0.95),
                ServerTickP99Milliseconds =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedTickMilliseconds,
                        0.99),
                MainThreadAllocP50Bytes =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedAllocatedBytes,
                        0.50),
                MainThreadAllocP95Bytes =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedAllocatedBytes,
                        0.95),
                MainThreadAllocP99Bytes =
                    LegacyNavigationBenchmarkAlgorithms.CalculateNearestRankPercentile(
                        sortedAllocatedBytes,
                        0.99),
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
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                TickMilliseconds = tickMilliseconds,
                MainThreadAllocatedBytes = allocatedBytes,
                Notes = "Server Simulation 墙钟时间包含同组正式系统；主线程分配量不包含 Worker 线程"
            };

            string outputDirectory = ResolveOutputDirectory(config.ResultDirectory.ToString());
            Directory.CreateDirectory(outputDirectory);
            string fileName =
                $"{config.ScenarioName}_{config.BaselineVersion}_" +
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            string outputPath = Path.Combine(outputDirectory, fileName);
            File.WriteAllText(
                outputPath,
                JsonUtility.ToJson(result, prettyPrint: true),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Debug.Log(
                $"[LegacyNavigationBenchmark] 采样完成：" +
                $"P50={result.ServerTickP50Milliseconds:F3} ms，" +
                $"P95={result.ServerTickP95Milliseconds:F3} ms，" +
                $"P99={result.ServerTickP99Milliseconds:F3} ms，" +
                $"结果={outputPath}");
        }

        private void CalculateFinalSpatialMetrics(
            ref SystemState state,
            LegacyNavigationBenchmarkState benchmarkState,
            out int arrivedCount,
            out float minimumUnitSpacing,
            out float averageFormationError)
        {
            var positions = new List<float3>();
            float totalFormationError = 0f;
            arrivedCount = 0;
            int slotIndex = 0;

            foreach (var (transform, steering) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<NavSteering>>()
                         .WithAll<LegacyNavigationBenchmarkAniTag>())
            {
                float3 position = transform.ValueRO.Position;
                positions.Add(position);
                if (steering.ValueRO.HasPath == 0)
                {
                    arrivedCount++;
                }

                float3 localOffset = AniFormationUtility.CalculateRectangularFormationLocalOffset(
                    slotIndex,
                    AniFormationUtility.FormationColumnCount,
                    AniFormationUtility.FormationHorizontalSpacing,
                    AniFormationUtility.FormationBackwardSpacing);
                float3 expectedPosition = benchmarkState.LastFormationCenter +
                    AniFormationUtility.RotateLocalOffsetToWorld(
                        localOffset,
                        benchmarkState.LastFormationRotation);
                totalFormationError += math.distance(position, expectedPosition);
                slotIndex++;
            }

            minimumUnitSpacing = float.PositiveInfinity;
            for (int firstIndex = 0; firstIndex < positions.Count; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1;
                     secondIndex < positions.Count;
                     secondIndex++)
                {
                    minimumUnitSpacing = math.min(
                        minimumUnitSpacing,
                        math.distance(positions[firstIndex], positions[secondIndex]));
                }
            }

            if (!math.isfinite(minimumUnitSpacing))
            {
                minimumUnitSpacing = 0f;
            }

            averageFormationError = positions.Count == 0
                ? 0f
                : totalFormationError / positions.Count;
        }

        private static string ResolveOutputDirectory(string configuredPath)
        {
            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            string root = Application.isEditor
                ? Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                : Application.persistentDataPath;
            return Path.Combine(root, configuredPath);
        }

        [Serializable]
        private sealed class LegacyNavigationBenchmarkResult
        {
            public int FormatVersion;
            public string Backend;
            public string BaselineVariant;
            public string BaselineVersion;
            public string ScenarioName;
            public int AgentCount;
            public int RandomSeed;
            public int WarmupTicks;
            public int SampleTicks;
            public int SimulationTickRate;
            public int NetworkTickRate;
            public int MaxSimulationStepsPerFrame;
            public int MaxSimulationStepBatchSize;
            public int AppliedCommandCount;
            public int PathRequestCount;
            public int PathSuccessCount;
            public int PathFailureCount;
            public int ArrivedCount;
            public float ArrivalRate;
            public float MinimumUnitSpacing;
            public float AverageFormationError;
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
            public string TimestampUtc;
            public double[] TickMilliseconds;
            public long[] MainThreadAllocatedBytes;
            public string Notes;
        }
    }
}
