#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid.Editor
{
    /// <summary>
    /// 执行阶段三 Portal、HPA 星、局部 Flow Field 与缓存自动验收
    /// </summary>
    public static class NavigationGridStageThreeValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Three Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity 批处理执行阶段三完整验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 执行分层烘焙、路径、缓存和异步接线验证
        /// </summary>
        public static void RunAll()
        {
            // 先验证纯算法，再验证 World 接线和异步生命周期
            TestHierarchyBakeDeterminism();
            TestHierarchicalReachabilityFieldAndCache();
            TestPortalClearanceFiltering();
            TestWorldFilterRegistration();
            TestAsynchronousFlowFieldSystem();
            TestBenchmarkWorkloadScales();
            // 只有全部断言完成后才输出成功标记
            Debug.Log("Navigation Grid 阶段三自动验收通过");
        }

        private static void TestHierarchyBakeDeterminism()
        {
            // 使用开放 Grid 排除障碍布局对 Portal 顺序的干扰
            NavigationGridCellData[] cells = CreateWalkableCells(16, 8);
            PrepareCells(cells, 16, 8, 4, 1f);
            NavigationGridHierarchyBuildResult first = NavigationGridHierarchyBuilder.Build(
                cells,
                16,
                8,
                4,
                1f);
            // 相同输入再次构建，用于检查分层数据的确定性
            NavigationGridHierarchyBuildResult second = NavigationGridHierarchyBuilder.Build(
                cells,
                16,
                8,
                4,
                1f);
            Assert(first.ClusterWidth == 4 && first.ClusterHeight == 2, "Cluster 尺寸错误");
            Assert(first.Portals.Length > 0, "开放 Grid 必须生成 Portal");
            Assert(first.PortalNodes.Length == first.Portals.Length * 2, "Portal 双端节点数量错误");
            Assert(first.AbstractEdges.Length > first.PortalNodes.Length, "缺少 Cluster 内静态成本边");
            Assert(first.Portals.Length == second.Portals.Length, "重复烘焙 Portal 数量不稳定");
            Assert(first.AbstractEdges.Length == second.AbstractEdges.Length, "重复烘焙抽象边数量不稳定");
            for (int index = 0; index < first.Portals.Length; index++)
            {
                Assert(
                    first.Portals[index].RepresentativeCellA ==
                    second.Portals[index].RepresentativeCellA,
                    "重复烘焙 Portal 顺序不稳定");
                Assert(first.Portals[index].MinimumClearance >= 0f, "Portal Clearance 非法");
            }
        }

        private static void TestHierarchicalReachabilityFieldAndCache()
        {
            const int Width = 24;
            const int Height = 12;
            // 中央墙只保留两个开口，强制宏观路线选择 Portal
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Height);
            for (int z = 0; z < Height; z++)
            {
                if (z != 2 && z != 9)
                {
                    SetWalkable(cells, Width, 11, z, false);
                }
            }

            PrepareCells(cells, Width, Height, 6, 1f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, Width, Height, 6);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(1.5f, 0f, 1.5f),
                new float3(22.5f, 0f, 10.5f),
                0.35f,
                1,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f,
                smoothingCostTolerance: 0f);
            // 普通 A 星提供可达性和成本基线
            PathExecutionResult ordinary = ExecuteOrdinaryPath(grid, request);
            // 连续两个相同请求用于验证同一批次内的 Field 缓存复用
            FlowExecutionResult flow = ExecuteFlowBatch(
                grid,
                new[]
                {
                    NavigationFlowFieldRequest.Create(request),
                    NavigationFlowFieldRequest.Create(request),
                });
            Assert(ordinary.Result.Status == NavigationPathStatus.Succeeded, "普通 A 星基线应可达");
            Assert(flow.Results[0].Status == ordinary.Result.Status, "HPA 星与普通 A 星可达性不一致");
            Assert(flow.Results[1].Status == NavigationPathStatus.Succeeded, "重复 Field 请求失败");
            Assert(flow.Results[1].CacheHit != 0, "相同目标和 Corridor 未复用 Field 缓存");
            Assert(flow.Results[0].CorridorClusterCount > 1, "跨 Cluster 请求未生成 Corridor");
            Assert(
                flow.Results[0].CorridorClusterCount < grid.Value.Clusters.Length,
                "局部 Field Corridor 不应覆盖全部 Cluster");
            Assert(
                flow.Results[0].AbstractExpandedNodeCount < ordinary.Result.ExpandedNodeCount,
                "宏观搜索访问节点数未低于普通全图 A 星");
            Assert(
                flow.Results[0].TotalCost <= ordinary.Result.TotalCost * 1.25f + 0.0001f,
                "HPA 星 Corridor 路径成本超过阶段三允许的 25% 次优范围");

            // 先将宏观 Corridor 转为可快速检查的 Cluster 集合
            var corridorSet = new HashSet<int>();
            for (int index = 0; index < flow.Results[0].CorridorClusterCount; index++)
            {
                corridorSet.Add(flow.CorridorClusters[
                    flow.Results[0].CorridorClusterOffset + index]);
            }

            var integrationCosts = new Dictionary<int, float>();
            // 建立 Cell 到 Integration Cost 的索引，供方向下降断言随机访问邻居
            for (int index = 0; index < flow.Results[0].FieldCount; index++)
            {
                NavigationFlowFieldCell fieldCell = flow.FlowCells[
                    flow.Results[0].FieldOffset + index];
                integrationCosts[fieldCell.CellIndex] = fieldCell.IntegrationCost;
                Assert(
                    corridorSet.Contains(grid.Value.Cells[fieldCell.CellIndex].ClusterId),
                    "Field 包含 Corridor 外 Cell");
            }

            // 最后逐 Cell 验证方向落在合法且成本下降的邻居上
            ValidateDescendingDirections(ref grid.Value, flow, 0, integrationCosts);
        }

        private static void TestPortalClearanceFiltering()
        {
            const int Width = 8;
            const int Height = 3;
            // 两个边界缺口分别承载窄通道和宽通道
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Height);
            SetWalkable(cells, Width, 3, 0, false);
            SetWalkable(cells, Width, 4, 0, false);

            NavigationGridAlgorithms.BuildConnectivity(cells, Width, Height, 0.5f);
            NavigationGridAlgorithms.AssignClusters(cells, Width, Height, 4);
            NavigationGridAlgorithms.AssignRegions(cells, Width, Height);
            // Connectivity 固定后手工覆盖 Clearance，隔离 Portal 分桶这一项行为
            for (int index = 0; index < cells.Length; index++)
            {
                NavigationGridCellData cell = cells[index];
                cell.Clearance = cell.Walkable ? 2f : 0f;
                cells[index] = cell;
            }
            // 1.1 和 1.8 在旧的整格分桶中会被合成同一 Portal，导致宽段被窄段拖累
            SetClearance(cells, Width, 3, 1, 1.1f);
            SetClearance(cells, Width, 4, 1, 1.1f);
            SetClearance(cells, Width, 3, 2, 1.8f);
            SetClearance(cells, Width, 4, 2, 1.8f);

            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, Width, Height, 4);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(1.5f, 0f, 2.5f),
                new float3(6.5f, 0f, 2.5f),
                1.85f,
                7,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f);
            // 请求半径设置为只能通过宽 Portal
            FlowExecutionResult flow = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) });
            Assert(flow.Results[0].Status == NavigationPathStatus.Succeeded, "大体型应通过宽 Portal");
            float requiredClearance = request.AgentRadius - grid.Value.BaseAgentRadius;
            // 逐个确认最终 Corridor 没有使用 Clearance 不足的 Portal
            for (int index = 0; index < flow.Results[0].CorridorPortalCount; index++)
            {
                int portalIndex = flow.CorridorPortals[
                    flow.Results[0].CorridorPortalOffset + index];
                Assert(
                    grid.Value.Portals[portalIndex].MinimumClearance + 0.0001f >=
                    requiredClearance,
                    "HPA 星选择了 Clearance 不足的 Portal");
            }
        }

        private static void TestWorldFilterRegistration()
        {
            // 分别读取三类 World 的默认系统注册表
            // 注册表来自系统特性解析，可验证过滤声明而不启动完整游戏 World
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> localSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.LocalSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            // Server 和 Local 可执行寻路，纯 Client 不得注册权威导航系统
            Assert(
                ContainsSystem(serverSystems, typeof(ServerNavigationGridBenchmarkGridSystem)),
                "Server World is missing the Grid benchmark data source");
            Assert(
                !ContainsSystem(clientSystems, typeof(ServerNavigationGridBenchmarkGridSystem)),
                "Client World must not register the Grid benchmark data source");
            Assert(
                ContainsSystem(serverSystems, typeof(ServerNavigationGridFlowFieldSystem)),
                "Server World 缺少阶段三 Field System");
            Assert(
                ContainsSystem(localSystems, typeof(ServerNavigationGridFlowFieldSystem)),
                "Local World 缺少阶段三 Field System");
            Assert(
                !ContainsSystem(clientSystems, typeof(ServerNavigationGridFlowFieldSystem)),
                "Client World 不应注册阶段三 Field System");
            Assert(
                ContainsSystem(
                    serverSystems,
                    typeof(ServerNavigationGridBenchmarkTimingStartSystem)),
                "Server World 缺少 Grid Benchmark 计时起点 System");
            Assert(
                ContainsSystem(
                    serverSystems,
                    typeof(ServerNavigationGridBenchmarkTimingEndSystem)),
                "Server World 缺少 Grid Benchmark 计时终点 System");
            Assert(
                !ContainsSystem(
                    clientSystems,
                    typeof(ServerNavigationGridBenchmarkTimingStartSystem)) &&
                !ContainsSystem(
                    localSystems,
                    typeof(ServerNavigationGridBenchmarkTimingStartSystem)),
                "Grid Benchmark 计时 System 只能注册到 Server World");
        }

        private static void TestAsynchronousFlowFieldSystem()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(16, 8);
            PrepareCells(cells, 16, 8, 4, 1f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 16, 8, 4);
            // 隔离 World 避免项目中其他导航系统影响请求状态
            using var world = new World("Navigation Grid Stage Three Validation", WorldFlags.Game);
            AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
            EntityManager entityManager = world.EntityManager;
            Entity gridEntity = entityManager.CreateEntity(typeof(NavigationGridReference));
            entityManager.SetComponentData(gridEntity, new NavigationGridReference { Value = grid });
            NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 0.5f),
                new float3(15.5f, 0f, 7.5f),
                0.35f,
                11,
                maximumProjectionRadiusInCells: 0);
            Entity requestEntity = CreateFlowRequestEntity(
                entityManager,
                NavigationFlowFieldRequest.Create(pathRequest));
            SystemHandle system = world.GetOrCreateSystem<ServerNavigationGridFlowFieldSystem>();
            // 首次 Update 只能调度 Job 并进入 Searching
            system.Update(world.Unmanaged);
            NavigationFlowFieldState fieldState =
                entityManager.GetComponentData<NavigationFlowFieldState>(requestEntity);
            Assert(fieldState.Status == NavigationPathStatus.Searching, "阶段三首帧不应同步完成搜索");
            WaitForFlowResult(world, system, requestEntity, out fieldState);
            Assert(fieldState.Status == NavigationPathStatus.Succeeded, "异步阶段三请求失败");

            // 递增版本后重交相同目标和 Corridor，验证跨帧缓存
            pathRequest.Version++;
            entityManager.SetComponentData(
                requestEntity,
                NavigationFlowFieldRequest.Create(pathRequest));
            entityManager.SetComponentData(
                requestEntity,
                NavigationFlowFieldState.CreatePending(pathRequest.Version));
            system.Update(world.Unmanaged);
            WaitForFlowResult(world, system, requestEntity, out fieldState);
            Assert(fieldState.CacheHit != 0, "跨帧相同 Field 请求未命中缓存");
        }

        private static void TestBenchmarkWorkloadScales()
        {
            int[] counts = { 32, 64, 128 };
            // 三档规模共用同一配置结构，只改变请求实体数量
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                int count = counts[countIndex];
                // 每档规模使用独立 World，避免前一档实体和系统状态进入计数
                using var world = new World($"Stage Three Benchmark {count}", WorldFlags.Game);
                AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
                EntityManager entityManager = world.EntityManager;
                Entity configEntity = entityManager.CreateEntity(
                    typeof(NavigationGridBenchmarkConfig),
                    typeof(NavigationGridBenchmarkState));
                DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                    entityManager.AddBuffer<NavigationGridBenchmarkCommand>(configEntity);
                entityManager.AddBuffer<NavigationGridBenchmarkTimingSample>(configEntity);
                commands.Add(new NavigationGridBenchmarkCommand
                {
                    Tick = 0,
                    TargetOffset = new float3(-20f, 0f, 16f),
                });
                entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
                {
                    AgentCount = count,
                    WarmupTicks = 1,
                    SampleTicks = 2,
                    SpawnColumnCount = 16,
                    SpawnSpacing = 1.25f,
                    SpawnOrigin = new float3(105f, 0.57f, 44.43f),
                    AgentRadius = 0.35f,
                });

                // 先让数据源系统注册唯一 Benchmark Grid
                SystemHandle gridSystem =
                    world.GetOrCreateSystem<ServerNavigationGridBenchmarkGridSystem>();
                gridSystem.Update(world.Unmanaged);
                // 唯一 Grid 是缓存索引和结果归属的前提
                using EntityQuery grids = entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NavigationGridReference>());
                Assert(grids.CalculateEntityCount() == 1, "Shared benchmark did not get one Grid");

                SystemHandle benchmarkSystem =
                    world.GetOrCreateSystem<ServerNavigationGridBenchmarkSystem>();
                benchmarkSystem.Update(world.Unmanaged);
                using EntityQuery requests = entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NavigationGridBenchmarkRequestTag>());
                Assert(requests.CalculateEntityCount() == count, $"{count} 规模未生成对应 Field 工作负载");
                // 逐实体检查纯路径工作负载没有混入移动组件
                using NativeArray<Entity> entities = requests.ToEntityArray(Allocator.Temp);
                for (int index = 0; index < entities.Length; index++)
                {
                    using NativeArray<ComponentType> componentTypes =
                        entityManager.GetComponentTypes(entities[index], Allocator.Temp);
                    for (int typeIndex = 0; typeIndex < componentTypes.Length; typeIndex++)
                    {
                        Type managedType = TypeManager.GetType(componentTypes[typeIndex].TypeIndex);
                        // LocalTransform 表示 Benchmark 已经污染 Ani 移动写回统计
                        Assert(
                            managedType == null || managedType.FullName != "Unity.Transforms.LocalTransform",
                            "Grid 路径与 Field Benchmark 不得创建或写入 Ani Transform");
                    }
                }
            }
        }

        private static void ValidateDescendingDirections(
            ref NavigationGridBlob grid,
            FlowExecutionResult flow,
            int resultIndex,
            Dictionary<int, float> integrationCosts)
        {
            NavigationFlowFieldJobResult result = flow.Results[resultIndex];
            for (int index = 0; index < result.FieldCount; index++)
            {
                NavigationFlowFieldCell fieldCell = flow.FlowCells[result.FieldOffset + index];
                if (math.lengthsq(fieldCell.Direction) <= 0.0001f)
                {
                    // 只有投影终点允许输出零方向
                    Assert(
                        fieldCell.CellIndex == result.ProjectedEndCellIndex,
                        "非目标 Cell 的 Flow Direction 不能为零");
                    continue;
                }

                int deltaX = (int)math.round(fieldCell.Direction.x);
                int deltaZ = (int)math.round(fieldCell.Direction.y);
                // 平滑方向最终仍应归属八邻域中的一个离散 Cell
                int x = fieldCell.CellIndex % grid.Width + deltaX;
                int z = fieldCell.CellIndex / grid.Width + deltaZ;
                // 非零方向必须映射到 Grid 内的离散邻居
                Assert(x >= 0 && x < grid.Width && z >= 0 && z < grid.Height, "Flow 指向 Grid 外");
                int neighbor = x + z * grid.Width;
                Assert(grid.Cells[neighbor].Walkable != 0, "Flow 指向不可行走 Cell");
                Assert(integrationCosts.TryGetValue(neighbor, out float neighborCost), "Flow 指向 Field 外 Cell");
                Assert(
                    neighborCost < fieldCell.IntegrationCost - 0.00001f,
                    "平滑后 Flow Direction 未保持 Integration Cost 下降");
            }
        }

        private static FlowExecutionResult ExecuteFlowBatch(
            BlobAssetReference<NavigationGridBlob> grid,
            NavigationFlowFieldRequest[] requests)
        {
            // Scratch 长度与运行时 System 使用相同的 Cell、Cluster 和 Node 维度
            int cellCount = grid.Value.Cells.Length;
            int clusterCount = grid.Value.Clusters.Length;
            int nodeCount = grid.Value.PortalNodes.Length;
            // 输入和结果数组按 request 下标一一对应
            var jobRequests = new NativeArray<NavigationFlowFieldJobRequest>(requests.Length, Allocator.TempJob);
            var results = new NativeArray<NavigationFlowFieldJobResult>(requests.Length, Allocator.TempJob);
            // 四个 NativeList 镜像 Job 的共享切片输出
            var corridorClusters = new NativeList<int>(64, Allocator.TempJob);
            var corridorPortals = new NativeList<int>(64, Allocator.TempJob);
            var waypointCells = new NativeList<int>(128, Allocator.TempJob);
            var flowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.TempJob);
            // Cell Scratch 用于局部 Dijkstra 和 Integration Field
            var cellCosts = new NativeArray<float>(cellCount, Allocator.TempJob);
            var cellHeap = new NativeArray<int>(cellCount, Allocator.TempJob);
            var cellHeapPositions = new NativeArray<int>(cellCount, Allocator.TempJob);
            var cellGenerations = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var clusterGenerations = new NativeArray<int>(clusterCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            // 抽象 Scratch 按 Portal Node 数量分配
            var abstractCosts = new NativeArray<float>(nodeCount, Allocator.TempJob);
            var abstractEndCosts = new NativeArray<float>(nodeCount, Allocator.TempJob);
            var abstractParents = new NativeArray<int>(nodeCount, Allocator.TempJob);
            var abstractHeap = new NativeArray<int>(nodeCount, Allocator.TempJob);
            var abstractHeapPositions = new NativeArray<int>(nodeCount, Allocator.TempJob);
            var abstractGenerations = new NativeArray<int>(nodeCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var workVisited = new NativeList<int>(256, Allocator.TempJob);
            var workClusters = new NativeList<int>(16, Allocator.TempJob);
            var workPortals = new NativeList<int>(16, Allocator.TempJob);
            var workNodes = new NativeList<int>(32, Allocator.TempJob);
            var cacheEntries = new NativeList<NavigationFlowFieldCacheEntry>(64, Allocator.TempJob);
            var cacheClusters = new NativeList<int>(64, Allocator.TempJob);
            var cacheCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.TempJob);
            try
            {
                // 数组顺序就是 Job 的稳定批次顺序
                for (int index = 0; index < requests.Length; index++)
                {
                    jobRequests[index] = new NavigationFlowFieldJobRequest
                    {
                        Entity = Entity.Null,
                        Request = requests[index],
                    };
                }

                var job = new NavigationGridFlowFieldJob
                {
                    Grid = grid,
                    Requests = jobRequests,
                    Results = results,
                    CorridorClusters = corridorClusters,
                    CorridorPortals = corridorPortals,
                    HierarchicalWaypointCells = waypointCells,
                    FlowCells = flowCells,
                    CellCosts = cellCosts,
                    CellHeap = cellHeap,
                    CellHeapPositions = cellHeapPositions,
                    CellGenerations = cellGenerations,
                    ClusterGenerations = clusterGenerations,
                    AbstractCosts = abstractCosts,
                    AbstractEndCosts = abstractEndCosts,
                    AbstractParents = abstractParents,
                    AbstractHeap = abstractHeap,
                    AbstractHeapPositions = abstractHeapPositions,
                    AbstractGenerations = abstractGenerations,
                    WorkVisitedCells = workVisited,
                    WorkCorridorClusters = workClusters,
                    WorkCorridorPortals = workPortals,
                    WorkNodeChain = workNodes,
                    CacheEntries = cacheEntries,
                    CacheCorridorClusters = cacheClusters,
                    CacheFlowCells = cacheCells,
                    CacheVersion = 1,
                    GenerationStart = 1,
                };
                // 测试同步等待只用于读取结果，不代表运行时 System 会阻塞主线程
                JobHandle handle = job.Schedule();
                handle.Complete();
                // 离开作用域前复制结果，返回对象不持有 Native 容器
                return new FlowExecutionResult(
                    results.ToArray(),
                    corridorClusters.AsArray().ToArray(),
                    corridorPortals.AsArray().ToArray(),
                    waypointCells.AsArray().ToArray(),
                    flowCells.AsArray().ToArray());
            }
            finally
            {
                // finally 保证 Job 或断言异常时也释放全部 TempJob 分配
                cacheCells.Dispose();
                cacheClusters.Dispose();
                cacheEntries.Dispose();
                workNodes.Dispose();
                workPortals.Dispose();
                workClusters.Dispose();
                workVisited.Dispose();
                abstractGenerations.Dispose();
                abstractHeapPositions.Dispose();
                abstractHeap.Dispose();
                abstractParents.Dispose();
                abstractEndCosts.Dispose();
                abstractCosts.Dispose();
                clusterGenerations.Dispose();
                cellGenerations.Dispose();
                cellHeapPositions.Dispose();
                cellHeap.Dispose();
                cellCosts.Dispose();
                flowCells.Dispose();
                waypointCells.Dispose();
                corridorPortals.Dispose();
                corridorClusters.Dispose();
                results.Dispose();
                jobRequests.Dispose();
            }
        }

        private static PathExecutionResult ExecuteOrdinaryPath(
            BlobAssetReference<NavigationGridBlob> grid,
            NavigationPathRequest request)
        {
            int cellCount = grid.Value.Cells.Length;
            // 普通 A 星 Scratch 全部按 Cell 数分配
            var requests = new NativeArray<NavigationPathJobRequest>(1, Allocator.TempJob);
            var results = new NativeArray<NavigationPathJobResult>(1, Allocator.TempJob);
            var pathCells = new NativeList<int>(32, Allocator.TempJob);
            var gCosts = new NativeArray<float>(cellCount, Allocator.TempJob);
            var parents = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heap = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heapPositions = new NativeArray<int>(cellCount, Allocator.TempJob);
            var generations = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                requests[0] = new NavigationPathJobRequest { Entity = Entity.Null, Request = request };
                var job = new NavigationGridPathfindingJob
                {
                    Grid = grid,
                    Requests = requests,
                    Results = results,
                    PathCells = pathCells,
                    GCosts = gCosts,
                    Parents = parents,
                    Heap = heap,
                    HeapPositions = heapPositions,
                    NodeGenerations = generations,
                    GenerationStart = 1,
                };
                // 同步完成后只保留状态、成本、展开量和路径 Cell
                JobHandle handle = job.Schedule();
                handle.Complete();
                return new PathExecutionResult(results[0], pathCells.AsArray().ToArray());
            }
            finally
            {
                // 普通路径基线与 Flow 辅助器保持相同的异常释放保证
                generations.Dispose();
                heapPositions.Dispose();
                heap.Dispose();
                parents.Dispose();
                gCosts.Dispose();
                pathCells.Dispose();
                results.Dispose();
                requests.Dispose();
            }
        }

        private static BlobAssetReference<NavigationGridBlob> CreateGrid(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int clusterSize)
        {
            // 分层数据直接由当前测试 Cell 拓扑生成
            NavigationGridHierarchyBuildResult hierarchy = NavigationGridHierarchyBuilder.Build(
                cells,
                width,
                height,
                clusterSize,
                1f);
            // Temp Builder 只承载构造过程，返回的 Blob 使用独立 Persistent 分配
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            root.BoundsMinimum = new float3(0f, -1f, 0f);
            root.BoundsMaximum = new float3(width, 3f, height);
            root.CellSize = 1f;
            root.BaseAgentRadius = 0.35f;
            root.BaseAgentHeight = 1.5f;
            root.Width = width;
            root.Height = height;
            root.ClusterSizeInCells = clusterSize;
            root.ClusterWidth = hierarchy.ClusterWidth;
            root.ClusterHeight = hierarchy.ClusterHeight;
            root.RegionCount = CountRegions(cells);
            root.DataVersion = NavigationGridBakeAsset.CurrentDataVersion;
            // 固定 Hash 让测试只观察拓扑和算法行为
            root.DataHash = new Unity.Entities.Hash128("00000000000000000000000000000001");

            // 测试 Blob 的 Cell 布局与生产 Baker 保持一致
            BlobBuilderArray<NavigationGridCell> blobCells = builder.Allocate(ref root.Cells, cells.Length);
            for (int index = 0; index < cells.Length; index++)
            {
                NavigationGridCellData source = cells[index];
                blobCells[index] = new NavigationGridCell
                {
                    Height = source.Height,
                    SurfaceNormal = source.SurfaceNormal,
                    SlopeDegrees = source.SlopeDegrees,
                    TerrainCost = source.TerrainCost,
                    Clearance = source.Clearance,
                    RegionId = source.RegionId,
                    ClusterId = source.ClusterId,
                    NeighborMask = (byte)source.NeighborMask,
                    Walkable = source.Walkable ? (byte)1 : (byte)0,
                };
            }

            // Cluster 的 Portal Node 偏移和数量必须保持构建器生成的连续切片契约
            BlobBuilderArray<NavigationGridCluster> blobClusters =
                builder.Allocate(ref root.Clusters, hierarchy.Clusters.Length);
            for (int index = 0; index < hierarchy.Clusters.Length; index++)
            {
                NavigationGridClusterData source = hierarchy.Clusters[index];
                blobClusters[index] = new NavigationGridCluster
                {
                    MinimumX = source.MinimumX,
                    MinimumZ = source.MinimumZ,
                    MaximumXExclusive = source.MaximumXExclusive,
                    MaximumZExclusive = source.MaximumZExclusive,
                    PortalNodeOffset = source.PortalNodeOffset,
                    PortalNodeCount = source.PortalNodeCount,
                };
            }

            // Portal 保留区间端点、代表 Cell 和双向成本
            BlobBuilderArray<NavigationGridPortal> blobPortals =
                builder.Allocate(ref root.Portals, hierarchy.Portals.Length);
            for (int index = 0; index < hierarchy.Portals.Length; index++)
            {
                NavigationGridPortalData source = hierarchy.Portals[index];
                blobPortals[index] = new NavigationGridPortal
                {
                    ClusterA = source.ClusterA,
                    ClusterB = source.ClusterB,
                    RegionId = source.RegionId,
                    FirstCellA = source.FirstCellA,
                    LastCellA = source.LastCellA,
                    FirstCellB = source.FirstCellB,
                    LastCellB = source.LastCellB,
                    RepresentativeCellA = source.RepresentativeCellA,
                    RepresentativeCellB = source.RepresentativeCellB,
                    MinimumClearance = source.MinimumClearance,
                    StaticCostAtoB = source.StaticCostAtoB,
                    StaticCostBtoA = source.StaticCostBtoA,
                };
            }

            // Portal Node 保留其 Cluster 和出边切片
            BlobBuilderArray<NavigationGridPortalNode> blobNodes =
                builder.Allocate(ref root.PortalNodes, hierarchy.PortalNodes.Length);
            for (int index = 0; index < hierarchy.PortalNodes.Length; index++)
            {
                NavigationGridPortalNodeData source = hierarchy.PortalNodes[index];
                blobNodes[index] = new NavigationGridPortalNode
                {
                    PortalIndex = source.PortalIndex,
                    ClusterId = source.ClusterId,
                    CellIndex = source.CellIndex,
                    EdgeOffset = source.EdgeOffset,
                    EdgeCount = source.EdgeCount,
                };
            }

            // 抽象边的字节标志与生产 Blob 表示一致
            BlobBuilderArray<NavigationGridAbstractEdge> blobEdges =
                builder.Allocate(ref root.AbstractEdges, hierarchy.AbstractEdges.Length);
            for (int index = 0; index < hierarchy.AbstractEdges.Length; index++)
            {
                NavigationGridAbstractEdgeData source = hierarchy.AbstractEdges[index];
                blobEdges[index] = new NavigationGridAbstractEdge
                {
                    ToNodeIndex = source.ToNodeIndex,
                    StaticCost = source.StaticCost,
                    MinimumClearance = source.MinimumClearance,
                    CrossesPortal = source.CrossesPortal ? (byte)1 : (byte)0,
                };
            }

            // Cluster 通过连续索引数组定位自己的 Portal Node
            BlobBuilderArray<int> clusterNodeIndices = builder.Allocate(
                ref root.ClusterPortalNodeIndices,
                hierarchy.ClusterPortalNodeIndices.Length);
            for (int index = 0; index < hierarchy.ClusterPortalNodeIndices.Length; index++)
            {
                clusterNodeIndices[index] = hierarchy.ClusterPortalNodeIndices[index];
            }

            // 返回 Persistent Blob，由每个 using 测试作用域负责释放
            BlobAssetReference<NavigationGridBlob> result =
                builder.CreateBlobAssetReference<NavigationGridBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        private static Entity CreateFlowRequestEntity(
            EntityManager entityManager,
            NavigationFlowFieldRequest request)
        {
            Entity entity = entityManager.CreateEntity(
                typeof(NavigationFlowFieldRequest),
                typeof(NavigationFlowFieldState));
            // Flow Field System 写回前要求四类输出 Buffer 全部存在
            entityManager.AddBuffer<NavigationCorridorCluster>(entity);
            entityManager.AddBuffer<NavigationCorridorPortal>(entity);
            entityManager.AddBuffer<NavigationHierarchicalWaypoint>(entity);
            entityManager.AddBuffer<NavigationFlowFieldCell>(entity);
            entityManager.SetComponentData(entity, request);
            // State 捕获相同版本，运行时写回会同时比对请求与状态版本
            entityManager.SetComponentData(
                entity,
                NavigationFlowFieldState.CreatePending(request.PathRequest.Version));
            return entity;
        }

        private static void WaitForFlowResult(
            World world,
            SystemHandle system,
            Entity entity,
            out NavigationFlowFieldState fieldState)
        {
            JobHandle.ScheduleBatchedJobs();
            fieldState = world.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
            // 有限循环防止调度故障让编辑器验收无限等待
            for (int updateIndex = 0;
                 updateIndex < 10000 && fieldState.Status == NavigationPathStatus.Searching;
                 updateIndex++)
            {
                Thread.Yield();
                // 每次 Update 只推进真实 System，不直接 Complete 其内部句柄
                system.Update(world.Unmanaged);
                fieldState = world.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
            }
        }

        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
            var cells = new NavigationGridCellData[width * height];
            for (int index = 0; index < cells.Length; index++)
            {
                // 默认开放地面，具体测试只覆盖障碍或 Clearance 差异
                cells[index] = new NavigationGridCellData
                {
                    Height = 0f,
                    SurfaceNormal = Vector3.up,
                    TerrainCost = 1f,
                    Clearance = 10f,
                    Walkable = true,
                };
            }
            return cells;
        }

        private static void PrepareCells(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int clusterSize,
            float cellSize)
        {
            // 派生顺序与生产烘焙一致，后一步依赖前一步的拓扑结果
            NavigationGridAlgorithms.BuildConnectivity(cells, width, height, 0.5f);
            NavigationGridAlgorithms.CalculateClearance(cells, width, height, cellSize);
            NavigationGridAlgorithms.AssignClusters(cells, width, height, clusterSize);
            NavigationGridAlgorithms.AssignRegions(cells, width, height);
        }

        private static void SetWalkable(
            NavigationGridCellData[] cells,
            int width,
            int x,
            int z,
            bool walkable)
        {
            int index = x + z * width;
            NavigationGridCellData cell = cells[index];
            cell.Walkable = walkable;
            cells[index] = cell;
        }

        private static void SetClearance(
            NavigationGridCellData[] cells,
            int width,
            int x,
            int z,
            float clearance)
        {
            int index = x + z * width;
            NavigationGridCellData cell = cells[index];
            cell.Clearance = clearance;
            cells[index] = cell;
        }

        private static int CountRegions(NavigationGridCellData[] cells)
        {
            int maximum = 0;
            // RegionId 从一连续编号，因此最大值就是有效 Region 数量
            for (int index = 0; index < cells.Length; index++)
            {
                maximum = math.max(maximum, cells[index].RegionId);
            }
            return maximum;
        }

        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type target)
        {
            for (int index = 0; index < systems.Count; index++)
            {
                if (systems[index] == target)
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

        private readonly struct PathExecutionResult
        {
            public PathExecutionResult(NavigationPathJobResult result, int[] pathCells)
            {
                Result = result;
                PathCells = pathCells;
            }

            public NavigationPathJobResult Result { get; }
            public int[] PathCells { get; }
        }

        private readonly struct FlowExecutionResult
        {
            public FlowExecutionResult(
                NavigationFlowFieldJobResult[] results,
                int[] corridorClusters,
                int[] corridorPortals,
                int[] waypointCells,
                NavigationFlowFieldCell[] flowCells)
            {
                Results = results;
                CorridorClusters = corridorClusters;
                CorridorPortals = corridorPortals;
                WaypointCells = waypointCells;
                FlowCells = flowCells;
            }

            public NavigationFlowFieldJobResult[] Results { get; }
            public int[] CorridorClusters { get; }
            public int[] CorridorPortals { get; }
            public int[] WaypointCells { get; }
            public NavigationFlowFieldCell[] FlowCells { get; }
        }
    }
}
#endif
