using System;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 统一维护 Benchmark 支持的规模、工作负载入口和阶段六冻结预算
    /// </summary>
    public static class NavigationGridBenchmarkScaleProfile
    {
        public const string InputHashVersion = "Stage6A0-v1";
        public const string BudgetVersion = "Stage6A0-60Hz-v1";
        public const int MaximumCohortSize = 128;
        public const int TargetSimulationTickRate = 60;
        public const double ServerTickP95BudgetMilliseconds = 16.667;
        public const double ServerTickP99BudgetMilliseconds = 20.0;
        public const double NavigationMainThreadP95BudgetMilliseconds = 8.0;
        public const double NavigationWorkerCriticalPathP95BudgetMilliseconds = 8.0;
        public const int RequestQueueWaitP95BudgetTicks = 4;
        public const long NavigationNativeMemoryBudgetBytes = 512L * 1024L * 1024L;

        /// <summary>
        /// 判断规模是否属于仍需回放的历史基线
        /// </summary>
        public static bool IsReplayBaselineAgentCount(int agentCount)
        {
            return agentCount == 32 || agentCount == 64 || agentCount == 128;
        }

        /// <summary>
        /// 判断规模是否属于阶段六新增的压力档位
        /// </summary>
        public static bool IsStageSixAgentCount(int agentCount)
        {
            return agentCount == 512 ||
                   agentCount == 1000 ||
                   agentCount == 2500 ||
                   agentCount == 5000 ||
                   agentCount == 10000;
        }

        /// <summary>
        /// 判断规模是否属于阶段六预算之外的扩展压力实验
        /// </summary>
        public static bool IsExtendedStressAgentCount(int agentCount)
        {
            return agentCount == 100000;
        }

        /// <summary>
        /// 判断自由移动测试是否需要大规模出生布局和导航网格
        /// </summary>
        public static bool UsesLargeScaleGrid(int agentCount)
        {
            return IsStageSixAgentCount(agentCount) || IsExtendedStressAgentCount(agentCount);
        }

        /// <summary>
        /// 判断统一 Harness 是否登记了给定规模
        /// </summary>
        public static bool IsSupportedAgentCount(int agentCount)
        {
            return IsReplayBaselineAgentCount(agentCount) ||
                   IsStageSixAgentCount(agentCount) ||
                   IsExtendedStressAgentCount(agentCount);
        }

        /// <summary>
        /// 判断工作负载是否已经具备可执行实现
        /// </summary>
        public static bool IsImplementedWorkload(NavigationGridBenchmarkWorkload workload)
        {
            return workload == NavigationGridBenchmarkWorkload.PathAndField ||
                   workload == NavigationGridBenchmarkWorkload.StrictFormationBaseline ||
                   workload == NavigationGridBenchmarkWorkload.ScaleInputDeterminism ||
                   workload == NavigationGridBenchmarkWorkload.FreeCohortMovement;
        }

        /// <summary>
        /// 判断工作负载是否需要记录完整 Server Tick
        /// </summary>
        public static bool RecordsFullServerTick(NavigationGridBenchmarkWorkload workload)
        {
            return workload == NavigationGridBenchmarkWorkload.StrictFormationBaseline ||
                   workload == NavigationGridBenchmarkWorkload.ScaleInputDeterminism ||
                   workload == NavigationGridBenchmarkWorkload.FreeCohortMovement;
        }

        /// <summary>
        /// 校验规模与工作负载组合并返回可直接显示的原因
        /// </summary>
        public static bool TryValidateRun(
            NavigationGridBenchmarkWorkload workload,
            int agentCount,
            out string reason)
        {
            if (!IsSupportedAgentCount(agentCount))
            {
                reason =
                    $"Navigation Benchmark 不支持 {agentCount} Ani，可用规模为 " +
                    "32、64、128、512、1000、2500、5000、10000 或 100000";
                return false;
            }

            if (!IsImplementedWorkload(workload))
            {
                reason = workload switch
                {
                    NavigationGridBenchmarkWorkload.Avoidance =>
                        "Avoidance 将在 6B.2 接入，6A.0 不能生成伪 ORCA 结果",
                    NavigationGridBenchmarkWorkload.Collision =>
                        "Collision 将在 6B.3 接入，6A.0 不能生成伪碰撞结果",
                    _ => $"工作负载 {workload} 尚未实现",
                };
                return false;
            }

            if ((workload == NavigationGridBenchmarkWorkload.PathAndField ||
                 workload == NavigationGridBenchmarkWorkload.StrictFormationBaseline) &&
                !IsReplayBaselineAgentCount(agentCount))
            {
                reason =
                    $"{workload} 只保留 32、64、128 Ani 历史基线，" +
                    "512 以上规模请使用 FreeCohortMovement 或 ScaleInputDeterminism";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// 解析命令行中的工作负载名称并兼容阶段三和阶段四旧参数
        /// </summary>
        public static bool TryParseWorkload(
            string value,
            out NavigationGridBenchmarkWorkload workload)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                EqualsAny(value, "stage4", "movement", "squad", "squadmovement", "strict", "strictformationbaseline"))
            {
                workload = NavigationGridBenchmarkWorkload.StrictFormationBaseline;
                return true;
            }

            if (EqualsAny(value, "stage3", "path", "pathandfield"))
            {
                workload = NavigationGridBenchmarkWorkload.PathAndField;
                return true;
            }

            if (EqualsAny(value, "stage6input", "input", "scale", "scaleinput", "scaleinputdeterminism"))
            {
                workload = NavigationGridBenchmarkWorkload.ScaleInputDeterminism;
                return true;
            }

            if (EqualsAny(value, "free", "cohort", "freecohortmovement"))
            {
                workload = NavigationGridBenchmarkWorkload.FreeCohortMovement;
                return true;
            }

            if (EqualsAny(value, "avoidance", "orca"))
            {
                workload = NavigationGridBenchmarkWorkload.Avoidance;
                return true;
            }

            if (EqualsAny(value, "collision", "worldcollision"))
            {
                workload = NavigationGridBenchmarkWorkload.Collision;
                return true;
            }

            workload = default;
            return false;
        }

        /// <summary>
        /// 返回阶段六各导航阶段的 P95 主线程预算
        /// </summary>
        public static double GetMainThreadP95BudgetMilliseconds(
            NavigationGridBenchmarkStage stage)
        {
            return stage switch
            {
                NavigationGridBenchmarkStage.CommandIngress => 0.25,
                NavigationGridBenchmarkStage.CohortPartition => 0.50,
                NavigationGridBenchmarkStage.OverlayAndTargetResolve => 0.50,
                NavigationGridBenchmarkStage.FieldRequestCollect => 0.50,
                NavigationGridBenchmarkStage.FieldBuildAndPublish => 0.75,
                NavigationGridBenchmarkStage.GoalRegionAssignment => 0.75,
                NavigationGridBenchmarkStage.NeighborGrid => 1.25,
                NavigationGridBenchmarkStage.PreferredVelocity => 0.75,
                NavigationGridBenchmarkStage.Avoidance => 1.50,
                NavigationGridBenchmarkStage.Collision => 0.75,
                NavigationGridBenchmarkStage.CommitAndProgress => 0.50,
                _ => 0.0,
            };
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                if (string.Equals(value, candidates[index], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 保存同一批规模输入的三类确定性 Hash
    /// </summary>
    public readonly struct NavigationGridScaleInputHashes
    {
        public readonly ulong CohortPartitionHash;
        public readonly ulong GoalRegionHash;
        public readonly ulong RequestKeyHash;

        public NavigationGridScaleInputHashes(
            ulong cohortPartitionHash,
            ulong goalRegionHash,
            ulong requestKeyHash)
        {
            CohortPartitionHash = cohortPartitionHash;
            GoalRegionHash = goalRegionHash;
            RequestKeyHash = requestKeyHash;
        }
    }

    /// <summary>
    /// 生成不经过 RPC FixedList 的阶段六规模输入并计算稳定 Hash
    /// </summary>
    public static class NavigationGridScaleInputAlgorithms
    {
        private const ulong HashOffset = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private const float GoldenAngle = 2.39996323f;

        /// <summary>
        /// 把稳定成员写入 ECS DynamicBuffer 并生成自然目标区域采样点
        /// </summary>
        public static void PopulateMembers(
            DynamicBuffer<NavigationGridScaleInputMember> members,
            NavigationGridBenchmarkConfig config,
            float3 targetPosition)
        {
            int count = math.max(1, config.AgentCount);
            members.ResizeUninitialized(count);
            for (int index = 0; index < count; index++)
            {
                int cohortIndex = index / NavigationGridBenchmarkScaleProfile.MaximumCohortSize;
                members[index] = new NavigationGridScaleInputMember
                {
                    StableId = index + 1,
                    CohortIndex = cohortIndex,
                    SpawnPosition = NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                        index,
                        count,
                        config.SpawnColumnCount,
                        config.SpawnSpacing,
                        config.SpawnOrigin,
                        config.RandomSeed),
                    GoalPosition = CalculateGoalRegionPosition(
                        index,
                        targetPosition,
                        config.AgentRadius),
                    RequestKey = CalculateRequestKey(config, cohortIndex, targetPosition),
                };
            }
        }

        /// <summary>
        /// 根据 DynamicBuffer 的最终内容计算 Cohort、目标区域和请求 Key Hash
        /// </summary>
        public static NavigationGridScaleInputHashes ComputeHashes(
            DynamicBuffer<NavigationGridScaleInputMember> members)
        {
            ulong cohortHash = HashOffset;
            ulong goalHash = HashOffset;
            ulong requestHash = HashOffset;
            int previousCohortIndex = -1;

            for (int index = 0; index < members.Length; index++)
            {
                NavigationGridScaleInputMember member = members[index];
                cohortHash = Mix(cohortHash, (uint)member.StableId);
                cohortHash = Mix(cohortHash, (uint)member.CohortIndex);

                goalHash = Mix(goalHash, (uint)member.StableId);
                goalHash = Mix(goalHash, Quantize(member.GoalPosition.x));
                goalHash = Mix(goalHash, Quantize(member.GoalPosition.y));
                goalHash = Mix(goalHash, Quantize(member.GoalPosition.z));

                if (member.CohortIndex != previousCohortIndex)
                {
                    requestHash = Mix(requestHash, (uint)member.CohortIndex);
                    requestHash = Mix(requestHash, (uint)member.RequestKey);
                    requestHash = Mix(requestHash, (uint)(member.RequestKey >> 32));
                    previousCohortIndex = member.CohortIndex;
                }
            }

            return new NavigationGridScaleInputHashes(cohortHash, goalHash, requestHash);
        }

        /// <summary>
        /// 计算给定成员数在当前硬上限下需要的 Cohort 数
        /// </summary>
        public static int CalculateCohortCount(int agentCount)
        {
            int count = math.max(1, agentCount);
            int capacity = NavigationGridBenchmarkScaleProfile.MaximumCohortSize;
            return (count + capacity - 1) / capacity;
        }

        private static float3 CalculateGoalRegionPosition(
            int index,
            float3 targetPosition,
            float agentRadius)
        {
            // 黄金角螺旋只定义目标区域采样输入，不会把成员绑定为固定阵型
            float spacing = math.max(0.1f, agentRadius * 2.15f);
            float radius = spacing * math.sqrt(index + 0.5f);
            float angle = index * GoldenAngle;
            math.sincos(angle, out float sine, out float cosine);
            return targetPosition + new float3(cosine * radius, 0f, sine * radius);
        }

        private static ulong CalculateRequestKey(
            NavigationGridBenchmarkConfig config,
            int cohortIndex,
            float3 targetPosition)
        {
            int firstMemberIndex =
                cohortIndex * NavigationGridBenchmarkScaleProfile.MaximumCohortSize;
            float3 cohortStart = NavigationBenchmarkInputAlgorithms.CalculateSpawnPosition(
                firstMemberIndex,
                config.AgentCount,
                config.SpawnColumnCount,
                config.SpawnSpacing,
                config.SpawnOrigin,
                config.RandomSeed);

            ulong hash = HashOffset;
            hash = Mix(hash, (uint)cohortIndex);
            hash = Mix(hash, Quantize(cohortStart.x));
            hash = Mix(hash, Quantize(cohortStart.z));
            hash = Mix(hash, Quantize(targetPosition.x));
            hash = Mix(hash, Quantize(targetPosition.z));
            hash = Mix(hash, Quantize(config.AgentRadius));
            return hash;
        }

        private static uint Quantize(float value)
        {
            return unchecked((uint)(int)math.round(value * 1000f));
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * HashPrime;
        }
    }
}
