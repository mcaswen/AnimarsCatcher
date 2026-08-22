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
    /// 自动验证队伍的创建与拆分、阵型槽位、系统运行范围和开阔地移动
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
        /// 供无界面的 Unity 进程执行阶段四全部验证
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次验证阵型算法、World 过滤、队伍生命周期和开阔地移动
        /// </summary>
        public static void RunAll()
        {
            // 先检查纯算法和系统注册范围，再运行包含 Entity 与异步 Job 的完整测试场景
            TestFormationLayouts();
            TestWorldFiltersAndPipelineOrder();
            TestSquadMembershipLifecycle();
            TestMovementBenchmarkScales();
            Debug.Log("Navigation Grid 阶段四自动验收通过");
        }

        private static void TestFormationLayouts()
        {
            int[] counts = { 32, 64, 128 };

            // 32、64、128 人都使用正式槽位算法，避免只验证小队时碰巧正确
            // 同时覆盖单列纵队和紧凑矩形，确认纵队成员始终位于中心线
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                // 两种阵型使用相同人数，便于直接比较布局对称性
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

            // 先保存全部相对位置，再检查数值有效、槽位不重复和整体居中
            for (int slotIndex = 0; slotIndex < memberCount; slotIndex++)
            {
                float3 offset = AniSquadFormationAlgorithms.CalculateSlotOffset(
                    slotIndex,
                    memberCount,
                    kind,
                    configuredColumns,
                    1.8f,
                    2.5f);
                // 每个槽位都必须得到有限坐标，才能继续比较和求中心
                Assert(math.all(math.isfinite(offset)), "阵型槽位出现非有限坐标");
                if (kind == AniSquadFormationKind.Column)
                {
                    // 纵队的所有槽位都应位于队伍锚点的横向中心线上
                    Assert(math.abs(offset.x) <= 0.0001f, "纵队槽位没有保持单列");
                }

                for (int previous = 0; previous < slotIndex; previous++)
                {
                    // 同时逐项检查，避免左右错误刚好在平均值中互相抵消
                    Assert(
                        math.distancesq(offsets[previous], offset) > 0.0001f,
                        $"{memberCount} Ani 阵型出现重复槽位");
                }

                offsets[slotIndex] = offset;
                center += offset;
            }

            center /= memberCount;

            // 使用所有成员的平均偏移验证整体居中，奇数和偶数人数都适用
            Assert(
                math.lengthsq(center) <= 0.0001f,
                $"{memberCount} Ani 阵型没有围绕 Anchor 中心对称");
        }

        private static void TestWorldFiltersAndPipelineOrder()
        {
            // 分别读取服务器和客户端系统列表，确保移动逻辑只注册到服务器
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);

            // 明确列出所有只能在服务器运行的系统，新增系统后若忘记补充 World 过滤，验证会立即失败
            Type[] authoritativeSystems =
            {
                typeof(ServerAniCommandIngressSystem),
                typeof(AniSquadLifecycleSystem),
                typeof(AniSquadAnchorAdvanceSystem),
                typeof(AniAdaptiveFormationSystem),
                typeof(AniFormationLayoutSystem),
                typeof(AniFormationAssignmentSystem),
                typeof(AniPreferredVelocitySystem),
                typeof(AniMovementCommitSystem),
                typeof(AniMovementProgressSystem),
                typeof(ServerNavigationGridMovementBenchmarkSystem),
            };

            // 指令入口、位置写回和基准系统都只能运行在服务器 World
            for (int index = 0; index < authoritativeSystems.Length; index++)
            {
                // 每个系统都必须出现在服务器列表中，同时不能出现在客户端列表中
                Assert(
                    ContainsSystem(serverSystems, authoritativeSystems[index]),
                    $"Server World 缺少 {authoritativeSystems[index].Name}");
                Assert(
                    !ContainsSystem(clientSystems, authoritativeSystems[index]),
                    $"Client World 不应注册 {authoritativeSystems[index].Name}");
            }

            AssertUpdatedAfter<AniSquadTargetResolveSystem, AniSquadLifecycleSystem>();
            AssertUpdatedAfter<AniSquadPathRequestSystem, AniSquadTargetResolveSystem>();
            // 队伍系统依赖底层网格系统，底层网格不应反向依赖队伍逻辑
            AssertUpdatedBefore<AniSquadPathRequestSystem, ServerNavigationGridFlowFieldSystem>();
            // Flow Field 结果先更新，队伍锚点随后才能读取本帧方向
            AssertUpdatedAfter<AniSquadAnchorAdvanceSystem, ServerNavigationGridFlowFieldSystem>();
            AssertUpdatedAfter<AniAdaptiveFormationSystem, AniSquadAnchorAdvanceSystem>();
            AssertUpdatedBefore<AniAdaptiveFormationSystem, AniFormationLayoutSystem>();
            AssertUpdatedAfter<AniFormationLayoutSystem, AniSquadAnchorAdvanceSystem>();
            AssertUpdatedAfter<AniFormationAssignmentSystem, AniFormationLayoutSystem>();
            AssertUpdatedAfter<AniSlotTargetSystem, AniFormationAssignmentSystem>();
            // 先生成槽位目标和期望速度，最后才由唯一提交系统写 Transform
            AssertUpdatedAfter<AniPreferredVelocitySystem, AniSlotTargetSystem>();
            AssertUpdatedAfter<AniMovementCommitSystem, AniPreferredVelocitySystem>();
            AssertUpdatedAfter<AniMovementProgressSystem, AniMovementCommitSystem>();

            // 检查目标解析、寻路、锚点、阵型、速度、位置写回和到达判断的完整顺序

            // 队伍锚点是独立的虚拟中心，不能引用某一名具体 Ani
            FieldInfo[] anchorFields = typeof(AniSquadAnchor).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int index = 0; index < anchorFields.Length; index++)
            {
                // 用反射检查所有字段，今后新增 Entity 字段也会触发失败
                Assert(
                    anchorFields[index].FieldType != typeof(Entity),
                    "AniSquadAnchor 不应绑定具体 Ani 实体");
            }
        }

        private static void TestSquadMembershipLifecycle()
        {
            // 生命周期测试不需要实际寻路，只启用网格移动后端让服务器系统运行
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

            // 第一条指令应把全部成员组成一支队伍，并补齐移动所需组件
            lifecycle.Update(world.Unmanaged);
            Entity firstSquad = GetSingleSquad(entityManager);
            Assert(entityManager.GetBuffer<AniSquadMember>(firstSquad).Length == 3, "Squad 初始成员数量错误");

            CreateCommand(entityManager, new[] { anis[0], anis[1] }, sequence: 2);

            // 只选择部分成员的新指令应拆出新队伍，未选成员继续留在原队伍
            lifecycle.Update(world.Unmanaged);
            using EntityQuery squadsAfterSplit = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(squadsAfterSplit.CalculateEntityCount() == 2, "成员离队后没有拆分 Squad 上下文");
            Entity newSquad = entityManager.GetComponentData<AniSquadMembership>(anis[0]).Squad;
            // 两名选中成员必须加入同一支新队伍，不能各自创建寻路上下文
            Assert(
                entityManager.GetComponentData<AniSquadMembership>(anis[1]).Squad == newSquad,
                "同一新命令的成员没有进入同一 Squad");
            Assert(
                entityManager.GetComponentData<AniSquadMembership>(anis[2]).Squad == firstSquad,
                "未选中成员不应被新命令迁移");

            entityManager.DestroyEntity(anis[2]);

            // 原队伍最后一名成员销毁后，其寻路和阵型数据也应立即销毁
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

            // 每种人数使用独立 World，避免前一轮请求和缓存影响下一轮
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                RunMovementBenchmark(counts[countIndex]);
            }
        }

        private static void RunMovementBenchmark(int agentCount)
        {
            // 测试按正式运行顺序手动更新各系统，直接验证完整调用链
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
            entityManager.AddBuffer<NavigationGridMovementBenchmarkStateTrace>(configEntity);
            entityManager.AddBuffer<NavigationGridMovementBenchmarkAgentTrace>(configEntity);

            // 先创建所有缓冲区，再取得命令缓冲区句柄，避免后续结构变化使句柄失效
            DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                entityManager.GetBuffer<NavigationGridBenchmarkCommand>(configEntity);
            commands.Add(new NavigationGridBenchmarkCommand
            {
                Tick = 0,
                TargetOffset = new float3(-10f, 0f, 8f),
            });

            // 一条命令包含全部成员，用来确认寻路请求按队伍创建，而不是每名 Ani 一份
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.StrictFormationBaseline,
                AgentCount = agentCount,
                RandomSeed = 104729,
                WarmupTicks = 0,
                SampleTicks = 480,
                SpawnColumnCount = 16,
                SpawnSpacing = 1.25f,
                SpawnOrigin = new float3(105f, 0.57f, 44.43f),
                AgentRadius = 0.35f,
                RecordMovementTrace = 1,
            });

            // 先发布共享导航网格，再创建队伍指令，与正式基准的资源准备顺序一致

            SystemHandle grid = world.GetOrCreateSystem<ServerNavigationGridBenchmarkGridSystem>();
            SystemHandle benchmark =
                world.GetOrCreateSystem<ServerNavigationGridMovementBenchmarkSystem>();
            SystemHandle lifecycle = world.GetOrCreateSystem<AniSquadLifecycleSystem>();
            SystemHandle target = world.GetOrCreateSystem<AniSquadTargetResolveSystem>();
            SystemHandle pathRequest = world.GetOrCreateSystem<AniSquadPathRequestSystem>();
            SystemHandle flow = world.GetOrCreateSystem<ServerNavigationGridFlowFieldSystem>();
            SystemHandle anchor = world.GetOrCreateSystem<AniSquadAnchorAdvanceSystem>();
            SystemHandle adaptive = world.GetOrCreateSystem<AniAdaptiveFormationSystem>();
            SystemHandle layout = world.GetOrCreateSystem<AniFormationLayoutSystem>();
            SystemHandle assignment = world.GetOrCreateSystem<AniFormationAssignmentSystem>();
            SystemHandle slotTarget = world.GetOrCreateSystem<AniSlotTargetSystem>();
            SystemHandle preferredVelocity = world.GetOrCreateSystem<AniPreferredVelocitySystem>();
            SystemHandle commit = world.GetOrCreateSystem<AniMovementCommitSystem>();
            SystemHandle progress = world.GetOrCreateSystem<AniMovementProgressSystem>();
            // 所有系统句柄在第一次更新前创建，测试不依赖编辑器自动组装系统组
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

            // Flow Field 会跨帧完成，循环既要等待异步寻路，也要等待成员站稳
            for (int tick = 0; tick < 1080; tick++)
            {
                world.SetTime(new TimeData(tick * DeltaTime, DeltaTime));

                // 基准系统先创建指令，生命周期系统随后处理，其余系统按正式依赖顺序更新
                benchmark.Update(world.Unmanaged);
                lifecycle.Update(world.Unmanaged);
                target.Update(world.Unmanaged);
                pathRequest.Update(world.Unmanaged);
                flow.Update(world.Unmanaged);
                JobHandle.ScheduleBatchedJobs();

                // 先让异步任务调度和写回，再更新依赖 Flow Field 的锚点与成员移动
                anchor.Update(world.Unmanaged);
                adaptive.Update(world.Unmanaged);
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
                    // 基准结束后仍要检查 Failed，避免固定窗口状态掩盖真实失败
                    Assert(state.Failed == 0, $"{agentCount} Ani 群体移动报告失败");
                    completed = true;
                    break;
                }

                // 每个验证帧之间刷新任务调度，让异步寻路获得运行机会
                Thread.Yield();
            }

            Assert(completed, $"{agentCount} Ani 群体移动未在期限内完成");
            using EntityQuery squads = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(squads.CalculateEntityCount() == 1, "一次移动命令必须只生成一个 Squad 上下文");
            Entity squadEntity = squads.GetSingletonEntity();
            // 先确认只有一支队伍，再读取 PathState，避免错误地把多队结果当作单例
            AniSquadPathState pathState = entityManager.GetComponentData<AniSquadPathState>(
                squadEntity);
            Assert(pathState.Status == AniSquadMovementStatus.Completed, "Squad 未提交完成状态");

            int configuredColumnCount = math.min(8, agentCount);
            AniSquadFormationState formation = entityManager.GetComponentData<
                AniSquadFormationState>(squadEntity);
            Assert(
                formation.ConfiguredColumnCount == configuredColumnCount,
                "Squad 没有持久保存指令配置列数");
            Assert(
                formation.ColumnCount == configuredColumnCount,
                "开阔地完成态阵型意外缩列");

            // 同一条队伍指令只应产生一份寻路请求，与成员人数无关
            Assert(pathState.FieldRequestCount == 1, "路径与 Field 请求数量没有按 Squad 增长");
            Assert(pathState.SuccessfulFieldRequestCount == 1, "Squad Field 请求没有成功完成");

            DynamicBuffer<AniSquadMember> members = entityManager.GetBuffer<AniSquadMember>(
                squadEntity);
            DynamicBuffer<AniFormationSlot> slots = entityManager.GetBuffer<AniFormationSlot>(
                squadEntity);
            Assert(members.Length == agentCount, "Squad 成员数量与测试规模不一致");
            Assert(slots.Length == agentCount, "阵型槽位数量与测试规模不一致");
            var usedSlots = new bool[agentCount];

            // 检查所有成员槽位索引不重复、不越界，并确认位置只由提交系统写入
            // usedSlots 仅用于测试中检查重复槽位
            for (int index = 0; index < members.Length; index++)
            {
                int slotIndex = members[index].SlotIndex;
                Assert(slotIndex >= 0 && slotIndex < agentCount, "成员槽位索引越界");
                Assert(!usedSlots[slotIndex], "成员被分配到重复槽位");
                usedSlots[slotIndex] = true;
                AniMovementResult movementResult = entityManager.GetComponentData<AniMovementResult>(
                    members[index].Ani);

                // CommitCount 用于确认每名成员确实经过唯一的 Transform 提交流程
                Assert(movementResult.CommitCount > 0, "Ani 没有经过唯一 Commit System 写入");
            }

            // 完成后继续运行一段时间，确认锚点朝向或阵型更新不会让已完成指令重新移动
            for (int stabilityTick = 0; stabilityTick < 30; stabilityTick++)
            {
                world.SetTime(new TimeData((1080 + stabilityTick) * DeltaTime, DeltaTime));
                target.Update(world.Unmanaged);
                pathRequest.Update(world.Unmanaged);
                flow.Update(world.Unmanaged);
                JobHandle.ScheduleBatchedJobs();
                anchor.Update(world.Unmanaged);
                adaptive.Update(world.Unmanaged);
                layout.Update(world.Unmanaged);
                assignment.Update(world.Unmanaged);
                slotTarget.Update(world.Unmanaged);
                preferredVelocity.Update(world.Unmanaged);
                commit.Update(world.Unmanaged);
                progress.Update(world.Unmanaged);

                pathState = entityManager.GetComponentData<AniSquadPathState>(squadEntity);
                Assert(
                    pathState.Status == AniSquadMovementStatus.Completed,
                    "完成态 Squad 被成员或阵型更新重新激活");
            }

            formation = entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            Assert(
                formation.ColumnCount == configuredColumnCount,
                "完成态稳定窗口内阵型列数发生变化");

            using EntityQuery pendingCommands = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquadCommandRequest>());
            // 指令 Entity 只会短暂存在于入口组，检查结束时不能残留尚未处理的请求
            Assert(pendingCommands.IsEmptyIgnoreFilter, "Benchmark 指令没有汇入统一 Squad 生命周期");
        }

        private static Entity CreateAni(EntityManager entityManager, float3 position)
        {
            // 测试成员开始时只有 Transform，其余运行时组件必须由生命周期系统补齐
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

            // 指令成员按固定数组顺序写入，使拆队结果可重复
            DynamicBuffer<AniSquadCommandMember> members =
                entityManager.AddBuffer<AniSquadCommandMember>(commandEntity);

            // 各种人数使用相同移动参数，保证测试变量只有成员数量
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
            // 该检查也能发现生命周期系统意外创建了第二支队伍
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniSquad>());
            Assert(query.CalculateEntityCount() == 1, "期望一个 Squad 上下文");
            return query.GetSingletonEntity();
        }

        private static void AssertUpdatedAfter<TSystem, TDependency>()
        {
            // 用反射读取 UpdateBefore 和 UpdateAfter，检查实际声明的系统顺序
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
            // UpdateBefore 和 UpdateAfter 都可以表达相同关系，检查时同时支持两种写法
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
            // 系统类型数量很少，直接扫描即可
            for (int index = 0; index < systems.Count; index++)
            {
                // 按 Type 比较，不依赖显示名称或程序集加载顺序
                if (systems[index] == target)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Assert(bool condition, string message)
        {
            // 失败时抛出异常，让 Batch Mode 通过退出码报告错误
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
