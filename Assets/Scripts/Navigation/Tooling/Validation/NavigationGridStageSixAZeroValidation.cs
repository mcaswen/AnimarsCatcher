#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证 6A.0 规模入口、工作负载边界、冻结预算和输入确定性
    /// </summary>
    public static class NavigationGridStageSixAZeroValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Six A Zero Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.0 自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次检查规模清单、工作负载、预算、DynamicBuffer 和三类输入 Hash
        /// </summary>
        public static void RunAll()
        {
            TestScaleCatalog();
            TestWorkloadParsingAndBoundaries();
            TestFrozenBudget();
            TestDeterministicScaleInputs();
            TestSystemRegistration();
            Debug.Log("Navigation Grid 6A.0 自动验收通过");
        }

        private static void TestScaleCatalog()
        {
            // 历史回归规模继续保留在统一 Harness 中
            int[] baselineCounts = { 32, 64, 128 };
            // 阶段六从 512 逐级扩展到正式万人上限
            int[] stageSixCounts = { 512, 1000, 2500, 5000, 10000 };
            for (int index = 0; index < baselineCounts.Length; index++)
            {
                Assert(
                    NavigationGridBenchmarkScaleProfile.IsReplayBaselineAgentCount(
                        baselineCounts[index]),
                    $"缺少 {baselineCounts[index]} Ani 历史回放入口");
                Assert(
                    NavigationGridBenchmarkScaleProfile.IsSupportedAgentCount(
                        baselineCounts[index]),
                    $"统一 Harness 未登记 {baselineCounts[index]} Ani");
            }

            for (int index = 0; index < stageSixCounts.Length; index++)
            {
                Assert(
                    NavigationGridBenchmarkScaleProfile.IsStageSixAgentCount(
                        stageSixCounts[index]),
                    $"缺少 {stageSixCounts[index]} Ani 阶段六入口");
                Assert(
                    NavigationGridBenchmarkScaleProfile.IsSupportedAgentCount(
                        stageSixCounts[index]),
                    $"统一 Harness 未登记 {stageSixCounts[index]} Ani");
            }

            // 未登记的中间值不能被范围判断静默放行
            Assert(
                !NavigationGridBenchmarkScaleProfile.IsSupportedAgentCount(256),
                "未登记的 256 Ani 不应被静默接受");
        }

        private static void TestWorkloadParsingAndBoundaries()
        {
            AssertWorkload("stage3", NavigationGridBenchmarkWorkload.PathAndField);
            AssertWorkload("SquadMovement", NavigationGridBenchmarkWorkload.StrictFormationBaseline);
            AssertWorkload("scaleinput", NavigationGridBenchmarkWorkload.ScaleInputDeterminism);
            AssertWorkload("free", NavigationGridBenchmarkWorkload.FreeCohortMovement);
            AssertWorkload("orca", NavigationGridBenchmarkWorkload.Avoidance);
            AssertWorkload("collision", NavigationGridBenchmarkWorkload.Collision);

            // 严格阵型仅保留 128 Ani 以内的历史回放资格
            Assert(
                NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    NavigationGridBenchmarkWorkload.StrictFormationBaseline,
                    128,
                    out _),
                "128 Ani 严格阵型历史基线必须继续可回放");
            // 512 Ani 必须在进入平方复杂度槽位分配前被拒绝
            Assert(
                !NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    NavigationGridBenchmarkWorkload.StrictFormationBaseline,
                    512,
                    out string strictReason) &&
                strictReason.Contains("历史基线", StringComparison.Ordinal),
                "严格阵型不能绕过规模保护进入平方复杂度链路");
            // 6A.0 规模输入不运行实际移动，因此允许完整万人数据
            Assert(
                NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    NavigationGridBenchmarkWorkload.ScaleInputDeterminism,
                    10000,
                    out _),
                "10000 Ani 规模输入入口必须可执行");
            // 尚未交付的工作负载应返回明确阶段提示
            Assert(
                !NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    NavigationGridBenchmarkWorkload.FreeCohortMovement,
                    10000,
                    out string futureReason) &&
                futureReason.Contains("6A.2", StringComparison.Ordinal),
                "尚未实现的自由移动工作负载必须明确拒绝");
        }

        private static void TestFrozenBudget()
        {
            Assert(
                NavigationGridBenchmarkScaleProfile.TargetSimulationTickRate == 60,
                "6A.0 Server Tick 目标必须固定为 60 Hz");
            Assert(
                NavigationGridBenchmarkScaleProfile.ServerTickP95BudgetMilliseconds <= 16.667,
                "Server Tick P95 预算不能超过一个 60 Hz Tick");
            Assert(
                NavigationGridBenchmarkScaleProfile.RequestQueueWaitP95BudgetTicks == 4,
                "请求排队 P95 预算必须保持为 4 Tick");
            Assert(
                NavigationGridBenchmarkScaleProfile.NavigationNativeMemoryBudgetBytes ==
                512L * 1024L * 1024L,
                "Navigation Native 内存预算必须保持为 512 MiB");

            // 各阶段预算必须完整累加回导航主线程总预算
            double stageBudgetTotal = 0.0;
            for (NavigationGridBenchmarkStage stage = NavigationGridBenchmarkStage.CommandIngress;
                 stage <= NavigationGridBenchmarkStage.CommitAndProgress;
                 stage++)
            {
                double budget =
                    NavigationGridBenchmarkScaleProfile.GetMainThreadP95BudgetMilliseconds(stage);
                Assert(budget > 0.0, $"{stage} 缺少主线程 P95 预算");
                stageBudgetTotal += budget;
            }

            Assert(
                Math.Abs(
                    stageBudgetTotal -
                    NavigationGridBenchmarkScaleProfile
                        .NavigationMainThreadP95BudgetMilliseconds) < 0.0001,
                "各导航阶段主线程预算之和必须等于总预算");
        }

        private static void TestDeterministicScaleInputs()
        {
            int[] counts = { 512, 1000, 2500, 5000, 10000 };
            for (int index = 0; index < counts.Length; index++)
            {
                ValidateScaleInput(counts[index]);
            }
        }

        private static void ValidateScaleInput(int agentCount)
        {
            // 每个规模使用独立 World，避免 Buffer 容量和 Entity 版本相互影响
            using var world = new World($"Stage Six A Zero {agentCount}", WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            Entity firstEntity = entityManager.CreateEntity();
            Entity secondEntity = entityManager.CreateEntity();
            entityManager.AddBuffer<NavigationGridScaleInputMember>(firstEntity);
            entityManager.AddBuffer<NavigationGridScaleInputMember>(secondEntity);
            // 两次结构变化完成后重新取得 Buffer，避免继续使用已失效的安全句柄
            DynamicBuffer<NavigationGridScaleInputMember> first =
                entityManager.GetBuffer<NavigationGridScaleInputMember>(firstEntity);
            DynamicBuffer<NavigationGridScaleInputMember> second =
                entityManager.GetBuffer<NavigationGridScaleInputMember>(secondEntity);
            var config = new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.ScaleInputDeterminism,
                AgentCount = agentCount,
                RandomSeed = 104729,
                SpawnColumnCount = 16,
                SpawnSpacing = 1.25f,
                SpawnOrigin = new float3(105f, 0.57f, 44.43f),
                AgentRadius = 0.35f,
            };
            // 使用共享回放的首个目标，固定值必须与正式 Harness 输入一致
            float3 targetPosition = config.SpawnOrigin + new float3(-20f, 0f, 16f);

            NavigationGridScaleInputAlgorithms.PopulateMembers(first, config, targetPosition);
            NavigationGridScaleInputAlgorithms.PopulateMembers(second, config, targetPosition);
            NavigationGridScaleInputHashes firstHashes =
                NavigationGridScaleInputAlgorithms.ComputeHashes(first);
            NavigationGridScaleInputHashes secondHashes =
                NavigationGridScaleInputAlgorithms.ComputeHashes(second);

            Assert(first.Length == agentCount, $"{agentCount} Ani 输入没有完整写入 DynamicBuffer");
            Assert(second.Length == agentCount, $"{agentCount} Ani 重放输入数量不一致");
            Assert(first.Capacity >= agentCount, $"{agentCount} Ani DynamicBuffer 容量不足");
            Assert(
                firstHashes.CohortPartitionHash == secondHashes.CohortPartitionHash,
                $"{agentCount} Ani Cohort 切分 Hash 不稳定");
            Assert(
                firstHashes.GoalRegionHash == secondHashes.GoalRegionHash,
                $"{agentCount} Ani 目标区域 Hash 不稳定");
            Assert(
                firstHashes.RequestKeyHash == secondHashes.RequestKeyHash,
                $"{agentCount} Ani 请求 Key Hash 不稳定");
            if (agentCount == 10000)
            {
                // 万人夹具的固定值用于阻止未升级 Hash 版本的输入漂移
                Assert(
                    firstHashes.CohortPartitionHash == 0x7FA032DD69575255UL,
                    "10000 Ani Cohort 切分 Hash 与 Stage6A0-v1 不一致");
                Assert(
                    firstHashes.GoalRegionHash == 0xCE895C650B74FB03UL,
                    "10000 Ani 目标区域 Hash 与 Stage6A0-v1 不一致");
                Assert(
                    firstHashes.RequestKeyHash == 0x7AC21B7695215AC3UL,
                    "10000 Ani 请求 Key Hash 与 Stage6A0-v1 不一致");
            }

            int expectedCohortCount =
                NavigationGridScaleInputAlgorithms.CalculateCohortCount(agentCount);
            Assert(
                first[first.Length - 1].CohortIndex + 1 == expectedCohortCount,
                $"{agentCount} Ani Cohort 数量错误");
            var requestKeys = new HashSet<ulong>();
            int previousCohortIndex = -1;
            for (int memberIndex = 0; memberIndex < first.Length; memberIndex++)
            {
                Assert(
                    first[memberIndex].StableId == memberIndex + 1,
                    $"{agentCount} Ani 稳定成员编号不连续");
                Assert(
                    first[memberIndex].CohortIndex ==
                    memberIndex / NavigationGridBenchmarkScaleProfile.MaximumCohortSize,
                    $"{agentCount} Ani Cohort 成员归属错误");
                if (first[memberIndex].CohortIndex != previousCohortIndex)
                {
                    // 每个 Cohort 首成员必须携带唯一请求 Key
                    Assert(
                        requestKeys.Add(first[memberIndex].RequestKey),
                        $"{agentCount} Ani 出现重复的 Cohort 请求 Key");
                    previousCohortIndex = first[memberIndex].CohortIndex;
                }
            }

            Assert(
                requestKeys.Count == expectedCohortCount,
                $"{agentCount} Ani 唯一请求 Key 数量错误");
        }

        private static void TestSystemRegistration()
        {
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            Assert(
                ContainsSystem(serverSystems, typeof(ServerNavigationGridScaleInputBenchmarkSystem)),
                "Server World 缺少阶段六规模输入 Benchmark System");
            Assert(
                !ContainsSystem(clientSystems, typeof(ServerNavigationGridScaleInputBenchmarkSystem)),
                "阶段六规模输入 Benchmark System 不能注册到 Client World");
        }

        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type targetType)
        {
            for (int index = 0; index < systems.Count; index++)
            {
                if (systems[index] == targetType)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertWorkload(
            string value,
            NavigationGridBenchmarkWorkload expected)
        {
            Assert(
                NavigationGridBenchmarkScaleProfile.TryParseWorkload(value, out var actual) &&
                actual == expected,
                $"工作负载参数 {value} 解析错误");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
