using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 为 512 到 10000 Ani 生成不经过 RPC 容量限制的确定性规模输入
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    [UpdateBefore(typeof(ServerNavigationGridBenchmarkSystem))]
    public partial struct ServerNavigationGridScaleInputBenchmarkSystem : ISystem
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
            if (config.Workload != NavigationGridBenchmarkWorkload.ScaleInputDeterminism)
            {
                return;
            }

            Entity benchmarkEntity = SystemAPI.GetSingletonEntity<NavigationGridBenchmarkConfig>();
            NavigationGridScaleInputBenchmarkState scaleState =
                SystemAPI.GetSingleton<NavigationGridScaleInputBenchmarkState>();
            NavigationGridMovementBenchmarkState timingState =
                SystemAPI.GetSingleton<NavigationGridMovementBenchmarkState>();
            DynamicBuffer<NavigationGridScaleInputMember> members =
                SystemAPI.GetSingletonBuffer<NavigationGridScaleInputMember>();
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkCommand>(true);
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> stageSamples =
                SystemAPI.GetSingletonBuffer<NavigationGridBenchmarkStageTimingSample>();

            if (scaleState.Completed != 0)
            {
                if (scaleState.ResultExported == 0)
                {
                    DynamicBuffer<NavigationGridMovementBenchmarkTimingSample> timingSamples =
                        SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkTimingSample>();
                    ValidateFinalHashes(ref scaleState, members);
                    scaleState.TrackedNativeBytes = CalculateTrackedNativeBytes(
                        members,
                        commands,
                        timingSamples,
                        stageSamples);
                    ClientServerTickRate resolvedTickRate = default;
                    if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                    {
                        resolvedTickRate = tickRate;
                    }

                    resolvedTickRate.ResolveDefaults();
                    ExportResult(
                        config,
                        scaleState,
                        members,
                        timingSamples,
                        stageSamples,
                        resolvedTickRate);
                    scaleState.ResultExported = 1;
                    timingState.ResultExported = 1;
                    state.EntityManager.SetComponentData(benchmarkEntity, scaleState);
                    state.EntityManager.SetComponentData(benchmarkEntity, timingState);
                    state.Enabled = false;
                }

                return;
            }

            if (scaleState.Initialized == 0)
            {
                if (commands.IsEmpty)
                {
                    FailBenchmark(
                        ref state,
                        benchmarkEntity,
                        ref scaleState,
                        "阶段六规模输入缺少目标回放命令");
                    return;
                }

                if (!NavigationGridBenchmarkScaleProfile.TryValidateRun(
                        config.Workload,
                        config.AgentCount,
                        out string reason))
                {
                    FailBenchmark(ref state, benchmarkEntity, ref scaleState, reason);
                    return;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                float3 targetPosition = config.SpawnOrigin + commands[0].TargetOffset;
                NavigationGridScaleInputAlgorithms.PopulateMembers(
                    members,
                    config,
                    targetPosition);
                NavigationGridScaleInputHashes firstHashes =
                    NavigationGridScaleInputAlgorithms.ComputeHashes(members);
                NavigationGridScaleInputHashes repeatedHashes =
                    NavigationGridScaleInputAlgorithms.ComputeHashes(members);
                double elapsedMilliseconds =
                    (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

                if (!HashesEqual(firstHashes, repeatedHashes))
                {
                    FailBenchmark(
                        ref state,
                        benchmarkEntity,
                        ref scaleState,
                        "阶段六规模输入在同一轮重复计算时产生不同 Hash");
                    return;
                }

                scaleState.CohortCount =
                    NavigationGridScaleInputAlgorithms.CalculateCohortCount(config.AgentCount);
                scaleState.UniqueFieldKeyCount = scaleState.CohortCount;
                scaleState.CohortPartitionHash = firstHashes.CohortPartitionHash;
                scaleState.GoalRegionHash = firstHashes.GoalRegionHash;
                scaleState.RequestKeyHash = firstHashes.RequestKeyHash;
                scaleState.Initialized = 1;
                timingState.Initialized = 1;
                timingState.Tick = 0;

                long trackedNativeBytes = CalculateTrackedNativeBytes(
                    members,
                    commands,
                    SystemAPI.GetSingletonBuffer<NavigationGridMovementBenchmarkTimingSample>(),
                    stageSamples);
                scaleState.TrackedNativeBytes = trackedNativeBytes;
                stageSamples.Add(new NavigationGridBenchmarkStageTimingSample
                {
                    Stage = NavigationGridBenchmarkStage.ScaleInputBuild,
                    Tick = -1,
                    MainThreadMilliseconds = elapsedMilliseconds,
                    WorkerMilliseconds = 0.0,
                    QueueWaitTicks = 0,
                    TrackedNativeBytes = trackedNativeBytes,
                });

                state.EntityManager.SetComponentData(benchmarkEntity, scaleState);
                state.EntityManager.SetComponentData(benchmarkEntity, timingState);
                Debug.Log(
                    $"[NavigationBenchmark] 已生成 {config.AgentCount} 条阶段六规模输入，" +
                    $"Cohort={scaleState.CohortCount}，Hash={FormatHash(scaleState.CohortPartitionHash)}");
                return;
            }

            scaleState.Tick++;
            timingState.Tick = scaleState.Tick;
            if (scaleState.Tick >= config.WarmupTicks + config.SampleTicks)
            {
                scaleState.Completed = 1;
                timingState.Completed = 1;
            }

            state.EntityManager.SetComponentData(benchmarkEntity, scaleState);
            state.EntityManager.SetComponentData(benchmarkEntity, timingState);
        }

        private static bool HashesEqual(
            NavigationGridScaleInputHashes left,
            NavigationGridScaleInputHashes right)
        {
            return left.CohortPartitionHash == right.CohortPartitionHash &&
                   left.GoalRegionHash == right.GoalRegionHash &&
                   left.RequestKeyHash == right.RequestKeyHash;
        }

        private static void ValidateFinalHashes(
            ref NavigationGridScaleInputBenchmarkState scaleState,
            DynamicBuffer<NavigationGridScaleInputMember> members)
        {
            NavigationGridScaleInputHashes finalHashes =
                NavigationGridScaleInputAlgorithms.ComputeHashes(members);
            if (finalHashes.CohortPartitionHash == scaleState.CohortPartitionHash &&
                finalHashes.GoalRegionHash == scaleState.GoalRegionHash &&
                finalHashes.RequestKeyHash == scaleState.RequestKeyHash)
            {
                return;
            }

            scaleState.Failed = 1;
            scaleState.FailureReason =
                new FixedString128Bytes("采样期间规模输入内容发生变化");
        }

        private static long CalculateTrackedNativeBytes(
            DynamicBuffer<NavigationGridScaleInputMember> members,
            DynamicBuffer<NavigationGridBenchmarkCommand> commands,
            DynamicBuffer<NavigationGridMovementBenchmarkTimingSample> timingSamples,
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> stageSamples)
        {
            // 这里只统计 Benchmark 自有 Buffer 负载，不把进程总内存冒充为导航内存
            return (long)members.Capacity * UnsafeUtility.SizeOf<NavigationGridScaleInputMember>() +
                   (long)commands.Capacity * UnsafeUtility.SizeOf<NavigationGridBenchmarkCommand>() +
                   (long)timingSamples.Capacity *
                   UnsafeUtility.SizeOf<NavigationGridMovementBenchmarkTimingSample>() +
                   (long)stageSamples.Capacity *
                   UnsafeUtility.SizeOf<NavigationGridBenchmarkStageTimingSample>();
        }

        private static void ExportResult(
            NavigationGridBenchmarkConfig config,
            NavigationGridScaleInputBenchmarkState scaleState,
            DynamicBuffer<NavigationGridScaleInputMember> members,
            DynamicBuffer<NavigationGridMovementBenchmarkTimingSample> tickSamples,
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> stageSamples,
            ClientServerTickRate resolvedTickRate)
        {
            double[] tickMilliseconds = new double[tickSamples.Length];
            long[] allocatedBytes = new long[tickSamples.Length];
            double[] sortedTickMilliseconds = new double[tickSamples.Length];
            double[] sortedAllocatedBytes = new double[tickSamples.Length];
            for (int index = 0; index < tickSamples.Length; index++)
            {
                tickMilliseconds[index] = tickSamples[index].ServerSimulationMilliseconds;
                allocatedBytes[index] = tickSamples[index].MainThreadAllocatedBytes;
                sortedTickMilliseconds[index] = tickMilliseconds[index];
                sortedAllocatedBytes[index] = allocatedBytes[index];
            }

            Array.Sort(sortedTickMilliseconds);
            Array.Sort(sortedAllocatedBytes);

            var report = new NavigationGridScaleInputBenchmarkReport
            {
                FormatVersion = 5,
                Backend = AniMovementBackend.ClearanceGrid.ToString(),
                Workload = NavigationGridBenchmarkWorkload.ScaleInputDeterminism.ToString(),
                PerformanceGateEligible = false,
                AgentCount = config.AgentCount,
                RandomSeed = config.RandomSeed,
                WarmupTicks = config.WarmupTicks,
                SampleTicks = config.SampleTicks,
                SimulationTickRate = resolvedTickRate.SimulationTickRate,
                Failed = scaleState.Failed != 0,
                FailureReason = scaleState.FailureReason.ToString(),
                InputHashVersion = NavigationGridBenchmarkScaleProfile.InputHashVersion,
                CohortCount = scaleState.CohortCount,
                MaximumCohortSize = NavigationGridBenchmarkScaleProfile.MaximumCohortSize,
                UniqueFieldKeyCount = scaleState.UniqueFieldKeyCount,
                FieldBuildCount = 0,
                SharedFieldHitCount = 0,
                RepathCount = 0,
                CohortPartitionHash = FormatHash(scaleState.CohortPartitionHash),
                GoalRegionHash = FormatHash(scaleState.GoalRegionHash),
                RequestKeyHash = FormatHash(scaleState.RequestKeyHash),
                DynamicBufferMemberCount = members.Length,
                TrackedNativeBytes = scaleState.TrackedNativeBytes,
                NativeMemoryCoverage = "Benchmark 自有 DynamicBuffer 负载",
                SystemTimingCoverage = "记录 ScaleInputBuild 主线程范围",
                WorkerTimingAvailable = false,
                RequestQueueTimingAvailable = false,
                ServerTickSampleCount = tickSamples.Length,
                ServerTickP50Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                    sortedTickMilliseconds,
                    0.50),
                ServerTickP95Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                    sortedTickMilliseconds,
                    0.95),
                ServerTickP99Milliseconds = StatisticsMath.CalculateNearestRankPercentile(
                    sortedTickMilliseconds,
                    0.99),
                ServerTickMaxMilliseconds = sortedTickMilliseconds.Length == 0
                    ? 0.0
                    : sortedTickMilliseconds[^1],
                MainThreadAllocP95Bytes = StatisticsMath.CalculateNearestRankPercentile(
                    sortedAllocatedBytes,
                    0.95),
                Budget = BuildBudgetReport(),
                StageSamples = BuildStageSampleReports(stageSamples),
                GitCommit = config.GitCommit.ToString(),
                UnityVersion = Application.unityVersion,
                EntitiesAssemblyVersion = typeof(Entity).Assembly.GetName().Version?.ToString(),
                Platform = Application.platform.ToString(),
                OperatingSystem = SystemInfo.operatingSystem,
                Processor = SystemInfo.processorType,
                ProcessorCount = SystemInfo.processorCount,
                SystemMemoryMegabytes = SystemInfo.systemMemorySize,
                MapSceneHash = config.MapSceneHash.ToString(),
                ReplayScriptHash = config.ReplayScriptHash.ToString(),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                TickMilliseconds = tickMilliseconds,
                MainThreadAllocatedBytes = allocatedBytes,
                Notes =
                    "6A.0 仅验证规模入口、DynamicBuffer 容量、报告结构与确定性输入，" +
                    "不包含自由移动、ORCA、世界碰撞或真实 Field 构建",
            };

            string directory = Path.GetFullPath("BenchmarkResults/GridNavigation");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                $"GridNavigation_{config.AgentCount}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log(
                $"[NavigationBenchmark] 阶段六规模输入结果已生成：" +
                $"Ani={config.AgentCount}，Cohort={scaleState.CohortCount}，结果={path}");
        }

        private static NavigationGridBenchmarkBudgetReport BuildBudgetReport()
        {
            var stageBudgets = new NavigationGridBenchmarkStageBudgetReport[11];
            int writeIndex = 0;
            for (NavigationGridBenchmarkStage stage = NavigationGridBenchmarkStage.CommandIngress;
                 stage <= NavigationGridBenchmarkStage.CommitAndProgress;
                 stage++)
            {
                stageBudgets[writeIndex++] = new NavigationGridBenchmarkStageBudgetReport
                {
                    Stage = stage.ToString(),
                    MainThreadP95Milliseconds =
                        NavigationGridBenchmarkScaleProfile.GetMainThreadP95BudgetMilliseconds(stage),
                };
            }

            return new NavigationGridBenchmarkBudgetReport
            {
                Version = NavigationGridBenchmarkScaleProfile.BudgetVersion,
                TargetSimulationTickRate =
                    NavigationGridBenchmarkScaleProfile.TargetSimulationTickRate,
                ServerTickP95Milliseconds =
                    NavigationGridBenchmarkScaleProfile.ServerTickP95BudgetMilliseconds,
                ServerTickP99Milliseconds =
                    NavigationGridBenchmarkScaleProfile.ServerTickP99BudgetMilliseconds,
                NavigationMainThreadP95Milliseconds =
                    NavigationGridBenchmarkScaleProfile.NavigationMainThreadP95BudgetMilliseconds,
                NavigationWorkerCriticalPathP95Milliseconds =
                    NavigationGridBenchmarkScaleProfile
                        .NavigationWorkerCriticalPathP95BudgetMilliseconds,
                RequestQueueWaitP95Ticks =
                    NavigationGridBenchmarkScaleProfile.RequestQueueWaitP95BudgetTicks,
                NavigationNativeMemoryBytes =
                    NavigationGridBenchmarkScaleProfile.NavigationNativeMemoryBudgetBytes,
                StageBudgets = stageBudgets,
            };
        }

        private static NavigationGridBenchmarkStageSampleReport[] BuildStageSampleReports(
            DynamicBuffer<NavigationGridBenchmarkStageTimingSample> samples)
        {
            var reports = new NavigationGridBenchmarkStageSampleReport[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                NavigationGridBenchmarkStageTimingSample sample = samples[index];
                reports[index] = new NavigationGridBenchmarkStageSampleReport
                {
                    Stage = sample.Stage.ToString(),
                    Tick = sample.Tick,
                    MainThreadMilliseconds = sample.MainThreadMilliseconds,
                    WorkerMilliseconds = sample.WorkerMilliseconds,
                    QueueWaitTicks = sample.QueueWaitTicks,
                    TrackedNativeBytes = sample.TrackedNativeBytes,
                };
            }

            return reports;
        }

        private static void FailBenchmark(
            ref SystemState state,
            Entity benchmarkEntity,
            ref NavigationGridScaleInputBenchmarkState scaleState,
            string reason)
        {
            scaleState.Failed = 1;
            scaleState.Completed = 1;
            scaleState.FailureReason = new FixedString128Bytes(reason);
            state.EntityManager.SetComponentData(benchmarkEntity, scaleState);
            Debug.LogError($"[NavigationBenchmark] {reason}");
        }

        private static string FormatHash(ulong value)
        {
            return value.ToString("X16", CultureInfo.InvariantCulture);
        }

        [Serializable]
        private sealed class NavigationGridScaleInputBenchmarkReport
        {
            public int FormatVersion;
            public string Backend;
            public string Workload;
            public bool PerformanceGateEligible;
            public int AgentCount;
            public int RandomSeed;
            public int WarmupTicks;
            public int SampleTicks;
            public int SimulationTickRate;
            public bool Failed;
            public string FailureReason;
            public string InputHashVersion;
            public int CohortCount;
            public int MaximumCohortSize;
            public int UniqueFieldKeyCount;
            public int FieldBuildCount;
            public int SharedFieldHitCount;
            public int RepathCount;
            public string CohortPartitionHash;
            public string GoalRegionHash;
            public string RequestKeyHash;
            public int DynamicBufferMemberCount;
            public long TrackedNativeBytes;
            public string NativeMemoryCoverage;
            public string SystemTimingCoverage;
            public bool WorkerTimingAvailable;
            public bool RequestQueueTimingAvailable;
            public int ServerTickSampleCount;
            public double ServerTickP50Milliseconds;
            public double ServerTickP95Milliseconds;
            public double ServerTickP99Milliseconds;
            public double ServerTickMaxMilliseconds;
            public double MainThreadAllocP95Bytes;
            public NavigationGridBenchmarkBudgetReport Budget;
            public NavigationGridBenchmarkStageSampleReport[] StageSamples;
            public string GitCommit;
            public string UnityVersion;
            public string EntitiesAssemblyVersion;
            public string Platform;
            public string OperatingSystem;
            public string Processor;
            public int ProcessorCount;
            public int SystemMemoryMegabytes;
            public string MapSceneHash;
            public string ReplayScriptHash;
            public string TimestampUtc;
            public double[] TickMilliseconds;
            public long[] MainThreadAllocatedBytes;
            public string Notes;
        }

        [Serializable]
        private sealed class NavigationGridBenchmarkBudgetReport
        {
            public string Version;
            public int TargetSimulationTickRate;
            public double ServerTickP95Milliseconds;
            public double ServerTickP99Milliseconds;
            public double NavigationMainThreadP95Milliseconds;
            public double NavigationWorkerCriticalPathP95Milliseconds;
            public int RequestQueueWaitP95Ticks;
            public long NavigationNativeMemoryBytes;
            public NavigationGridBenchmarkStageBudgetReport[] StageBudgets;
        }

        [Serializable]
        private sealed class NavigationGridBenchmarkStageBudgetReport
        {
            public string Stage;
            public double MainThreadP95Milliseconds;
        }

        [Serializable]
        private sealed class NavigationGridBenchmarkStageSampleReport
        {
            public string Stage;
            public int Tick;
            public double MainThreadMilliseconds;
            public double WorkerMilliseconds;
            public int QueueWaitTicks;
            public long TrackedNativeBytes;
        }
    }
}
