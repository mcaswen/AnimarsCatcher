#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证 6A.2 Cohort 切分、自然目标区域、自由移动和成员生命周期
    /// </summary>
    public static class NavigationGridStageSixATwoValidation
    {
        private const float DeltaTime = 1f / 60f;
        private const int MaximumMovementTicks = 1800;

        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Six A Two Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.2 自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次检查万人切分、生命周期、四档到达和重复回放确定性
        /// </summary>
        public static void RunAll()
        {
            TestAlgorithmBoundaries();
            TestSystemRegistration();

            PartitionReplayResult firstPartition = RunPartitionReplay(runLifecycleChecks: true);
            // 第二轮使用独立 World，Hash 一致才能证明结果不依赖运行时 Entity
            PartitionReplayResult secondPartition = RunPartitionReplay(runLifecycleChecks: false);
            Assert(firstPartition.MemberCount == 10000, "万人订单没有完整进入 Cohort");
            Assert(firstPartition.MaximumCohortSize <= 64, "Cohort 超过默认 64 人容量");
            Assert(firstPartition.PartitionHash == secondPartition.PartitionHash,
                "相同万人输入得到不同 Cohort 切分 Hash");

            RunMovementReplay(32);
            RunMovementReplay(64);
            RunMovementReplay(128);
            // 512 档重复运行，同时承担 6A.2 基础规模与最终位置确定性验收
            MovementReplayResult first512 = RunMovementReplay(512);
            MovementReplayResult second512 = RunMovementReplay(512);
            Assert(first512.PartitionHash == second512.PartitionHash,
                "512 Ani 重复回放的 Cohort Hash 不一致");
            Assert(first512.GoalHash == second512.GoalHash,
                "512 Ani 重复回放的目标区域 Hash 不一致");
            Assert(first512.PositionHash == second512.PositionHash,
                "512 Ani 重复回放的最终位置 Hash 不一致");

            Debug.Log(
                $"Navigation Grid 6A.2 自动验收通过：万人 Cohort={firstPartition.CohortCount}，" +
                $"最大成员={firstPartition.MaximumCohortSize}，" +
                $"PartitionHash={firstPartition.PartitionHash:X16}，" +
                $"512GoalHash={first512.GoalHash:X16}，" +
                $"512PositionHash={first512.PositionHash:X16}");
        }

        private static void TestAlgorithmBoundaries()
        {
            // 纯算法断言先拦截容量和空间键退化，避免万人场景只报告模糊的生命周期失败
            var settings = new AniMovementCohortSettings
            {
                PreferredMemberCapacity = 512,
                MaximumMemberCapacity = 512,
            };
            Assert(
                AniMovementCohortAlgorithms.ResolveMemberCapacity(settings) ==
                AniMovementCohortAlgorithms.HardMemberCapacity,
                "异常配置绕过了 Cohort 硬上限");

            int capacity = AniMovementCohortAlgorithms.CalculateCellCapacity(
                1f,
                0.2f,
                1f,
                out int slotsPerAxis);
            Assert(capacity == 4 && slotsPerAxis == 2, "目标 Cell 容量计算不符合体型间距");
            Assert(
                AniMovementCohortAlgorithms.CalculateMortonKey(new int2(2, 3)) !=
                AniMovementCohortAlgorithms.CalculateMortonKey(new int2(3, 2)),
                "Morton Key 没有区分交换后的 Cell 坐标");
        }

        private static void TestSystemRegistration()
        {
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            Type[] requiredSystems =
            {
                typeof(AniMovementCohortPartitionSystem),
                typeof(AniCohortTargetResolveSystem),
                typeof(AniGoalRegionAssignmentSystem),
                typeof(AniMovementCohortPathRequestSystem),
                typeof(AniFreePreferredVelocitySystem),
                typeof(AniFreeMovementProgressSystem),
            };

            for (int index = 0; index < requiredSystems.Length; index++)
            {
                Type systemType = requiredSystems[index];
                Assert(ContainsSystem(serverSystems, systemType),
                    $"Server World 缺少 {systemType.Name}");
                Assert(!ContainsSystem(clientSystems, systemType),
                    $"{systemType.Name} 不应注册到 Client World");
            }
        }

        private static PartitionReplayResult RunPartitionReplay(bool runLifecycleChecks)
        {
            using var world = new World("Stage Six A Two Partition", WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            CreateBackendAndBenchmarkConfig(entityManager, 10000);
            SystemHandle gridSystem = world.GetOrCreateSystem<
                ServerNavigationGridBenchmarkGridSystem>();
            SystemHandle partitionSystem = world.GetOrCreateSystem<
                AniMovementCohortPartitionSystem>();
            gridSystem.Update(world.Unmanaged);

            // 万人回放只执行切分和生命周期，不把 6A.3 前的 Field 调度冒充性能结果
            using NativeArray<Entity> anis = CreateAnis(
                entityManager,
                10000,
                new float3(50.5f, 0.57f, 18.5f),
                92,
                1f);
            Entity orderEntity = CreateOrder(
                entityManager,
                anis,
                1,
                new float3(126.5f, 0.57f, 48.5f));
            world.SetTime(new TimeData(0, DeltaTime));
            partitionSystem.Update(world.Unmanaged);

            PartitionReplayResult result = InspectPartition(
                entityManager,
                orderEntity,
                expectedMemberCount: 10000);
            using EntityQuery squads = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(squads.IsEmptyIgnoreFilter,
                "正式 MovementOrder 仍然创建了严格阵型 Squad");

            if (runLifecycleChecks)
            {
                // 销毁一名成员后再次运行切分系统，验证 Cohort 和订单汇总同步收缩
                Entity removedAni = anis[0];
                entityManager.DestroyEntity(removedAni);
                world.SetTime(new TimeData(DeltaTime, DeltaTime));
                partitionSystem.Update(world.Unmanaged);
                InspectPartition(entityManager, orderEntity, expectedMemberCount: 9999);

                // 新订单覆盖旧订单末尾 130 人，未选中的旧成员应继续原命令
                var replacementMembers = new NativeArray<Entity>(
                    130,
                    Allocator.Temp);
                for (int index = 0; index < replacementMembers.Length; index++)
                {
                    replacementMembers[index] = anis[anis.Length - 1 - index];
                }

                Entity replacementOrder = CreateOrder(
                    entityManager,
                    replacementMembers,
                    2,
                    new float3(120.5f, 0.57f, 42.5f),
                    firstStableId: 10000,
                    descendingStableIds: true);
                replacementMembers.Dispose();
                world.SetTime(new TimeData(DeltaTime * 2, DeltaTime));
                partitionSystem.Update(world.Unmanaged);
                InspectPartition(entityManager, replacementOrder, expectedMemberCount: 130);
                AssertUniqueLiveMemberships(entityManager, 9999);

                AniMovementOrderState oldState =
                    entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
                Assert(oldState.ValidMemberCount == 9869,
                    "新命令没有从旧订单移走重叠成员");
            }

            return result;
        }

        private static MovementReplayResult RunMovementReplay(int agentCount)
        {
            using var world = new World(
                $"Stage Six A Two Movement {agentCount}",
                WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            CreateBackendAndBenchmarkConfig(entityManager, agentCount);

            SystemHandle gridSystem = world.GetOrCreateSystem<
                ServerNavigationGridBenchmarkGridSystem>();
            SystemHandle partitionSystem = world.GetOrCreateSystem<
                AniMovementCohortPartitionSystem>();
            SystemHandle targetSystem = world.GetOrCreateSystem<
                AniCohortTargetResolveSystem>();
            SystemHandle goalSystem = world.GetOrCreateSystem<
                AniGoalRegionAssignmentSystem>();
            SystemHandle pathSystem = world.GetOrCreateSystem<
                AniMovementCohortPathRequestSystem>();
            SystemHandle flowSystem = world.GetOrCreateSystem<
                ServerNavigationGridFlowFieldSystem>();
            SystemHandle velocitySystem = world.GetOrCreateSystem<
                AniFreePreferredVelocitySystem>();
            SystemHandle commitSystem = world.GetOrCreateSystem<
                AniMovementCommitSystem>();
            SystemHandle progressSystem = world.GetOrCreateSystem<
                AniFreeMovementProgressSystem>();

            gridSystem.Update(world.Unmanaged);
            PrepareOverlay(entityManager);
            int columns = math.min(32, agentCount);
            using NativeArray<Entity> anis = CreateAnis(
                entityManager,
                agentCount,
                new float3(50.5f, 0.57f, 40.5f),
                columns,
                1f);
            Entity orderEntity = CreateOrder(
                entityManager,
                anis,
                1,
                new float3(126.5f, 0.57f, 48.5f));

            bool completed = false;
            for (int tick = 0; tick < MaximumMovementTicks; tick++)
            {
                world.SetTime(new TimeData(tick * DeltaTime, DeltaTime));
                partitionSystem.Update(world.Unmanaged);
                targetSystem.Update(world.Unmanaged);
                goalSystem.Update(world.Unmanaged);
                pathSystem.Update(world.Unmanaged);
                flowSystem.Update(world.Unmanaged);
                JobHandle.ScheduleBatchedJobs();
                // Flow 跨帧写回后再运行速度、唯一提交和进度归约
                velocitySystem.Update(world.Unmanaged);
                commitSystem.Update(world.Unmanaged);
                progressSystem.Update(world.Unmanaged);

                AniMovementOrderState orderState =
                    entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
                Assert(orderState.Status != AniMovementOrderStatus.Failed,
                    $"{agentCount} Ani 自由移动进入失败状态");
                if (orderState.Status == AniMovementOrderStatus.Completed)
                {
                    completed = true;
                    break;
                }

                Thread.Yield();
            }

            Assert(
                completed,
                BuildMovementFailureReason(entityManager, orderEntity, anis, agentCount));
            AniMovementOrderState finalState =
                entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
            AssertAllMembersArrived(entityManager, anis);
            return new MovementReplayResult
            {
                PartitionHash = finalState.CohortPartitionHash,
                GoalHash = finalState.GoalRegionHash,
                PositionHash = CalculatePositionHash(entityManager, anis),
            };
        }

        private static NativeArray<Entity> CreateAnis(
            EntityManager entityManager,
            int count,
            float3 minimum,
            int columns,
            float spacing)
        {
            EntityArchetype archetype = entityManager.CreateArchetype(typeof(LocalTransform));
            // 批量创建控制验收启动成本，位置仍按稳定索引逐项写入
            var anis = new NativeArray<Entity>(count, Allocator.Temp);
            entityManager.CreateEntity(archetype, anis);
            for (int index = 0; index < count; index++)
            {
                int x = index % math.max(1, columns);
                int z = (index / math.max(1, columns)) % 56;
                entityManager.SetComponentData(
                    anis[index],
                    LocalTransform.FromPositionRotation(
                        minimum + new float3(x * spacing, 0f, z * spacing),
                        quaternion.identity));
            }

            return anis;
        }

        private static Entity CreateOrder(
            EntityManager entityManager,
            NativeArray<Entity> anis,
            uint sequence,
            float3 targetPosition,
            int firstStableId = 1,
            bool descendingStableIds = false)
        {
            Entity orderEntity = entityManager.CreateEntity(
                typeof(AniMovementOrder),
                typeof(AniMovementOrderRequest));
            entityManager.SetComponentData(orderEntity, new AniMovementOrder
            {
                Sequence = sequence,
                OwnerNetworkId = 1,
                SelectionVersion = sequence,
                SelectionHash = sequence,
                CreatedTick = sequence,
                CancellationVersion = sequence,
                Mode = AniSquadCommandMode.MoveTo,
                TargetPosition = targetPosition,
                TargetStoppingDistance = 0.7f,
                GoalCellCapacityScale = 1f,
                GoalInfluenceRadius = 4f,
            });
            DynamicBuffer<AniMovementOrderMember> members =
                entityManager.AddBuffer<AniMovementOrderMember>(orderEntity);
            // 验收直接构造服务器订单，网络分块和权限边界已由 6A.1 独立覆盖
            for (int index = 0; index < anis.Length; index++)
            {
                members.Add(new AniMovementOrderMember
                {
                    Ani = anis[index],
                    GhostId = descendingStableIds
                        ? firstStableId - index
                        : firstStableId + index,
                    MaxSpeed = 12f,
                    MaxAcceleration = 48f,
                    AgentRadius = 0.35f,
                    AgentProfile = 1,
                });
            }

            return orderEntity;
        }

        private static PartitionReplayResult InspectPartition(
            EntityManager entityManager,
            Entity orderEntity,
            int expectedMemberCount)
        {
            AniMovementOrderState orderState =
                entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
            Assert(orderState.Status == AniMovementOrderStatus.Active,
                "MovementOrder 没有进入活动状态");
            Assert(orderState.ValidMemberCount == expectedMemberCount,
                "MovementOrder 成员汇总不符合预期");

            int cohortCount = 0;
            int memberCount = 0;
            int maximumCohortSize = 0;
            using EntityQuery cohortQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementCohort>(),
                ComponentType.ReadOnly<AniMovementCohortMember>());
            using NativeArray<Entity> cohorts = cohortQuery.ToEntityArray(Allocator.Temp);
            // 只汇总目标订单，生命周期用例中旧订单和替换订单会同时存在
            for (int index = 0; index < cohorts.Length; index++)
            {
                AniMovementCohort cohort =
                    entityManager.GetComponentData<AniMovementCohort>(cohorts[index]);
                if (cohort.Order != orderEntity)
                {
                    continue;
                }

                DynamicBuffer<AniMovementCohortMember> members =
                    entityManager.GetBuffer<AniMovementCohortMember>(cohorts[index], true);
                Assert(members.Length <= AniMovementCohortAlgorithms.DefaultMemberCapacity,
                    "Cohort 成员数超过默认容量");
                Assert(!entityManager.HasBuffer<AniFormationSlot>(cohorts[index]),
                    "自由移动 Cohort 不应保存严格阵型槽位");
                cohortCount++;
                memberCount += members.Length;
                maximumCohortSize = math.max(maximumCohortSize, members.Length);
            }

            Assert(memberCount == expectedMemberCount, "Cohort 成员发生重复或丢失");
            return new PartitionReplayResult
            {
                MemberCount = memberCount,
                CohortCount = cohortCount,
                MaximumCohortSize = maximumCohortSize,
                PartitionHash = orderState.CohortPartitionHash,
            };
        }

        private static void AssertUniqueLiveMemberships(
            EntityManager entityManager,
            int expectedMemberCount)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementCohortMembership>());
            Assert(query.CalculateEntityCount() == expectedMemberCount,
                "成员死亡或换令后留下重复或悬空归属");
            using NativeArray<Entity> anis = query.ToEntityArray(Allocator.Temp);
            // Membership 指向的 Cohort 必须仍然存在，数量正确不足以发现悬空引用
            for (int index = 0; index < anis.Length; index++)
            {
                AniMovementCohortMembership membership =
                    entityManager.GetComponentData<AniMovementCohortMembership>(anis[index]);
                Assert(entityManager.Exists(membership.Cohort), "Ani 指向已销毁的 Cohort");
            }
        }

        private static void AssertAllMembersArrived(
            EntityManager entityManager,
            NativeArray<Entity> anis)
        {
            for (int index = 0; index < anis.Length; index++)
            {
                AniGoalAssignment goal =
                    entityManager.GetComponentData<AniGoalAssignment>(anis[index]);
                LocalTransform transform =
                    entityManager.GetComponentData<LocalTransform>(anis[index]);
                AniMovementResult result =
                    entityManager.GetComponentData<AniMovementResult>(anis[index]);
                float distance = math.length(
                    new float2(
                        goal.TargetPosition.x - transform.Position.x,
                        goal.TargetPosition.z - transform.Position.z));
                Assert(distance <= goal.ArrivalRadius + 0.001f,
                    "Ani 没有到达自己的目标区域落点");
                Assert(result.CommitCount > 0, "Ani 没有经过唯一 Commit System");
            }
        }

        private static string BuildMovementFailureReason(
            EntityManager entityManager,
            Entity orderEntity,
            NativeArray<Entity> anis,
            int agentCount)
        {
            // 超时诊断保留首个未到达成员和每个 Cohort 状态，便于区分寻路与收敛问题
            AniMovementOrderState orderState =
                entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
            float maximumDistance = 0f;
            int unsettledCount = 0;
            string firstUnsettled = string.Empty;
            for (int index = 0; index < anis.Length; index++)
            {
                if (!entityManager.HasComponent<AniGoalAssignment>(anis[index]))
                {
                    unsettledCount++;
                    continue;
                }

                AniGoalAssignment goal =
                    entityManager.GetComponentData<AniGoalAssignment>(anis[index]);
                LocalTransform transform =
                    entityManager.GetComponentData<LocalTransform>(anis[index]);
                float distance = math.length(
                    new float2(
                        goal.TargetPosition.x - transform.Position.x,
                        goal.TargetPosition.z - transform.Position.z));
                maximumDistance = math.max(maximumDistance, distance);
                if (distance > goal.ArrivalRadius)
                {
                    unsettledCount++;
                    if (string.IsNullOrEmpty(firstUnsettled))
                    {
                        AniPreferredVelocity velocity =
                            entityManager.GetComponentData<AniPreferredVelocity>(anis[index]);
                        firstUnsettled =
                            $"，首个未到达=I{index}/P{transform.Position}/G{goal.TargetPosition}/" +
                            $"V{velocity.Value}/R{goal.InfluenceRadius:F3}";
                    }
                }
            }

            var details = new StringBuilder();
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementCohort>(),
                ComponentType.ReadOnly<AniMovementCohortPathState>(),
                ComponentType.ReadOnly<NavigationFlowFieldState>());
            using NativeArray<Entity> cohorts = query.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                AniMovementCohort cohort =
                    entityManager.GetComponentData<AniMovementCohort>(cohorts[index]);
                if (cohort.Order != orderEntity)
                {
                    continue;
                }

                AniMovementCohortPathState path =
                    entityManager.GetComponentData<AniMovementCohortPathState>(cohorts[index]);
                NavigationFlowFieldState field =
                    entityManager.GetComponentData<NavigationFlowFieldState>(cohorts[index]);
                details.Append($" C{cohort.CohortId}:{path.Status}/{field.Status}/S{path.SettledTicks}");
            }

            return $"{agentCount} Ani 未在固定窗口内完成自由移动，" +
                   $"Order={orderState.Status}，未到达={unsettledCount}，" +
                   $"最大距离={maximumDistance:F3}{firstUnsettled}，Cohort={details}";
        }

        private static ulong CalculatePositionHash(
            EntityManager entityManager,
            NativeArray<Entity> anis)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            // 采用创建顺序和毫米量化坐标，浮点尾差不会掩盖可见位置变化
            for (int index = 0; index < anis.Length; index++)
            {
                float3 position = entityManager.GetComponentData<LocalTransform>(anis[index])
                    .Position;
                hash = (hash ^ unchecked((uint)(int)math.round(position.x * 1000f))) * prime;
                hash = (hash ^ unchecked((uint)(int)math.round(position.z * 1000f))) * prime;
            }

            return hash;
        }

        private static void CreateBackendAndBenchmarkConfig(
            EntityManager entityManager,
            int agentCount)
        {
            entityManager.CreateEntity(typeof(GridMovementBackendEnabled));
            Entity configEntity = entityManager.CreateEntity(typeof(NavigationGridBenchmarkConfig));
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.FreeCohortMovement,
                AgentCount = agentCount,
            });
        }

        private static void PrepareOverlay(EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            Entity gridEntity = query.GetSingletonEntity();
            // 合成开放 Grid 使用空 Overlay，仍走正式 Flow 系统需要的完整数据形状
            NavigationGridReference gridReference =
                entityManager.GetComponentData<NavigationGridReference>(gridEntity);
            DynamicBuffer<NavigationDynamicOverlayCell> cells =
                entityManager.AddBuffer<NavigationDynamicOverlayCell>(gridEntity);
            cells.ResizeUninitialized(gridReference.Value.Value.Cells.Length);
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = default;
            }
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters =
                entityManager.AddBuffer<NavigationDynamicOverlayCluster>(gridEntity);
            clusters.ResizeUninitialized(gridReference.Value.Value.Clusters.Length);
            for (int index = 0; index < clusters.Length; index++)
            {
                clusters[index] = default;
            }
            entityManager.AddComponentData(gridEntity, new NavigationDynamicOverlayState
            {
                Version = 1,
                Initialized = 1,
            });
            entityManager.AddComponentData(gridEntity, new NavigationGridJobActivity());
        }

        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type expected)
        {
            for (int index = 0; index < systems.Count; index++)
            {
                if (systems[index] == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private struct PartitionReplayResult
        {
            public int MemberCount;
            public int CohortCount;
            public int MaximumCohortSize;
            public ulong PartitionHash;
        }

        private struct MovementReplayResult
        {
            public ulong PartitionHash;
            public ulong GoalHash;
            public ulong PositionHash;
        }
    }
}
#endif
