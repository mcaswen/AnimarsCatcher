#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AnimarsCatcher.Gameplay;
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
    /// 执行阶段四 Squad 生命周期、阵型和开阔地移动自动验收
    /// </summary>
    public static class NavigationGridStageFourValidation
    {
        private const float DeltaTime = 1f / 60f;

        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Four Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 从无界面 Unity 进程运行阶段四全部自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 执行阵型、World 过滤、生命周期和开阔地移动验收
        /// </summary>
        public static void RunAll()
        {
            // 先验证纯算法和 World 边界，再运行会创建实体和异步 Job 的场景夹具
            TestFormationLayouts();
            TestWorldFiltersAndPipelineOrder();
            TestSquadMembershipLifecycle();
            TestMovementBenchmarkScales();
            Debug.Log("Navigation Grid 阶段四自动验收通过");
        }

        private static void TestFormationLayouts()
        {
            int[] counts = { 32, 64, 128 };

            // 三档规模共用同一算法入口，防止只验证小规模的偶然布局
            // Column 和 CompactRectangle 都必须覆盖，前者验证中心线不变量
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                // 两种布局共用同一 memberCount，避免测试夹具自身改变对称性
                ValidateFormation(counts[countIndex], AniSquadFormationKind.Column, 1);
                ValidateFormation(counts[countIndex], AniSquadFormationKind.CompactRectangle, 8);
            }
        }

        private static void ValidateFormation(
            int memberCount,
            AniSquadFormationKind kind,
            int configuredColumns)
        {
            var offsets = new float3[memberCount];
            float3 center = float3.zero;

            // 先保存全部局部偏移，后续同时检查有限性、唯一性和中心矩
            for (int slotIndex = 0; slotIndex < memberCount; slotIndex++)
            {
                float3 offset = AniSquadFormationAlgorithms.CalculateSlotOffset(
                    slotIndex,
                    memberCount,
                    kind,
                    configuredColumns,
                    1.8f,
                    2.5f);
                // 槽位算法必须对所有索引返回有限坐标，后续比较才有意义
                Assert(math.all(math.isfinite(offset)), "阵型槽位出现非有限坐标");
                if (kind == AniSquadFormationKind.Column)
                {
                    // Column 的横向不变量是所有槽位都落在 Anchor 中心线上
                    Assert(math.abs(offset.x) <= 0.0001f, "纵队槽位没有保持单列");
                }

                for (int previous = 0; previous < slotIndex; previous++)
                {
                    // 逐项比较保证错误布局不会通过后续的中心平均检查
                    Assert(
                        math.distancesq(offsets[previous], offset) > 0.0001f,
                        $"{memberCount} Ani 阵型出现重复槽位");
                }

                offsets[slotIndex] = offset;
                center += offset;
            }

            center /= memberCount;

            // 对称性使用平均偏移验证，避免依赖特定偶数或奇数成员数
            Assert(
                math.lengthsq(center) <= 0.0001f,
                $"{memberCount} Ani 阵型没有围绕 Anchor 中心对称");
        }

        private static void TestWorldFiltersAndPipelineOrder()
        {
            // Server 和 Client 的系统清单分别读取，验证权威逻辑不会泄漏到客户端
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);

            // 显式列出权威类型，新增移动 System 时验收会强制补齐过滤测试
            Type[] authoritativeSystems =
            {
                typeof(ServerAniCommandIngressSystem),
                typeof(AniSquadLifecycleSystem),
                typeof(AniSquadAnchorAdvanceSystem),
                typeof(AniFormationLayoutSystem),
                typeof(AniFormationAssignmentSystem),
                typeof(AniPreferredVelocitySystem),
                typeof(AniMovementCommitSystem),
                typeof(AniMovementProgressSystem),
                typeof(ServerNavigationGridMovementBenchmarkSystem),
            };

            // 指令入口、移动提交和 Benchmark 都必须只出现在 Server World
            for (int index = 0; index < authoritativeSystems.Length; index++)
            {
                // 同一类型同时出现在两类 World 时，客户端应明确排除权威写入系统
                Assert(
                    ContainsSystem(serverSystems, authoritativeSystems[index]),
                    $"Server World 缺少 {authoritativeSystems[index].Name}");
                Assert(
                    !ContainsSystem(clientSystems, authoritativeSystems[index]),
                    $"Client World 不应注册 {authoritativeSystems[index].Name}");
            }

            AssertUpdatedAfter<AniSquadTargetResolveSystem, AniSquadLifecycleSystem>();
            AssertUpdatedAfter<AniSquadPathRequestSystem, AniSquadTargetResolveSystem>();
            // 由 Squad 声明对 Grid 的单向依赖，避免 Grid Runtime 反向引用 Squad
            AssertUpdatedBefore<AniSquadPathRequestSystem, ServerNavigationGridFlowFieldSystem>();
            // 路径结果必须先完成，Anchor 才能读取当前 Field 方向
            AssertUpdatedAfter<AniSquadAnchorAdvanceSystem, ServerNavigationGridFlowFieldSystem>();
            AssertUpdatedAfter<AniFormationLayoutSystem, AniSquadAnchorAdvanceSystem>();
            AssertUpdatedAfter<AniFormationAssignmentSystem, AniFormationLayoutSystem>();
            AssertUpdatedAfter<AniSlotTargetSystem, AniFormationAssignmentSystem>();
            // 槽位目标和期望速度都必须先于唯一 Commit 写入
            AssertUpdatedAfter<AniPreferredVelocitySystem, AniSlotTargetSystem>();
            AssertUpdatedAfter<AniMovementCommitSystem, AniPreferredVelocitySystem>();
            AssertUpdatedAfter<AniMovementProgressSystem, AniMovementCommitSystem>();

            // 顺序断言覆盖目标解析、路径、Anchor、阵型、速度、提交和到达状态

            // Anchor 只保存空间状态，不绑定队伍中的某一个具体 Ani
            FieldInfo[] anchorFields = typeof(AniSquadAnchor).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int index = 0; index < anchorFields.Length; index++)
            {
                // 反射检查所有字段，防止后续新增 Entity 引用绕过架构约束
                Assert(
                    anchorFields[index].FieldType != typeof(Entity),
                    "AniSquadAnchor 不应绑定具体 Ani 实体");
            }
        }

        private static void TestSquadMembershipLifecycle()
        {
            // 生命周期测试不需要完整 Grid，只配置后端标记以运行服务器系统
            using var world = new World("Navigation Grid Stage Four Lifecycle", WorldFlags.Game);
            AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
            EntityManager entityManager = world.EntityManager;
            Entity[] anis =
            {
                CreateAni(entityManager, new float3(-1f, 0f, 0f)),
                CreateAni(entityManager, new float3(0f, 0f, 0f)),
                CreateAni(entityManager, new float3(1f, 0f, 0f)),
            };

            CreateCommand(entityManager, anis, sequence: 1);
            SystemHandle lifecycle = world.GetOrCreateSystem<AniSquadLifecycleSystem>();

            // 首个指令必须聚合为一个 Squad，并为每个成员补齐运行时组件
            lifecycle.Update(world.Unmanaged);
            Entity firstSquad = GetSingleSquad(entityManager);
            Assert(entityManager.GetBuffer<AniSquadMember>(firstSquad).Length == 3, "Squad 初始成员数量错误");

            CreateCommand(entityManager, new[] { anis[0], anis[1] }, sequence: 2);

            // 子集指令会拆出新 Squad，未选成员继续留在旧上下文
            lifecycle.Update(world.Unmanaged);
            using EntityQuery squadsAfterSplit = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(squadsAfterSplit.CalculateEntityCount() == 2, "成员离队后没有拆分 Squad 上下文");
            Entity newSquad = entityManager.GetComponentData<AniSquadMembership>(anis[0]).Squad;
            // 两名选中成员必须共享新 Squad，不能各自生成路径上下文
            Assert(
                entityManager.GetComponentData<AniSquadMembership>(anis[1]).Squad == newSquad,
                "同一新命令的成员没有进入同一 Squad");
            Assert(
                entityManager.GetComponentData<AniSquadMembership>(anis[2]).Squad == firstSquad,
                "未选中成员不应被新命令迁移");

            entityManager.DestroyEntity(anis[2]);

            // 旧 Squad 的最后成员死亡后应立即销毁路径和阵型资源
            lifecycle.Update(world.Unmanaged);
            Assert(!entityManager.Exists(firstSquad), "最后一个成员死亡后旧 Squad 没有拆除");

            entityManager.DestroyEntity(anis[0]);
            entityManager.DestroyEntity(anis[1]);
            lifecycle.Update(world.Unmanaged);
            // 所有成员失效后不能留下没有成员的孤立上下文
            using EntityQuery remainingSquads = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(remainingSquads.IsEmptyIgnoreFilter, "成员全部失效后仍残留 Squad");
        }

        private static void TestMovementBenchmarkScales()
        {
            int[] counts = { 32, 64, 128 };

            // 每档使用独立 World，避免前一档的请求版本或缓存污染下一档
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                RunMovementBenchmark(counts[countIndex]);
            }
        }

        private static void RunMovementBenchmark(int agentCount)
        {
            // 该夹具手动按生产顺序更新系统，验证链路而不是依赖编辑器自动组装
            using var world = new World($"Navigation Grid Stage Four {agentCount}", WorldFlags.Game);
            AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
            EntityManager entityManager = world.EntityManager;
            Entity configEntity = entityManager.CreateEntity(
                typeof(NavigationGridBenchmarkConfig),
                typeof(NavigationGridBenchmarkState),
                typeof(NavigationGridMovementBenchmarkState));
            entityManager.AddBuffer<NavigationGridBenchmarkCommand>(configEntity);
            entityManager.AddBuffer<NavigationGridBenchmarkTimingSample>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkTimingSample>(configEntity);

            // 所有 Buffer 必须在获取命令句柄前创建，避免后续结构变更使句柄失效
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                entityManager.GetBuffer<NavigationGridBenchmarkCommand>(configEntity);
            commands.Add(new NavigationGridBenchmarkCommand
            {
                Tick = 0,
                TargetOffset = new float3(-10f, 0f, 8f),
            });

            // 单条命令包含全部成员，用于验证路径请求按 Squad 而非 Ani 扩展
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.SquadMovement,
                AgentCount = agentCount,
                RandomSeed = 104729,
                WarmupTicks = 0,
                SampleTicks = 480,
                SpawnColumnCount = 16,
                SpawnSpacing = 1.25f,
                SpawnOrigin = new float3(105f, 0.57f, 44.43f),
                AgentRadius = 0.35f,
            });

            // 夹具先发布共享 Grid，再创建请求；顺序与生产 Benchmark 的资源依赖一致

            SystemHandle grid = world.GetOrCreateSystem<ServerNavigationGridBenchmarkGridSystem>();
            SystemHandle benchmark =
                world.GetOrCreateSystem<ServerNavigationGridMovementBenchmarkSystem>();
            SystemHandle lifecycle = world.GetOrCreateSystem<AniSquadLifecycleSystem>();
            SystemHandle target = world.GetOrCreateSystem<AniSquadTargetResolveSystem>();
            SystemHandle pathRequest = world.GetOrCreateSystem<AniSquadPathRequestSystem>();
            SystemHandle flow = world.GetOrCreateSystem<ServerNavigationGridFlowFieldSystem>();
            SystemHandle anchor = world.GetOrCreateSystem<AniSquadAnchorAdvanceSystem>();
            SystemHandle layout = world.GetOrCreateSystem<AniFormationLayoutSystem>();
            SystemHandle assignment = world.GetOrCreateSystem<AniFormationAssignmentSystem>();
            SystemHandle slotTarget = world.GetOrCreateSystem<AniSlotTargetSystem>();
            SystemHandle preferredVelocity = world.GetOrCreateSystem<AniPreferredVelocitySystem>();
            SystemHandle commit = world.GetOrCreateSystem<AniMovementCommitSystem>();
            SystemHandle progress = world.GetOrCreateSystem<AniMovementProgressSystem>();
            // 所有句柄在首次 Update 前创建，避免系统自动组更新顺序影响夹具
            grid.Update(world.Unmanaged);
            Entity gridEntity = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>()).GetSingletonEntity();
            NavigationGridReference gridReference = entityManager.GetComponentData<NavigationGridReference>(gridEntity);
            DynamicBuffer<NavigationDynamicOverlayCell> overlayCells =
                entityManager.AddBuffer<NavigationDynamicOverlayCell>(gridEntity);
            overlayCells.ResizeUninitialized(gridReference.Value.Value.Cells.Length);
            for (int index = 0; index < overlayCells.Length; index++)
            {
                overlayCells[index] = default;
            }
            DynamicBuffer<NavigationDynamicOverlayCluster> overlayClusters =
                entityManager.AddBuffer<NavigationDynamicOverlayCluster>(gridEntity);
            overlayClusters.ResizeUninitialized(gridReference.Value.Value.Clusters.Length);
            for (int index = 0; index < overlayClusters.Length; index++)
            {
                overlayClusters[index] = default;
            }
            entityManager.AddComponentData(
                gridEntity,
                new NavigationDynamicOverlayState { Version = 1, Initialized = 1 });
            entityManager.AddComponentData(gridEntity, default(NavigationGridJobActivity));

            bool completed = false;

            // Flow Field 可能跨 Tick 完成，循环必须覆盖异步搜索和成员收敛两个阶段
            for (int tick = 0; tick < 1080; tick++)
            {
                world.SetTime(new TimeData(tick * DeltaTime, DeltaTime));

                // Benchmark 先创建指令，生命周期随后消费；其余系统按生产依赖顺序推进
                benchmark.Update(world.Unmanaged);
                lifecycle.Update(world.Unmanaged);
                target.Update(world.Unmanaged);
                pathRequest.Update(world.Unmanaged);
                flow.Update(world.Unmanaged);
                JobHandle.ScheduleBatchedJobs();

                // 先完成异步 Job 调度，再运行依赖 Field 的 Anchor 和表现链路
                anchor.Update(world.Unmanaged);
                layout.Update(world.Unmanaged);
                assignment.Update(world.Unmanaged);
                slotTarget.Update(world.Unmanaged);
                preferredVelocity.Update(world.Unmanaged);
                commit.Update(world.Unmanaged);
                progress.Update(world.Unmanaged);

                NavigationGridMovementBenchmarkState state =
                    entityManager.GetComponentData<NavigationGridMovementBenchmarkState>(
                        configEntity);
                if (state.Completed != 0)
                {
                    // Completed 仍需检查 Failed，防止失败状态被 benchmark 终态掩盖
                    Assert(state.Failed == 0, $"{agentCount} Ani 群体移动报告失败");
                    completed = true;
                    break;
                }

                // 让异步寻路在每个验证 Tick 之间获得调度机会
                Thread.Yield();
            }

            Assert(completed, $"{agentCount} Ani 群体移动未在期限内完成");
            using EntityQuery squads = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(squads.CalculateEntityCount() == 1, "一次移动命令必须只生成一个 Squad 上下文");
            Entity squadEntity = squads.GetSingletonEntity();
            // 先确认单个上下文，再读取 PathState，避免多队伍时误取 Singleton
            AniSquadPathState pathState = entityManager.GetComponentData<AniSquadPathState>(
                squadEntity);
            Assert(pathState.Status == AniSquadMovementStatus.Completed, "Squad 未提交完成状态");

            // 同一指令的路径请求必须保持 Squad 级别，与成员数解耦
            Assert(pathState.FieldRequestCount == 1, "路径与 Field 请求数量没有按 Squad 增长");
            Assert(pathState.SuccessfulFieldRequestCount == 1, "Squad Field 请求没有成功完成");

            DynamicBuffer<AniSquadMember> members = entityManager.GetBuffer<AniSquadMember>(
                squadEntity);
            DynamicBuffer<AniFormationSlot> slots = entityManager.GetBuffer<AniFormationSlot>(
                squadEntity);
            Assert(members.Length == agentCount, "Squad 成员数量与测试规模不一致");
            Assert(slots.Length == agentCount, "阵型槽位数量与测试规模不一致");
            var usedSlots = new bool[agentCount];

            // 成员 Buffer 采用稳定槽位索引，验收重复、越界和唯一 Commit 写入
            // usedSlots 只用于测试断言，不参与运行时分配逻辑
            for (int index = 0; index < members.Length; index++)
            {
                int slotIndex = members[index].SlotIndex;
                Assert(slotIndex >= 0 && slotIndex < agentCount, "成员槽位索引越界");
                Assert(!usedSlots[slotIndex], "成员被分配到重复槽位");
                usedSlots[slotIndex] = true;
                AniMovementResult movementResult = entityManager.GetComponentData<AniMovementResult>(
                    members[index].Ani);

                // CommitCount 是唯一 Transform 写入所有权的可观测证据
                Assert(movementResult.CommitCount > 0, "Ani 没有经过唯一 Commit System 写入");
            }

            using EntityQuery pendingCommands = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquadCommandRequest>());
            // 指令实体只能短暂存在于入口组，验收结束时不应残留未消费请求
            Assert(pendingCommands.IsEmptyIgnoreFilter, "Benchmark 指令没有汇入统一 Squad 生命周期");
        }

        private static Entity CreateAni(EntityManager entityManager, float3 position)
        {
            // 夹具只创建生命周期所需的 Transform，运行时组件由指令消费补齐
            Entity entity = entityManager.CreateEntity(typeof(LocalTransform));
            entityManager.SetComponentData(
                entity,
                LocalTransform.FromPositionRotation(position, quaternion.identity));
            return entity;
        }

        private static void CreateCommand(
            EntityManager entityManager,
            Entity[] anis,
            uint sequence)
        {
            Entity commandEntity = entityManager.CreateEntity(
                typeof(AniSquadCommandRequest),
                typeof(AniSquadCommand));
            entityManager.SetComponentData(commandEntity, new AniSquadCommand
            {
                Sequence = sequence,
                OwnerNetworkId = 1,
                Mode = AniSquadCommandMode.MoveTo,
                Formation = AniSquadFormationKind.CompactRectangle,
                TargetPosition = new float3(10f, 0f, 10f),
                FormationColumnCount = 2,
                TargetStoppingDistance = 0.7f,
                DesiredForward = new float3(0f, 0f, 1f),
            });

            // 指令成员按稳定数组顺序写入，生命周期测试可复现拆队结果
            DynamicBuffer<AniSquadCommandMember> members =
                entityManager.AddBuffer<AniSquadCommandMember>(commandEntity);

            // 测试指令使用固定能力参数，确保不同规模只改变成员数量
            for (int index = 0; index < anis.Length; index++)
            {
                members.Add(new AniSquadCommandMember
                {
                    Ani = anis[index],
                    StableId = index,
                    MaxSpeed = 8f,
                    MaxAcceleration = 32f,
                    AgentRadius = 0.35f,
                });
            }
        }

        private static Entity GetSingleSquad(EntityManager entityManager)
        {
            // 该断言同时验证生命周期没有意外创建第二个上下文
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(query.CalculateEntityCount() == 1, "期望一个 Squad 上下文");
            return query.GetSingletonEntity();
        }

        private static void AssertUpdatedAfter<TSystem, TDependency>()
        {
            // 反射读取显式属性，避免只检查类型存在而漏掉实际更新顺序
            object[] attributes = typeof(TSystem).GetCustomAttributes(
                typeof(UpdateAfterAttribute),
                inherit: true);
            for (int index = 0; index < attributes.Length; index++)
            {
                if (((UpdateAfterAttribute)attributes[index]).SystemType == typeof(TDependency))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"{typeof(TSystem).Name} 缺少对 {typeof(TDependency).Name} 的 UpdateAfter 约束");
        }

        private static void AssertUpdatedBefore<TSystem, TDependency>()
        {
            // UpdateBefore 与 UpdateAfter 表达相同顺序时优先放在高层消费者一侧
            object[] attributes = typeof(TSystem).GetCustomAttributes(
                typeof(UpdateBeforeAttribute),
                inherit: true);
            for (int index = 0; index < attributes.Length; index++)
            {
                if (((UpdateBeforeAttribute)attributes[index]).SystemType == typeof(TDependency))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"{typeof(TSystem).Name} 缺少对 {typeof(TDependency).Name} 的 UpdateBefore 约束");
        }

        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type target)
        {
            // 使用线性扫描保持 Editor 验收无额外分配，系统数量很小
            for (int index = 0; index < systems.Count; index++)
            {
                // 类型比较避免依赖系统名称或程序集加载顺序
                if (systems[index] == target)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Assert(bool condition, string message)
        {
            // 统一抛出异常让 batchmode 返回失败，而不是只写一条普通日志
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
