#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证 6A.4 并行单位移动、成员进度归约和万人确定性
    /// </summary>
    public static class NavigationGridStageSixAFourValidation
    {
        private const int AgentCount = 10000;
        private const int WarmupTicks = 8;
        private const int SampleTicks = 120;
        private const float DeltaTime = 1f / 60f;

        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Six A Four Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.4 自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 检查任务注册、万人零分配移动和重复回放确定性
        /// </summary>
        public static void RunAll()
        {
            TestWorkloadAndSystemRegistration();
            MovementReplayResult first = RunMovementReplay("Stage Six A Four First");
            MovementReplayResult second = RunMovementReplay("Stage Six A Four Second");

            Assert(first.PositionHash == second.PositionHash,
                "万人并行移动重复回放的最终位置 Hash 不一致");
            Assert(first.ArrivedCount == AgentCount && second.ArrivedCount == AgentCount,
                "万人并行移动没有全部到达目标");
            Assert(first.MainThreadAllocatedBytes == 0 && second.MainThreadAllocatedBytes == 0,
                "万人并行移动采样 Tick 出现托管分配");
            Debug.Log(
                $"Navigation Grid 6A.4 自动验收通过：Ani={AgentCount}，" +
                $"Commit={first.CommitCountPerAni}，PositionHash={first.PositionHash:X16}，" +
                $"MainThreadAlloc={first.MainThreadAllocatedBytes} B");
        }

        private static void TestWorkloadAndSystemRegistration()
        {
            Assert(
                NavigationGridBenchmarkScaleProfile.IsImplementedWorkload(
                    NavigationGridBenchmarkWorkload.FreeCohortMovement),
                "万人 Harness 尚未开放自由 Cohort 移动工作负载");
            Assert(
                NavigationGridBenchmarkScaleProfile.RecordsFullServerTick(
                    NavigationGridBenchmarkWorkload.FreeCohortMovement),
                "自由 Cohort 移动没有进入完整服务器 Tick 采样");
            Assert(
                NavigationGridBenchmarkScaleProfile.TryValidateRun(
                    NavigationGridBenchmarkWorkload.FreeCohortMovement,
                    AgentCount,
                    out _),
                "万人自由 Cohort 移动参数没有通过 Harness 校验");

            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            Type[] requiredSystems =
            {
                typeof(AniFreePreferredVelocitySystem),
                typeof(AniMovementCommitSystem),
                typeof(AniFreeMovementProgressSystem),
            };
            for (int index = 0; index < requiredSystems.Length; index++)
            {
                Assert(ContainsSystem(serverSystems, requiredSystems[index]),
                    $"Server World 缺少 {requiredSystems[index].Name}");
                Assert(!ContainsSystem(clientSystems, requiredSystems[index]),
                    $"{requiredSystems[index].Name} 不应注册到 Client World");
            }

            // 具体工作类型必须保留 IJobEntity 契约，避免系统以后退回主线程逐 Ani 循环
            Type navigationAssemblyMarker = typeof(AniFreePreferredVelocitySystem);
            string namespaceName = navigationAssemblyMarker.Namespace;
            AssertJobType(navigationAssemblyMarker, $"{namespaceName}.AniFreePreferredVelocityJob");
            AssertJobType(navigationAssemblyMarker, $"{namespaceName}.AniCohortMovementCommitJob");
            AssertJobType(navigationAssemblyMarker, $"{namespaceName}.AniFreeCohortProgressJob");
            AssertJobType(navigationAssemblyMarker, $"{namespaceName}.AniMovementOrderProgressJob");
        }

        private static MovementReplayResult RunMovementReplay(string worldName)
        {
            using var world = new World(worldName, WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            entityManager.CreateEntity(typeof(GridMovementBackendEnabled));
            Entity configEntity = entityManager.CreateEntity(typeof(NavigationGridBenchmarkConfig));
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.FreeCohortMovement,
                AgentCount = AgentCount,
            });

            SystemHandle gridSystem = world.GetOrCreateSystem<
                ServerNavigationGridBenchmarkGridSystem>();
            SystemHandle velocitySystem = world.GetOrCreateSystem<
                AniFreePreferredVelocitySystem>();
            SystemHandle commitSystem = world.GetOrCreateSystem<AniMovementCommitSystem>();
            SystemHandle progressSystem = world.GetOrCreateSystem<
                AniFreeMovementProgressSystem>();
            gridSystem.Update(world.Unmanaged);

            using EntityQuery gridQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            NavigationGridReference gridReference =
                gridQuery.GetSingleton<NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            int startCellIndex = 20 + 20 * grid.Width;
            float3 cellCenter = NavigationGridQuery.GetCellWorldPosition(ref grid, startCellIndex);
            float3 startPosition = cellCenter + new float3(-0.2f, 0f, 0f);
            float3 targetPosition = cellCenter + new float3(0.2f, 0f, 0f);

            Entity orderEntity = CreateMovementData(
                entityManager,
                startCellIndex,
                startPosition,
                targetPosition,
                out NativeArray<Entity> anis);
            try
            {
                int completedTicks = 0;
                for (int tick = 0; tick < WarmupTicks; tick++)
                {
                    RunMovementTick(
                        world,
                        velocitySystem,
                        commitSystem,
                        progressSystem,
                        tick);
                    completedTicks++;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int tick = 0; tick < SampleTicks; tick++)
                {
                    RunMovementTick(
                        world,
                        velocitySystem,
                        commitSystem,
                        progressSystem,
                        WarmupTicks + tick);
                    completedTicks++;
                }
                long allocatedBytes =
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                AniMovementOrderState orderState =
                    entityManager.GetComponentData<AniMovementOrderState>(orderEntity);
                Assert(orderState.Status == AniMovementOrderStatus.Completed,
                    "万人并行移动请求没有完成 Cohort 归约");
                int arrivedCount = 0;
                ulong positionHash = 14695981039346656037UL;
                for (int index = 0; index < anis.Length; index++)
                {
                    AniMovementResult result =
                        entityManager.GetComponentData<AniMovementResult>(anis[index]);
                    Assert(result.CommitCount == completedTicks,
                        "唯一提交次数与有效模拟 Tick 数不一致");
                    Assert(result.TargetVersion == 1,
                        "成员结果没有携带当前目标版本");
                    arrivedCount += result.Settled;
                    float3 position = entityManager.GetComponentData<LocalTransform>(anis[index])
                        .Position;
                    positionHash = Mix(positionHash, position.x);
                    positionHash = Mix(positionHash, position.z);
                }

                return new MovementReplayResult
                {
                    PositionHash = positionHash,
                    ArrivedCount = arrivedCount,
                    CommitCountPerAni = completedTicks,
                    MainThreadAllocatedBytes = allocatedBytes,
                };
            }
            finally
            {
                anis.Dispose();
            }
        }

        private static Entity CreateMovementData(
            EntityManager entityManager,
            int targetCellIndex,
            float3 startPosition,
            float3 targetPosition,
            out NativeArray<Entity> anis)
        {
            Entity flowRecord = entityManager.CreateEntity();
            // 故意保持空 Field，验证 Ani 离开稀疏覆盖后仍能沿可直达路径移动
            entityManager.AddBuffer<NavigationFlowFieldCell>(flowRecord);
            Entity orderEntity = entityManager.CreateEntity(
                typeof(AniMovementOrder),
                typeof(AniMovementOrderState));
            entityManager.SetComponentData(orderEntity, new AniMovementOrder
            {
                Sequence = 1,
                Mode = AniSquadCommandMode.MoveTo,
                TargetPosition = targetPosition,
            });
            entityManager.SetComponentData(orderEntity, new AniMovementOrderState
            {
                Status = AniMovementOrderStatus.Active,
                ValidMemberCount = AgentCount,
                TargetVersion = 1,
            });
            DynamicBuffer<AniMovementOrderCohort> orderCohorts =
                entityManager.AddBuffer<AniMovementOrderCohort>(orderEntity);

            int cohortCount = (AgentCount + AniMovementCohortAlgorithms.DefaultMemberCapacity - 1) /
                              AniMovementCohortAlgorithms.DefaultMemberCapacity;
            EntityArchetype cohortArchetype = entityManager.CreateArchetype(
                typeof(AniMovementCohort),
                typeof(AniMovementCohortTarget),
                typeof(AniMovementCohortPathState),
                typeof(NavigationFlowFieldState),
                typeof(NavigationFlowFieldHandle),
                typeof(AniMovementCohortMember));
            using var cohorts = new NativeArray<Entity>(cohortCount, Allocator.Temp);
            entityManager.CreateEntity(cohortArchetype, cohorts);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                int memberCount = math.min(
                    AniMovementCohortAlgorithms.DefaultMemberCapacity,
                    AgentCount - index * AniMovementCohortAlgorithms.DefaultMemberCapacity);
                entityManager.SetComponentData(cohortEntity, new AniMovementCohort
                {
                    CohortId = unchecked((uint)(index + 1)),
                    Order = orderEntity,
                    MemberCount = memberCount,
                    TargetVersion = 1,
                });
                entityManager.SetComponentData(cohortEntity, new AniMovementCohortTarget
                {
                    Mode = AniSquadCommandMode.MoveTo,
                });
                entityManager.SetComponentData(cohortEntity, new AniMovementCohortPathState
                {
                    Status = AniMovementCohortStatus.Moving,
                    ActiveRequestVersion = 1,
                });
                entityManager.SetComponentData(cohortEntity, new NavigationFlowFieldState
                {
                    Status = NavigationPathStatus.Succeeded,
                    RequestVersion = 1,
                });
                entityManager.SetComponentData(cohortEntity, new NavigationFlowFieldHandle
                {
                    Record = flowRecord,
                    RequestVersion = 1,
                });
                orderCohorts.Add(new AniMovementOrderCohort { Cohort = cohortEntity });
            }

            EntityArchetype aniArchetype = entityManager.CreateArchetype(
                typeof(LocalTransform),
                typeof(AniMovementCohortMembership),
                typeof(AniMovementConfig),
                typeof(AniGoalAssignment),
                typeof(AniPreferredVelocity),
                typeof(AniMovementResult));
            anis = new NativeArray<Entity>(AgentCount, Allocator.Persistent);
            entityManager.CreateEntity(aniArchetype, anis);
            for (int index = 0; index < anis.Length; index++)
            {
                int cohortIndex = index / AniMovementCohortAlgorithms.DefaultMemberCapacity;
                Entity ani = anis[index];
                entityManager.SetComponentData(
                    ani,
                    LocalTransform.FromPositionRotation(startPosition, quaternion.identity));
                entityManager.SetComponentData(ani, new AniMovementCohortMembership
                {
                    Cohort = cohorts[cohortIndex],
                    CohortId = unchecked((uint)(cohortIndex + 1)),
                    StableId = index + 1,
                    AgentProfile = 1,
                });
                entityManager.SetComponentData(ani, new AniMovementConfig
                {
                    MaxSpeed = 2f,
                    MaxAcceleration = 20f,
                    AgentRadius = 0.35f,
                    ArrivalRadius = 0.05f,
                    RotationSpeedRadians = math.radians(540f),
                });
                entityManager.SetComponentData(ani, new AniGoalAssignment
                {
                    TargetCellIndex = targetCellIndex,
                    TargetPosition = targetPosition,
                    ArrivalRadius = 0.05f,
                    InfluenceRadius = 0.1f,
                    TargetVersion = 1,
                });
            }

            // 所有结构变更结束后再填写成员 Buffer，后续任务可以稳定并行读取
            for (int cohortIndex = 0; cohortIndex < cohorts.Length; cohortIndex++)
            {
                DynamicBuffer<AniMovementCohortMember> members =
                    entityManager.GetBuffer<AniMovementCohortMember>(cohorts[cohortIndex]);
                int firstMember = cohortIndex * AniMovementCohortAlgorithms.DefaultMemberCapacity;
                int memberEnd = math.min(
                    AgentCount,
                    firstMember + AniMovementCohortAlgorithms.DefaultMemberCapacity);
                for (int memberIndex = firstMember; memberIndex < memberEnd; memberIndex++)
                {
                    members.Add(new AniMovementCohortMember
                    {
                        Ani = anis[memberIndex],
                        StableId = memberIndex + 1,
                    });
                }
            }

            return orderEntity;
        }

        private static void RunMovementTick(
            World world,
            SystemHandle velocitySystem,
            SystemHandle commitSystem,
            SystemHandle progressSystem,
            int tick)
        {
            world.SetTime(new TimeData(tick * DeltaTime, DeltaTime));
            velocitySystem.Update(world.Unmanaged);
            commitSystem.Update(world.Unmanaged);
            progressSystem.Update(world.Unmanaged);
            // 每次采样等待本 Tick 完成，不能用积压的任务队列伪造主线程耗时
            world.EntityManager.CompleteAllTrackedJobs();
        }

        private static void AssertJobType(Type assemblyMarker, string typeName)
        {
            Type jobType = assemblyMarker.Assembly.GetType(typeName);
            Assert(jobType != null && typeof(IJobEntity).IsAssignableFrom(jobType),
                $"{typeName} 没有实现 IJobEntity");
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

        private static ulong Mix(ulong hash, float value)
        {
            hash ^= unchecked((uint)(int)math.round(value * 1000f));
            return hash * 1099511628211UL;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private struct MovementReplayResult
        {
            public ulong PositionHash;
            public int ArrivedCount;
            public int CommitCountPerAni;
            public long MainThreadAllocatedBytes;
        }
    }
}
#endif
