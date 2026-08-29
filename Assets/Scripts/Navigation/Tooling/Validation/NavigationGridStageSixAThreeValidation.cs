#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 验证 6A.3 共享 Field、预算调度、取消超时和 Overlay 快照
    /// </summary>
    public static class NavigationGridStageSixAThreeValidation
    {
        private const float DeltaTime = 1f / 60f;
        private const int MaximumTicks = 600;

        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Six A Three Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.3 自动验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行 6A.5 目标场共享与局部刷新验收
        /// </summary>
        public static void RunStageSixAFiveFromCommandLine()
        {
            TestGoalRegionSharingAndLocalRefresh();
            Debug.Log("Navigation Grid 6A.5 目标场专项验收通过");
        }

        /// <summary>
        /// 依次检查系统边界、请求归并、预算队列、Overlay 快照和内存淘汰
        /// </summary>
        public static void RunAll()
        {
            TestSystemRegistration();
            TestSharedRecordAndQueueMetrics();
            TestPriorityAndTimeout();
            TestCancellationAndOverlaySnapshot();
            TestGridReplacementDuringPublish();
            TestByteBudgetEviction();
            Debug.Log("Navigation Grid 6A.3 自动验收通过");
        }

        private static void TestSystemRegistration()
        {
            // 共享 Store 只属于服务器，Client World 不应承担搜索内存和调度状态
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            Assert(ContainsSystem(serverSystems, typeof(ServerNavigationSharedFlowFieldSystem)),
                "Server World 缺少共享 Flow Field 调度器");
            Assert(!ContainsSystem(clientSystems, typeof(ServerNavigationSharedFlowFieldSystem)),
                "共享 Flow Field 调度器不应注册到 Client World");
            // 类型约束防止后续重构把唯一 Key 构建退回主线程循环
            Assert(typeof(IJobParallelFor).IsAssignableFrom(
                    typeof(NavigationSharedFlowFieldBuildJob)),
                "唯一 Field Key 没有使用可并行的独立构建 Job");
        }

        private static void TestSharedRecordAndQueueMetrics()
        {
            using var world = CreateWorld("Stage Six A Three Shared Record");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out _);
            Entity storeEntity = GetStoreEntity(entityManager);
            SetSettings(entityManager, storeEntity, maximumBuilds: 4, timeoutTicks: 120);

            var cohorts = new NativeArray<Entity>(8, Allocator.Temp);
            float3 start = GetCellPosition(entityManager, new int2(10, 10));
            float3 end = GetCellPosition(entityManager, new int2(30, 24));
            for (int index = 0; index < cohorts.Length; index++)
            {
                cohorts[index] = CreateRequestCohort(
                    entityManager,
                    start,
                    end,
                    (uint)(index + 1),
                    priority: 1);
            }

            // 八个完全相同的 Key 必须等待同一份构建结果，不能各自复制 Field
            DriveUntil(world, scheduler, () => AllTerminal(entityManager, cohorts));
            Assert(AllSucceeded(entityManager, cohorts),
                BuildStateSummary(entityManager, cohorts));
            NavigationFlowFieldSchedulerState schedulerState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(schedulerState.CumulativeUniqueBuildCount == 1,
                "相同 Field Key 被重复构建");
            Assert(schedulerState.CumulativeSharedHitCount == cohorts.Length - 1,
                "同批等待者没有共享唯一构建结果");
            Assert(schedulerState.StoreRecordCount == 1,
                "唯一 Field Key 没有只生成一条 Store Record");

            // 每个消费者都应指向同一 Record，Cohort 自带的兼容 Buffer 必须保持为空
            Entity sharedRecord = Entity.Null;
            for (int index = 0; index < cohorts.Length; index++)
            {
                NavigationFlowFieldHandle handle = entityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohorts[index]);
                if (index == 0)
                {
                    sharedRecord = handle.Record;
                }
                Assert(handle.Record == sharedRecord && sharedRecord != Entity.Null,
                    "相同请求没有取得同一共享 Handle");
                Assert(entityManager.GetBuffer<NavigationFlowFieldCell>(cohorts[index]).IsEmpty,
                    "共享命中仍向 Cohort 复制了完整 Flow Field");
            }
            Assert(entityManager.GetBuffer<NavigationFlowFieldCell>(sharedRecord).Length > 0,
                "Store Record 没有保存可消费的 Flow Field");

            DynamicBuffer<NavigationFlowFieldQueueWaitSample> samples = entityManager.GetBuffer<
                NavigationFlowFieldQueueWaitSample>(storeEntity, true);
            Assert(samples.Length == cohorts.Length,
                "共享请求没有逐项记录排队样本");
            AssertPercentilesOrdered(samples);
            cohorts.Dispose();
        }

        private static void TestPriorityAndTimeout()
        {
            using var world = CreateWorld("Stage Six A Three Priority");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out _);
            Entity storeEntity = GetStoreEntity(entityManager);
            SetSettings(entityManager, storeEntity, maximumBuilds: 1, timeoutTicks: 1);

            // 两个不同 Key 同时入队，用单构建预算观察优先级和滞留超时
            Entity lowPriority = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(10, 10)),
                GetCellPosition(entityManager, new int2(20, 18)),
                1,
                priority: 1);
            Entity highPriority = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(60, 40)),
                GetCellPosition(entityManager, new int2(70, 46)),
                2,
                priority: 9);

            UpdateScheduler(world, scheduler, 0);
            Assert(entityManager.GetComponentData<NavigationFlowFieldState>(highPriority).Status ==
                   NavigationPathStatus.Searching,
                "高优先级请求没有先取得唯一构建预算");
            Assert(entityManager.GetComponentData<NavigationFlowFieldState>(lowPriority).Status ==
                   NavigationPathStatus.Pending,
                "低优先级请求绕过每 Tick 构建预算");

            DriveUntil(
                world,
                scheduler,
                () => IsTerminal(entityManager, highPriority) &&
                      IsTerminal(entityManager, lowPriority),
                firstTick: 1);
            Assert(entityManager.GetComponentData<NavigationFlowFieldState>(highPriority).Status ==
                   NavigationPathStatus.Succeeded,
                "高优先级请求没有完成构建");
            NavigationFlowFieldState lowState = entityManager.GetComponentData<
                NavigationFlowFieldState>(lowPriority);
            Assert(lowState.Status == NavigationPathStatus.Failed &&
                   lowState.FailureReason == NavigationPathFailureReason.TimedOut,
                "超出排队预算的请求没有进入可诊断超时状态");
            NavigationFlowFieldSchedulerState schedulerState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(schedulerState.CumulativeTimeoutCount == 1,
                "调度器没有累计排队超时指标");
        }

        private static void TestCancellationAndOverlaySnapshot()
        {
            using var world = CreateWorld("Stage Six A Three Overlay Snapshot");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out SystemHandle overlaySystem);
            Entity storeEntity = GetStoreEntity(entityManager);
            SetSettings(entityManager, storeEntity, maximumBuilds: 2, timeoutTicks: 120);

            // 两条 Corridor 相隔足够远，局部失效不应把它们当成同一影响范围
            Entity affected = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(10, 10)),
                GetCellPosition(entityManager, new int2(13, 13)),
                1,
                priority: 2);
            Entity unaffected = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(68, 41)),
                GetCellPosition(entityManager, new int2(71, 45)),
                2,
                priority: 2);

            // 先让两个请求进入 Worker，再在主线程发布新的 Overlay 版本
            UpdateScheduler(world, scheduler, 0);
            uint previousOverlayVersion = GetOverlayState(entityManager).Version;
            AddOverlayDelta(entityManager, new int2(14, 14), sourceId: 1);
            world.SetTime(new TimeData(DeltaTime, DeltaTime));
            overlaySystem.Update(world.Unmanaged);
            Assert(GetOverlayState(entityManager).Version != previousOverlayVersion,
                "共享 Field Job 活跃时 Overlay 仍被阻塞");

            // 构建期间替换请求版本，旧结果只能成为共享缓存，不能覆盖新版本状态
            NavigationFlowFieldRequest replacement = entityManager.GetComponentData<
                NavigationFlowFieldRequest>(unaffected);
            replacement.PathRequest.Version = 3;
            replacement.CancellationVersion = 3;
            entityManager.SetComponentData(unaffected, replacement);
            entityManager.SetComponentData(
                unaffected,
                NavigationFlowFieldState.CreatePending(3));

            var cohorts = new NativeArray<Entity>(2, Allocator.Temp);
            cohorts[0] = affected;
            cohorts[1] = unaffected;
            DriveUntil(world, scheduler, () => AllSucceeded(entityManager, cohorts), firstTick: 2);
            NavigationFlowFieldSchedulerState schedulerState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(schedulerState.CumulativeCancelledCount == 1,
                "构建期间被替换的请求没有计入取消指标");

            Entity affectedRecord = entityManager.GetComponentData<NavigationFlowFieldHandle>(
                affected).Record;
            Entity unaffectedRecord = entityManager.GetComponentData<NavigationFlowFieldHandle>(
                unaffected).Record;
            // 障碍只落在第一条 Corridor，第二个 Record 应继续保持同一引用
            AddOverlayDelta(entityManager, new int2(12, 14), sourceId: 2);
            world.SetTime(new TimeData(DeltaTime * 500, DeltaTime));
            overlaySystem.Update(world.Unmanaged);
            scheduler.Update(world.Unmanaged);

            NavigationFlowFieldHandle affectedHandle = entityManager.GetComponentData<
                NavigationFlowFieldHandle>(affected);
            NavigationFlowFieldHandle unaffectedHandle = entityManager.GetComponentData<
                NavigationFlowFieldHandle>(unaffected);
            Assert(affectedHandle.Record != affectedRecord,
                "Corridor 内 Overlay 变化没有撤销受影响 Record");
            Assert(unaffectedHandle.Record == unaffectedRecord &&
                   entityManager.Exists(unaffectedRecord),
                "Corridor 外 Overlay 变化错误地清除了无关 Record");
            cohorts.Dispose();
        }

        private static void TestByteBudgetEviction()
        {
            using var world = CreateWorld("Stage Six A Three Byte Budget");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out _);
            Entity storeEntity = GetStoreEntity(entityManager);
            SetSettings(entityManager, storeEntity, maximumBuilds: 1, timeoutTicks: 120);

            Entity cohort = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(20, 20)),
                GetCellPosition(entityManager, new int2(28, 26)),
                1,
                priority: 1);
            DriveUntil(world, scheduler, () => IsSucceeded(entityManager, cohort));
            Entity record = entityManager.GetComponentData<NavigationFlowFieldHandle>(cohort).Record;
            Assert(record != Entity.Null && entityManager.Exists(record),
                "内存预算专项没有先生成缓存 Record");

            // 先移除唯一消费者，再把预算压到最低，避免测试误删仍被引用的 Record
            entityManager.DestroyEntity(cohort);
            NavigationFlowFieldSchedulerSettings settings = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerSettings>(storeEntity);
            settings.StoreByteBudget = 1;
            entityManager.SetComponentData(storeEntity, settings);
            UpdateScheduler(world, scheduler, 500);
            Assert(!entityManager.Exists(record),
                "无引用 Record 超出字节预算后仍未淘汰");
            Assert(entityManager.GetComponentData<NavigationFlowFieldSchedulerState>(storeEntity)
                       .CumulativeEvictedCount > 0,
                "调度器没有记录字节预算淘汰数量");
        }

        private static void TestGoalRegionSharingAndLocalRefresh()
        {
            using var world = CreateWorld("Stage Six A Five Goal Region Refresh");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out SystemHandle overlaySystem);
            Entity storeEntity = GetStoreEntity(entityManager);
            SetSettings(entityManager, storeEntity, maximumBuilds: 2, timeoutTicks: 120);

            var cohorts = new NativeArray<Entity>(4, Allocator.Temp);
            int2[] startCoordinates =
            {
                new int2(8, 8),
                new int2(72, 8),
                new int2(8, 48),
                new int2(72, 48),
            };
            float3 target = GetCellPosition(entityManager, new int2(46, 30));
            for (int index = 0; index < cohorts.Length; index++)
            {
                cohorts[index] = CreateRequestCohort(
                    entityManager,
                    GetCellPosition(entityManager, startCoordinates[index]),
                    target,
                    (uint)(index + 1),
                    priority: 2,
                    coverageMode: NavigationFlowFieldCoverageMode.GoalRegion);
            }

            // 四个远距离起点必须归并到同一个不含精确起点的目标场 Key
            DriveUntil(world, scheduler, () => AllSucceeded(entityManager, cohorts));
            Entity recordEntity = entityManager.GetComponentData<NavigationFlowFieldHandle>(
                cohorts[0]).Record;
            NavigationSharedFlowFieldRecord originalRecord = entityManager.GetComponentData<
                NavigationSharedFlowFieldRecord>(recordEntity);
            ulong clearFieldHash = CalculateFieldHash(entityManager, recordEntity);
            for (int index = 1; index < cohorts.Length; index++)
            {
                Assert(entityManager.GetComponentData<NavigationFlowFieldHandle>(cohorts[index])
                           .Record == recordEntity,
                    "不同起点没有共享同一目标场 Record");
            }
            NavigationFlowFieldSchedulerState initialState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(initialState.CumulativeUniqueBuildCount == 1 &&
                   initialState.CumulativeSharedHitCount == cohorts.Length - 1,
                "目标场共享没有归并多起点请求");

            // 障碍位于目标场中部，刷新期间未被选为所有者的 Handle 必须保持可用
            AddOverlayDelta(entityManager, new int2(38, 25), sourceId: 100);
            world.SetTime(new TimeData(DeltaTime * 300, DeltaTime));
            overlaySystem.Update(world.Unmanaged);
            scheduler.Update(world.Unmanaged);
            int preservedHandleCount = CountHandlesReferencing(
                entityManager,
                cohorts,
                recordEntity);
            Assert(preservedHandleCount == cohorts.Length - 1,
                "局部目标场刷新撤销了无关 Cohort 的活动 Handle");

            DriveUntil(
                world,
                scheduler,
                () => AllSucceeded(entityManager, cohorts) &&
                      entityManager.GetComponentData<NavigationSharedFlowFieldRecord>(recordEntity)
                          .RefreshPending == 0,
                firstTick: 301);
            NavigationSharedFlowFieldRecord blockedRecord = entityManager.GetComponentData<
                NavigationSharedFlowFieldRecord>(recordEntity);
            NavigationFlowFieldSchedulerState blockedState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(blockedRecord.RecordVersion == originalRecord.RecordVersion &&
                   CountHandlesReferencing(entityManager, cohorts, recordEntity) == cohorts.Length,
                "局部目标场刷新换掉了 Record Entity 或版本");
            Assert(blockedState.CumulativeUniqueBuildCount ==
                   initialState.CumulativeUniqueBuildCount + 1 &&
                   blockedState.CumulativeCoverageTileInvalidationCount ==
                   initialState.CumulativeCoverageTileInvalidationCount + 1 &&
                   blockedState.CumulativeCoverageTileBuildCount ==
                   initialState.CumulativeCoverageTileBuildCount +
                   originalRecord.CoverageTileCount,
                "局部失效数量或完整目标场重建数量记录错误");

            // 没有新 Delta 时 Overlay 版本不变，继续调度不能重复刷新同一个覆盖块
            for (int tick = 500; tick < 505; tick++)
            {
                UpdateScheduler(world, scheduler, tick);
            }
            NavigationFlowFieldSchedulerState repeatedState = entityManager.GetComponentData<
                NavigationFlowFieldSchedulerState>(storeEntity);
            Assert(repeatedState.CumulativeUniqueBuildCount ==
                   blockedState.CumulativeUniqueBuildCount,
                "相同 Overlay 版本被重复构建");

            // 移除同一障碍后再次原位刷新，空 Overlay 的 Flow 结果应恢复原始 Hash
            AddOverlayDelta(
                entityManager,
                new int2(38, 25),
                sourceId: 100,
                blockCountDelta: -1);
            world.SetTime(new TimeData(DeltaTime * 600, DeltaTime));
            overlaySystem.Update(world.Unmanaged);
            scheduler.Update(world.Unmanaged);
            DriveUntil(
                world,
                scheduler,
                () => entityManager.GetComponentData<NavigationSharedFlowFieldRecord>(recordEntity)
                          .RefreshPending == 0 &&
                      AllSucceeded(entityManager, cohorts),
                firstTick: 601);
            Assert(CalculateFieldHash(entityManager, recordEntity) == clearFieldHash,
                "解除局部障碍后目标场没有恢复稳定结果");
            cohorts.Dispose();
        }

        private static void TestGridReplacementDuringPublish()
        {
            using var world = CreateWorld("Stage Six A Three Grid Replacement");
            EntityManager entityManager = world.EntityManager;
            SystemHandle scheduler = PrepareWorld(world, out _);
            Entity cohort = CreateRequestCohort(
                entityManager,
                GetCellPosition(entityManager, new int2(10, 10)),
                GetCellPosition(entityManager, new int2(22, 18)),
                1,
                priority: 1);

            UpdateScheduler(world, scheduler, 0);
            Assert(entityManager.GetComponentData<NavigationFlowFieldState>(cohort).Status ==
                   NavigationPathStatus.Searching,
                "Grid 换代专项没有进入活动构建状态");
            // 小地图构建通常已在此期间结束，但结果仍等待调度器下一次更新发布
            Thread.Sleep(20);

            NavigationFlowFieldRequest replacement = entityManager.GetComponentData<
                NavigationFlowFieldRequest>(cohort);
            replacement.PathRequest.StartPosition = GetCellPosition(
                entityManager,
                new int2(42, 30));
            replacement.PathRequest.EndPosition = GetCellPosition(
                entityManager,
                new int2(55, 38));
            entityManager.SetComponentData(cohort, replacement);

            // 合成 Grid 只改数据身份，用不同投影请求模拟换代后 Cell 索引不再匹配旧 Key
            using EntityQuery gridQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadWrite<NavigationGridReference>());
            NavigationGridReference gridReference = gridQuery.GetSingleton<NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            grid.DataHash = new Unity.Entities.Hash128("364133477269645265706c6163653031");

            DriveUntil(world, scheduler, () => IsSucceeded(entityManager, cohort), firstTick: 1);
            NavigationFlowFieldState fieldState = entityManager.GetComponentData<
                NavigationFlowFieldState>(cohort);
            Assert(fieldState.ProjectedStartCellIndex == 42 + 30 * grid.Width,
                "Grid 换代后请求仍使用旧 Key 的投影起点");
        }

        private static World CreateWorld(string name)
        {
            var world = new World(name, WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            entityManager.CreateEntity(typeof(GridMovementBackendEnabled));
            Entity configEntity = entityManager.CreateEntity(typeof(NavigationGridBenchmarkConfig));
            entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
            {
                Workload = NavigationGridBenchmarkWorkload.FreeCohortMovement,
                AgentCount = 8,
            });
            return world;
        }

        private static SystemHandle PrepareWorld(
            World world,
            out SystemHandle overlaySystem)
        {
            SystemHandle gridSystem = world.GetOrCreateSystem<
                ServerNavigationGridBenchmarkGridSystem>();
            gridSystem.Update(world.Unmanaged);
            PrepareOverlay(world.EntityManager);
            overlaySystem = world.GetOrCreateSystem<NavigationDynamicOverlaySystem>();
            return world.GetOrCreateSystem<ServerNavigationSharedFlowFieldSystem>();
        }

        private static Entity CreateRequestCohort(
            EntityManager entityManager,
            float3 start,
            float3 end,
            uint version,
            byte priority,
            NavigationFlowFieldCoverageMode coverageMode =
                NavigationFlowFieldCoverageMode.Corridor)
        {
            // 保留旧结果 Buffer 用来断言共享链路没有向 Cohort 回写大块数据
            Entity entity = entityManager.CreateEntity(
                typeof(AniMovementCohort),
                typeof(NavigationFlowFieldRequest),
                typeof(NavigationFlowFieldState),
                typeof(NavigationFlowFieldHandle),
                typeof(NavigationFlowFieldQueueState),
                typeof(NavigationCorridorCluster),
                typeof(NavigationCorridorPortal),
                typeof(NavigationHierarchicalWaypoint),
                typeof(NavigationFlowFieldCell));
            entityManager.SetComponentData(entity, new AniMovementCohort
            {
                CohortId = version,
                Priority = priority,
                CancellationVersion = version,
            });
            NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                start,
                end,
                0.35f,
                version,
                clearanceMargin: 0.05f,
                maximumProjectionRadiusInCells: 8);
            entityManager.SetComponentData(
                entity,
                NavigationFlowFieldRequest.Create(
                    pathRequest,
                    priority,
                    version,
                    coverageMode));
            entityManager.SetComponentData(
                entity,
                NavigationFlowFieldState.CreatePending(version));
            return entity;
        }

        private static void DriveUntil(
            World world,
            SystemHandle scheduler,
            Func<bool> completed,
            int firstTick = 0)
        {
            int lastTickExclusive = firstTick + MaximumTicks;
            for (int tick = firstTick; tick < lastTickExclusive; tick++)
            {
                UpdateScheduler(world, scheduler, tick);
                if (completed())
                {
                    return;
                }
                // 测试 World 没有其他 System 工作量，短暂让出时间片供后台 Worker 完成构建
                Thread.Sleep(1);
            }
            throw new InvalidOperationException("共享 Flow Field 专项验收超出固定 Tick 上限");
        }

        private static void UpdateScheduler(World world, SystemHandle scheduler, int tick)
        {
            world.SetTime(new TimeData(tick * DeltaTime, DeltaTime));
            scheduler.Update(world.Unmanaged);
            JobHandle.ScheduleBatchedJobs();
        }

        private static bool AllSucceeded(
            EntityManager entityManager,
            NativeArray<Entity> entities)
        {
            for (int index = 0; index < entities.Length; index++)
            {
                if (!IsSucceeded(entityManager, entities[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool AllTerminal(
            EntityManager entityManager,
            NativeArray<Entity> entities)
        {
            for (int index = 0; index < entities.Length; index++)
            {
                if (!IsTerminal(entityManager, entities[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string BuildStateSummary(
            EntityManager entityManager,
            NativeArray<Entity> entities)
        {
            string summary = "共享请求没有全部成功";
            for (int index = 0; index < entities.Length; index++)
            {
                NavigationFlowFieldState state = entityManager.GetComponentData<
                    NavigationFlowFieldState>(entities[index]);
                summary += $" C{index}:{state.Status}/{state.FailureReason}/V{state.RequestVersion}";
            }
            return summary;
        }

        private static bool IsSucceeded(EntityManager entityManager, Entity entity)
        {
            return entityManager.Exists(entity) &&
                   entityManager.GetComponentData<NavigationFlowFieldState>(entity).Status ==
                   NavigationPathStatus.Succeeded;
        }

        private static bool IsTerminal(EntityManager entityManager, Entity entity)
        {
            NavigationPathStatus status = entityManager.GetComponentData<
                NavigationFlowFieldState>(entity).Status;
            return status == NavigationPathStatus.Succeeded ||
                   status == NavigationPathStatus.Failed;
        }

        private static void SetSettings(
            EntityManager entityManager,
            Entity storeEntity,
            int maximumBuilds,
            int timeoutTicks)
        {
            // 专项测试让并发数与每 Tick 预算相同，单独控制可开工的唯一 Key 数量
            NavigationFlowFieldSchedulerSettings settings =
                NavigationFlowFieldSchedulerSettings.CreateDefault();
            settings.MaximumConcurrentBuilds = maximumBuilds;
            settings.MaximumBuildsPerTick = maximumBuilds;
            settings.RequestTimeoutTicks = timeoutTicks;
            entityManager.SetComponentData(storeEntity, settings);
        }

        private static Entity GetStoreEntity(EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationFlowFieldSchedulerState>(),
                ComponentType.ReadWrite<NavigationFlowFieldSchedulerSettings>(),
                ComponentType.ReadWrite<NavigationFlowFieldQueueWaitSample>());
            return query.GetSingletonEntity();
        }

        private static float3 GetCellPosition(
            EntityManager entityManager,
            int2 coordinate)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            NavigationGridReference gridReference = query.GetSingleton<NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            return NavigationGridQuery.GetCellWorldPosition(
                ref grid,
                coordinate.x + coordinate.y * grid.Width);
        }

        private static void PrepareOverlay(EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            Entity gridEntity = query.GetSingletonEntity();
            NavigationGridReference gridReference = query.GetSingleton<NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            // Buffer 长度与 Grid 索引一一对应，签名测试才能直接定位受影响 Cluster
            DynamicBuffer<NavigationDynamicOverlayCell> cells = entityManager.AddBuffer<
                NavigationDynamicOverlayCell>(gridEntity);
            cells.ResizeUninitialized(grid.Cells.Length);
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = default;
            }
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters = entityManager.AddBuffer<
                NavigationDynamicOverlayCluster>(gridEntity);
            clusters.ResizeUninitialized(grid.Clusters.Length);
            for (int index = 0; index < clusters.Length; index++)
            {
                clusters[index] = default;
            }
            entityManager.AddBuffer<NavigationDynamicOverlayDelta>(gridEntity);
            entityManager.AddComponentData(gridEntity, new NavigationDynamicOverlayState
            {
                Version = 1,
                Initialized = 1,
            });
            entityManager.AddComponentData(gridEntity, new NavigationGridJobActivity());
        }

        private static void AddOverlayDelta(
            EntityManager entityManager,
            int2 coordinate,
            uint sourceId,
            int blockCountDelta = 1)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            Entity gridEntity = query.GetSingletonEntity();
            NavigationGridReference gridReference = query.GetSingleton<NavigationGridReference>();
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            // Delta 交给正式 Overlay System 消费，不直接篡改 Cell 和 Cluster 版本
            entityManager.GetBuffer<NavigationDynamicOverlayDelta>(gridEntity).Add(
                new NavigationDynamicOverlayDelta
                {
                    CellIndex = coordinate.x + coordinate.y * grid.Width,
                    BlockCountDelta = blockCountDelta,
                    SourceId = sourceId,
                });
        }

        private static int CountHandlesReferencing(
            EntityManager entityManager,
            NativeArray<Entity> cohorts,
            Entity recordEntity)
        {
            int count = 0;
            for (int index = 0; index < cohorts.Length; index++)
            {
                NavigationFlowFieldHandle handle = entityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohorts[index]);
                if (handle.Record == recordEntity)
                {
                    count++;
                }
            }
            return count;
        }

        private static ulong CalculateFieldHash(
            EntityManager entityManager,
            Entity recordEntity)
        {
            DynamicBuffer<NavigationFlowFieldCell> field = entityManager.GetBuffer<
                NavigationFlowFieldCell>(recordEntity, true);
            ulong hash = 1469598103934665603UL;
            for (int index = 0; index < field.Length; index++)
            {
                NavigationFlowFieldCell cell = field[index];
                hash = Mix(hash, (uint)cell.CellIndex);
                hash = Mix(hash, math.asuint(cell.IntegrationCost));
                hash = Mix(hash, math.asuint(cell.Direction.x));
                hash = Mix(hash, math.asuint(cell.Direction.y));
            }
            return hash;
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        private static NavigationDynamicOverlayState GetOverlayState(
            EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<NavigationDynamicOverlayState>());
            return query.GetSingleton<NavigationDynamicOverlayState>();
        }

        private static void AssertPercentilesOrdered(
            DynamicBuffer<NavigationFlowFieldQueueWaitSample> samples)
        {
            // 报告采用 nearest-rank，验收使用同一取值规则检查分位数单调性
            var values = new int[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                values[index] = samples[index].WaitTicks;
            }
            Array.Sort(values);
            int p50 = values[(int)math.ceil(values.Length * 0.50f) - 1];
            int p95 = values[(int)math.ceil(values.Length * 0.95f) - 1];
            int p99 = values[(int)math.ceil(values.Length * 0.99f) - 1];
            Assert(p50 <= p95 && p95 <= p99,
                "排队 P50、P95 和 P99 样本顺序无效");
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
    }
}
#endif
