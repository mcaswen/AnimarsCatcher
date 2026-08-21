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
    /// 自动验证分块入口、分层寻路、局部 Flow Field、动态障碍改路和缓存复用
    /// </summary>
    public static class NavigationGridStageThreeValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Three Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行阶段三全部验证
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次验证分层数据、宏观路线、Flow Field、缓存和异步系统接线
        /// </summary>
        public static void RunAll()
        {
            // 先检查纯算法，再检查 World 中的系统注册与异步生命周期
            TestHierarchyBakeDeterminism();
            TestHierarchicalReachabilityFieldAndCache();
            TestDynamicOverlayReselectsCorridor();
            TestFlowDirectionBellmanFallback();
            TestCacheCapacityRecycles();
            TestPortalClearanceFiltering();
            TestWorldFilterRegistration();
            TestAsynchronousFlowFieldSystem();
            TestBenchmarkWorkloadScales();
            // 所有断言通过后才输出成功标记
            Debug.Log("Navigation Grid 阶段三自动验收通过");
        }

        private static void TestHierarchyBakeDeterminism()
        {
            // 使用开放地图，排除障碍布局对分块入口生成顺序的影响
            NavigationGridCellData[] cells = CreateWalkableCells(16, 8);
            PrepareCells(cells, 16, 8, 4, 1f);
            NavigationGridHierarchyBuildResult first = NavigationGridHierarchyBuilder.Build(
                cells,
                16,
                8,
                4,
                1f);
            // 用相同输入重新构建一次，确认分层数据完全一致
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
            // 中央墙只留两个开口，强制宏观路线明确选择分块入口
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
            // 普通 A* 提供实际可达和路线成本基线
            PathExecutionResult ordinary = ExecuteOrdinaryPath(grid, request);
            // 连续提交两个相同请求，检查同一批次内能否复用 Flow Field 缓存
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

            // 先把宏观通道转换为分块集合，便于检查 Flow Field 是否越界
            var corridorSet = new HashSet<int>();
            for (int index = 0; index < flow.Results[0].CorridorClusterCount; index++)
            {
                corridorSet.Add(flow.CorridorClusters[
                    flow.Results[0].CorridorClusterOffset + index]);
            }

            var integrationCosts = new Dictionary<int, float>();
            // 建立格子到剩余成本的索引，便于检查每个方向指向的邻格
            for (int index = 0; index < flow.Results[0].FieldCount; index++)
            {
                NavigationFlowFieldCell fieldCell = flow.FlowCells[
                    flow.Results[0].FieldOffset + index];
                integrationCosts[fieldCell.CellIndex] = fieldCell.IntegrationCost;
                Assert(
                    corridorSet.Contains(grid.Value.Cells[fieldCell.CellIndex].ClusterId),
                    "Field 包含 Corridor 外 Cell");
            }

            // 最后逐格确认方向指向合法且保持最低总成本的邻居
            ValidateDescendingDirections(ref grid.Value, flow, 0, integrationCosts, request);
        }

        private static void TestPortalClearanceFiltering()
        {
            const int Width = 8;
            const int Height = 3;
            // 两个分块边界开口分别形成窄入口和宽入口
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Height);
            SetWalkable(cells, Width, 3, 0, false);
            SetWalkable(cells, Width, 4, 0, false);

            NavigationGridBakingAlgorithms.BuildConnectivity(cells, Width, Height, 0.5f);
            NavigationGridBakingAlgorithms.AssignClusters(cells, Width, Height, 4);
            NavigationGridBakingAlgorithms.AssignRegions(cells, Width, Height);
            // 先固定连接关系，再手工设置安全距离，只测试入口按宽度拆分的行为
            for (int index = 0; index < cells.Length; index++)
            {
                NavigationGridCellData cell = cells[index];
                cell.Clearance = cell.Walkable ? 2f : 0f;
                cells[index] = cell;
            }
            // 宽度 1.1 和 1.8 不能合并成同一入口，否则宽段会被窄段的最小值限制
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
            // 角色体型设置为只能通过宽入口
            FlowExecutionResult flow = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) });
            Assert(flow.Results[0].Status == NavigationPathStatus.Succeeded, "大体型应通过宽 Portal");
            float requiredClearance = request.AgentRadius - grid.Value.BaseAgentRadius;
            // 逐项确认最终通道没有使用空间不足的入口
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

        private static void TestDynamicOverlayReselectsCorridor()
        {
            // 三乘三分块提供穿过中央、绕上方和绕下方三类宏观路线
            // 起终点位于中间一行，静态最短路线会穿过中央分块
            // 动态墙只切断中央分块内部，入口代表格子本身仍保持开放
            // 宏观搜索必须发现内部已不可达并改走外围，而不是等到 Flow Field 阶段才失败
            // 另一项测试只增加成本、不阻挡格子，确认宏观路线也会选择更便宜的绕路
            const int Width = 12;
            const int Height = 12;
            const int ClusterSize = 4;
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Height);
            PrepareCells(cells, Width, Height, ClusterSize, 1f);
            using BlobAssetReference<NavigationGridBlob> grid =
                CreateGrid(cells, Width, Height, ClusterSize);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(1.5f, 0f, 5.5f),
                new float3(10.5f, 0f, 5.5f),
                0.35f,
                21,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f);
            FlowExecutionResult baseline = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) });
            Assert(baseline.Results[0].Status == NavigationPathStatus.Succeeded,
                "动态 Corridor 夹具的静态基线失败");

            // 在中央分块内建立贯穿墙，但保留入口代表格子，用来复现静态内部连接失效
            var blockedOverlay = new NavigationDynamicOverlayCell[Width * Height];
            for (int z = 4; z < 8; z++)
            {
                blockedOverlay[5 + z * Width] = new NavigationDynamicOverlayCell
                {
                    BlockCount = 1,
                    Version = 2,
                };
            }

            FlowExecutionResult blocked = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) },
                blockedOverlay,
                2);
            Assert(blocked.Results[0].Status == NavigationPathStatus.Succeeded,
                "中央动态墙存在替代路线时不应沿失效静态 Corridor 返回失败");
            Assert(!CorridorEquals(baseline, blocked),
                "动态墙未触发宏观 Corridor 重选");

            // 很高的动态附加成本也必须影响宏观路线，不能等通道选定后才在 Flow Field 中生效
            var expensiveOverlay = new NavigationDynamicOverlayCell[Width * Height];
            for (int z = 4; z < 8; z++)
            {
                for (int x = 4; x < 8; x++)
                {
                    expensiveOverlay[x + z * Width] = new NavigationDynamicOverlayCell
                    {
                        ExtraCost = 100f,
                        Version = 2,
                    };
                }
            }

            FlowExecutionResult expensive = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) },
                expensiveOverlay,
                2);
            Assert(expensive.Results[0].Status == NavigationPathStatus.Succeeded,
                "动态额外成本夹具应保持可达");
            Assert(!CorridorEquals(baseline, expensive),
                "动态额外成本未参与宏观 Corridor 选择");
        }

        private static void TestFlowDirectionBellmanFallback()
        {
            // 使用单个分块，排除宏观通道选择对方向结果的影响
            // 中心到目标的直线被一个格子挡住，左右绕行完全对称
            // 东西两个下一格具有相同最低总成本，方向混合后会互相抵消
            // 即使混合向量为零，也必须选择一条真实可走的最优边
            // 西侧格子索引更小，因此预期方向固定为负 X
            // 测试运行完整的 Flow Field 任务来生成成本，不手工填入临时数据
            const int Width = 5;
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Width);
            // 目标正南方的障碍使中心格子产生东西两个对称的最优下一格
            SetWalkable(cells, Width, 2, 1, false);
            PrepareCells(cells, Width, Width, Width, 1f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, Width, Width, Width);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(2.5f, 0f, 2.5f),
                new float3(2.5f, 0f, 0.5f),
                0.35f,
                22,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f);
            FlowExecutionResult flow = ExecuteFlowBatch(
                grid,
                new[] { NavigationFlowFieldRequest.Create(request) });
            Assert(flow.Results[0].Status == NavigationPathStatus.Succeeded,
                "对称 Flow Direction 夹具构建失败");
            const int Center = 12;
            bool foundCenter = false;
            for (int index = 0; index < flow.Results[0].FieldCount; index++)
            {
                NavigationFlowFieldCell fieldCell =
                    flow.FlowCells[flow.Results[0].FieldOffset + index];
                if (fieldCell.CellIndex != Center)
                {
                    continue;
                }

                foundCenter = true;
                Assert(math.lengthsq(fieldCell.Direction) > 0.5f,
                    "对称 Bellman 后继抵消后输出了零方向");
                Assert(math.all(fieldCell.Direction == new float2(-1f, 0f)),
                    "对称 Bellman 后继未稳定选择较小 Cell 索引");
            }

            Assert(foundCenter, "对称 Flow Direction 夹具未输出中心 Cell");
        }

        private static void TestCacheCapacityRecycles()
        {
            // 65 个请求共用同一分块通道，但目标格子不同，因此缓存键也不同
            // 前 64 项正好填满缓存，不应提前换代
            // 第 65 项触发整代清理，并成为新一代第一项
            // 第 66 项重复第 65 个目标，必须命中新一代缓存
            // 测试只读取公开任务结果，不访问内部缓存容器
            // 同时检查换代后的 Flow Field 切片和起点成本仍然有效
            const int Width = 9;
            NavigationGridCellData[] cells = CreateWalkableCells(Width, Width);
            PrepareCells(cells, Width, Width, Width, 1f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, Width, Width, Width);
            var requests = new NavigationFlowFieldRequest[66];
            // 前 65 个目标填满并换代，最后一项重复第 65 个目标并验证缓存命中
            for (int target = 0; target <= 64; target++)
            {
                int x = target % Width;
                int z = target / Width;
                NavigationPathRequest request = NavigationPathRequest.Create(
                    new float3(0.5f, 0f, 0.5f),
                    new float3(x + 0.5f, 0f, z + 0.5f),
                    0.35f,
                    (uint)(100 + target),
                    maximumProjectionRadiusInCells: 0,
                    clearancePenaltyWeight: 0f);
                requests[target] = NavigationFlowFieldRequest.Create(request);
            }

            requests[65] = NavigationFlowFieldRequest.Create(
                NavigationPathRequest.Create(
                    new float3(0.5f, 0f, 0.5f),
                    new float3(1.5f, 0f, 7.5f),
                    0.35f,
                    200,
                    maximumProjectionRadiusInCells: 0,
                    clearancePenaltyWeight: 0f));
            FlowExecutionResult flow = ExecuteFlowBatch(grid, requests);
            Assert(flow.Results[64].Status == NavigationPathStatus.Succeeded,
                "缓存换代触发请求失败");
            Assert(flow.Results[65].Status == NavigationPathStatus.Succeeded &&
                   flow.Results[65].CacheHit != 0,
                "缓存达到容量后没有回收并接纳新目标");
        }

        private static void TestWorldFilterRegistration()
        {
            // 分别读取服务器、本地模拟和客户端的默认系统列表
            // 无需启动完整游戏 World，就能检查系统过滤声明
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> localSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.LocalSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);
            // 服务器和本地模拟可以寻路，纯客户端不能注册只应由服务器运行的导航系统
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
            // 使用隔离 World，避免项目中的其他系统改变测试请求状态
            using var world = new World("Navigation Grid Stage Three Validation", WorldFlags.Game);
            AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
            EntityManager entityManager = world.EntityManager;
            Entity gridEntity = entityManager.CreateEntity(typeof(NavigationGridReference));
            entityManager.SetComponentData(gridEntity, new NavigationGridReference { Value = grid });
            DynamicBuffer<NavigationDynamicOverlayCell> overlayCells =
                entityManager.AddBuffer<NavigationDynamicOverlayCell>(gridEntity);
            overlayCells.ResizeUninitialized(cells.Length);
            for (int cellIndex = 0; cellIndex < overlayCells.Length; cellIndex++)
            {
                overlayCells[cellIndex] = default;
            }
            DynamicBuffer<NavigationDynamicOverlayCluster> overlayClusters =
                entityManager.AddBuffer<NavigationDynamicOverlayCluster>(gridEntity);
            overlayClusters.ResizeUninitialized(grid.Value.Clusters.Length);
            for (int clusterIndex = 0; clusterIndex < overlayClusters.Length; clusterIndex++)
            {
                overlayClusters[clusterIndex] = default;
            }
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
            // 第一次更新只能调度后台任务并进入 Searching
            system.Update(world.Unmanaged);
            NavigationFlowFieldState fieldState =
                entityManager.GetComponentData<NavigationFlowFieldState>(requestEntity);
            Assert(fieldState.Status == NavigationPathStatus.Searching, "阶段三首帧不应同步完成搜索");
            WaitForFlowResult(world, system, requestEntity, out fieldState);
            Assert(fieldState.Status == NavigationPathStatus.Succeeded, "异步阶段三请求失败");

            // 递增版本后重新提交相同目标和通道，检查跨帧缓存复用
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
            // 三种规模使用同一配置，只改变请求 Entity 数量
            for (int countIndex = 0; countIndex < counts.Length; countIndex++)
            {
                int count = counts[countIndex];
                // 每种规模使用独立 World，避免上一轮 Entity 和系统状态影响统计
                using var world = new World($"Stage Three Benchmark {count}", WorldFlags.Game);
                AniMovementBackendWorldUtility.ConfigureWorld(world, AniMovementBackend.ClearanceGrid);
                EntityManager entityManager = world.EntityManager;
                Entity configEntity = entityManager.CreateEntity(
                    typeof(NavigationGridBenchmarkConfig),
                    typeof(NavigationGridBenchmarkState));
                entityManager.AddBuffer<NavigationGridBenchmarkCommand>(configEntity);
                entityManager.AddBuffer<NavigationGridBenchmarkTimingSample>(configEntity);
                DynamicBuffer<NavigationGridBenchmarkCommand> commands =
                    entityManager.GetBuffer<NavigationGridBenchmarkCommand>(configEntity);
                commands.Add(new NavigationGridBenchmarkCommand
                {
                    Tick = 0,
                    TargetOffset = new float3(-20f, 0f, 16f),
                });
                entityManager.SetComponentData(configEntity, new NavigationGridBenchmarkConfig
                {
                    Workload = NavigationGridBenchmarkWorkload.PathAndField,
                    AgentCount = count,
                    WarmupTicks = 1,
                    SampleTicks = 2,
                    SpawnColumnCount = 16,
                    SpawnSpacing = 1.25f,
                    SpawnOrigin = new float3(105f, 0.57f, 44.43f),
                    AgentRadius = 0.35f,
                });

                // 先让数据源系统创建唯一的基准导航网格
                SystemHandle gridSystem =
                    world.GetOrCreateSystem<ServerNavigationGridBenchmarkGridSystem>();
                gridSystem.Update(world.Unmanaged);
                // 只有一张网格时，缓存索引和结果归属才有明确含义
                using EntityQuery grids = entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NavigationGridReference>());
                Assert(grids.CalculateEntityCount() == 1, "Shared benchmark did not get one Grid");

                SystemHandle benchmarkSystem =
                    world.GetOrCreateSystem<ServerNavigationGridBenchmarkSystem>();
                benchmarkSystem.Update(world.Unmanaged);
                using EntityQuery requests = entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NavigationGridBenchmarkRequestTag>());
                Assert(requests.CalculateEntityCount() == count, $"{count} 规模未生成对应 Field 工作负载");
                // 逐个 Entity 检查纯寻路模式没有意外添加移动组件
                using NativeArray<Entity> entities = requests.ToEntityArray(Allocator.Temp);
                for (int index = 0; index < entities.Length; index++)
                {
                    using NativeArray<ComponentType> componentTypes =
                        entityManager.GetComponentTypes(entities[index], Allocator.Temp);
                    for (int typeIndex = 0; typeIndex < componentTypes.Length; typeIndex++)
                    {
                        Type managedType = TypeManager.GetType(componentTypes[typeIndex].TypeIndex);
                        // 出现 LocalTransform 说明纯寻路基准错误地创建了可移动 Ani
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
            Dictionary<int, float> integrationCosts,
            NavigationPathRequest request)
        {
            // 方向依次检查非零、对应真实相邻格，以及满足 Bellman 最优条件
            // 成本下降可防止局部环，Bellman 条件可防止选择更贵的下降边
            // 当前场景没有动态障碍，因此下一格成本不包含动态附加项
            // 动态成本由前面的宏观改路测试覆盖
            NavigationFlowFieldJobResult result = flow.Results[resultIndex];
            for (int index = 0; index < result.FieldCount; index++)
            {
                NavigationFlowFieldCell fieldCell = flow.FlowCells[result.FieldOffset + index];
                if (math.lengthsq(fieldCell.Direction) <= 0.0001f)
                {
                    // 只有纠正后的目标格子允许方向为零
                    Assert(
                        fieldCell.CellIndex == result.ProjectedEndCellIndex,
                        "非目标 Cell 的 Flow Direction 不能为零");
                    continue;
                }

                int deltaX = (int)math.round(fieldCell.Direction.x);
                int deltaZ = (int)math.round(fieldCell.Direction.y);
                // 平滑方向最终仍必须对应八邻域中的一个真实格子
                int x = fieldCell.CellIndex % grid.Width + deltaX;
                int z = fieldCell.CellIndex / grid.Width + deltaZ;
                // 非零方向必须能映射到地图范围内的相邻格子
                Assert(x >= 0 && x < grid.Width && z >= 0 && z < grid.Height, "Flow 指向 Grid 外");
                int neighbor = x + z * grid.Width;
                Assert(grid.Cells[neighbor].Walkable != 0, "Flow 指向不可行走 Cell");
                Assert(integrationCosts.TryGetValue(neighbor, out float neighborCost), "Flow 指向 Field 外 Cell");
                Assert(
                    neighborCost < fieldCell.IntegrationCost - 0.00001f,
                    "平滑后 Flow Direction 未保持 Integration Cost 下降");
                float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                    ref grid,
                    request.AgentRadius,
                    request.ClearanceMargin);
                float successorCost = neighborCost + NavigationGridCost.CalculateStepCost(
                    ref grid,
                    fieldCell.CellIndex,
                    neighbor,
                    requiredClearance,
                    request.ClearancePenaltyWeight);
                Assert(math.abs(successorCost - fieldCell.IntegrationCost) <= 0.0001f,
                    "Flow Direction 未保持 Bellman 最优后继");
            }
        }

        private static FlowExecutionResult ExecuteFlowBatch(
            BlobAssetReference<NavigationGridBlob> grid,
            NavigationFlowFieldRequest[] requests,
            NavigationDynamicOverlayCell[] overlayCells = null,
            uint overlayVersion = 1)
        {
            // 临时数组按正式系统使用的格子、分块和节点数量分配
            int cellCount = grid.Value.Cells.Length;
            int clusterCount = grid.Value.Clusters.Length;
            int nodeCount = grid.Value.PortalNodes.Length;
            // 输入请求和结果数组按下标一一对应
            var jobRequests = new NativeArray<NavigationFlowFieldJobRequest>(requests.Length, Allocator.TempJob);
            var results = new NativeArray<NavigationFlowFieldJobResult>(requests.Length, Allocator.TempJob);
            // 四个 NativeList 对应后台任务的四类共享输出
            var corridorClusters = new NativeList<int>(64, Allocator.TempJob);
            var corridorPortals = new NativeList<int>(64, Allocator.TempJob);
            var waypointCells = new NativeList<int>(128, Allocator.TempJob);
            var flowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.TempJob);
            // 格子临时数组由局部 Dijkstra 和 Integration Field 共用
            var cellCosts = new NativeArray<float>(cellCount, Allocator.TempJob);
            var cellHeap = new NativeArray<int>(cellCount, Allocator.TempJob);
            var cellHeapPositions = new NativeArray<int>(cellCount, Allocator.TempJob);
            var cellGenerations = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var clusterGenerations = new NativeArray<int>(clusterCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            // 抽象搜索临时数组按入口节点数量分配
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
            var dynamicOverlay = new NativeArray<NavigationDynamicOverlayCell>(
                cellCount,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            var dynamicOverlayClusters = new NativeArray<NavigationDynamicOverlayCluster>(
                clusterCount,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            try
            {
                if (overlayCells != null)
                {
                    // 托管输入先复制到与正式任务相同形状的 NativeArray
                    // 只有当前仍包含动态影响的分块才写入非零版本
                    // 全局动态障碍版本非零时，宏观搜索才启用动态边重算
                    // AffectedCellCount 仅用于诊断，不影响路线选择
                    Assert(overlayCells.Length == cellCount, "测试 Overlay 长度与 Grid 不一致");
                    for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
                    {
                        NavigationDynamicOverlayCell overlayCell = overlayCells[cellIndex];
                        dynamicOverlay[cellIndex] = overlayCell;
                        if (overlayCell.BlockCount <= 0 &&
                            overlayCell.ExtraCost <= 0f &&
                            overlayCell.ClearanceReduction <= 0f)
                        {
                            continue;
                        }

                        int clusterIndex = grid.Value.Cells[cellIndex].ClusterId;
                        NavigationDynamicOverlayCluster cluster =
                            dynamicOverlayClusters[clusterIndex];
                        cluster.Version = overlayVersion;
                        cluster.AffectedCellCount++;
                        dynamicOverlayClusters[clusterIndex] = cluster;
                    }
                }

                // 数组顺序就是后台任务处理请求的固定顺序
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
                    DynamicOverlay = dynamicOverlay,
                    DynamicOverlayClusters = dynamicOverlayClusters,
                    DynamicOverlayVersion = overlayVersion,
                };
                // 测试中同步等待只是为了立即读取结果，正式系统不会这样阻塞主线程
                JobHandle handle = job.Schedule();
                handle.Complete();
                // 退出作用域前将结果复制为托管数据，返回对象不持有 Native 容器
                return new FlowExecutionResult(
                    results.ToArray(),
                    corridorClusters.AsArray().ToArray(),
                    corridorPortals.AsArray().ToArray(),
                    waypointCells.AsArray().ToArray(),
                    flowCells.AsArray().ToArray());
            }
            finally
            {
                // finally 确保任务或断言异常时也会释放所有 TempJob 内存
                cacheCells.Dispose();
                cacheClusters.Dispose();
                cacheEntries.Dispose();
                dynamicOverlay.Dispose();
                dynamicOverlayClusters.Dispose();
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

        private static bool CorridorEquals(
            FlowExecutionResult left,
            FlowExecutionResult right)
        {
            // 每条结果在共享数组中的起点可能不同，比较时必须使用各自切片范围
            // 分块顺序属于宏观路线的一部分，即使集合相同，顺序不同也算路线变化
            // 分块序列已经决定入口和宏观路点，这里无需重复比较
            NavigationFlowFieldJobResult leftResult = left.Results[0];
            NavigationFlowFieldJobResult rightResult = right.Results[0];
            if (leftResult.CorridorClusterCount != rightResult.CorridorClusterCount)
            {
                return false;
            }

            for (int index = 0; index < leftResult.CorridorClusterCount; index++)
            {
                if (left.CorridorClusters[leftResult.CorridorClusterOffset + index] !=
                    right.CorridorClusters[rightResult.CorridorClusterOffset + index])
                {
                    return false;
                }
            }

            return true;
        }

        private static PathExecutionResult ExecuteOrdinaryPath(
            BlobAssetReference<NavigationGridBlob> grid,
            NavigationPathRequest request)
        {
            int cellCount = grid.Value.Cells.Length;
            // 普通 A* 的临时数组全部按格子数分配
            var requests = new NativeArray<NavigationPathJobRequest>(1, Allocator.TempJob);
            var results = new NativeArray<NavigationPathJobResult>(1, Allocator.TempJob);
            var pathCells = new NativeList<int>(32, Allocator.TempJob);
            var gCosts = new NativeArray<float>(cellCount, Allocator.TempJob);
            var parents = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heap = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heapPositions = new NativeArray<int>(cellCount, Allocator.TempJob);
            var generations = new NativeArray<int>(cellCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var dynamicOverlay = new NativeArray<NavigationDynamicOverlayCell>(
                cellCount,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
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
                    DynamicOverlay = dynamicOverlay,
                };
                // 同步完成后只保留状态、成本、展开数和路径格子
                JobHandle handle = job.Schedule();
                handle.Complete();
                return new PathExecutionResult(results[0], pathCells.AsArray().ToArray());
            }
            finally
            {
                // 普通路径基线与 Flow Field 测试使用相同的异常释放规则
                generations.Dispose();
                dynamicOverlay.Dispose();
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
            // 分层数据直接根据当前测试格子的连接关系生成
            NavigationGridHierarchyBuildResult hierarchy = NavigationGridHierarchyBuilder.Build(
                cells,
                width,
                height,
                clusterSize,
                1f);
            // 临时 Builder 只用于构建，返回的 Blob 使用独立 Persistent 内存
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
            // 使用固定哈希，让测试只关注地图连接和算法结果
            root.DataHash = new Unity.Entities.Hash128("00000000000000000000000000000001");

            // 测试 Blob 的格子布局与正式 Baker 保持一致
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

            // 分块直接复制构建器生成的入口节点切片起点和数量
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

            // 分块入口保留两侧范围、代表格子和双向成本
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

            // 入口节点保留所属分块和出边切片
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

            // 抽象连接的 byte 标记与正式 Blob 表示一致
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

            // 每个分块通过连续索引数组找到自己的入口节点
            BlobBuilderArray<int> clusterNodeIndices = builder.Allocate(
                ref root.ClusterPortalNodeIndices,
                hierarchy.ClusterPortalNodeIndices.Length);
            for (int index = 0; index < hierarchy.ClusterPortalNodeIndices.Length; index++)
            {
                clusterNodeIndices[index] = hierarchy.ClusterPortalNodeIndices[index];
            }

            // 返回 Persistent Blob，由每个测试作用域负责释放
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
            // Flow Field 系统写回前要求四类输出缓冲区全部存在
            entityManager.AddBuffer<NavigationCorridorCluster>(entity);
            entityManager.AddBuffer<NavigationCorridorPortal>(entity);
            entityManager.AddBuffer<NavigationHierarchicalWaypoint>(entity);
            entityManager.AddBuffer<NavigationFlowFieldCell>(entity);
            entityManager.SetComponentData(entity, request);
            // 请求和状态使用相同版本，运行时写回会同时检查两者
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
            // 使用有限循环，避免任务调度故障让编辑器测试无限等待
            for (int updateIndex = 0;
                 updateIndex < 10000 && fieldState.Status == NavigationPathStatus.Searching;
                 updateIndex++)
            {
                Thread.Yield();
                // 每次更新只运行真实系统，不直接访问或 Complete 内部任务句柄
                system.Update(world.Unmanaged);
                fieldState = world.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
            }
        }

        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
            var cells = new NavigationGridCellData[width * height];
            for (int index = 0; index < cells.Length; index++)
            {
                // 默认创建开放地面，各测试只覆盖需要的障碍或安全距离差异
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
            // 使用与正式烘焙相同的计算顺序，后一步读取前一步结果
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, width, height, 0.5f);
            NavigationEuclideanDistanceTransform.Calculate(cells, width, height, cellSize);
            NavigationGridBakingAlgorithms.AssignClusters(cells, width, height, clusterSize);
            NavigationGridBakingAlgorithms.AssignRegions(cells, width, height);
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
            // 有效区域从 1 连续编号，因此最大 RegionId 就是区域数量
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
