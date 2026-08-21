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
    /// 自动验证端点纠正、普通 A*、路径平滑和异步结果写回
    /// </summary>
    public static class NavigationGridStageTwoValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Two Validation")]
        // 编辑器菜单和批处理执行同一组测试
        // 测试只创建临时 World 和 Native 容器，不修改项目资产
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity Batch Mode 执行阶段二全部验证
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 依次验证坐标转换、端点纠正、路线成本、重复结果和失败状态
        /// </summary>
        public static void RunAll()
        {
            // 先检查基础查询和失败情况，再验证成本选择与异步系统
            // 每项测试使用独立 Blob 和 Native 容器，避免互相影响
            TestCoordinateConversionAndProjection();
            TestRegionRejection();
            TestCornerLineOfSight();
            TestOpenGridSmoothing();
            TestClearanceChangesRoute();
            TestTerrainCostAndDeterminism();
            TestProjectionFailure();
            TestWorldFilterRegistration();
            TestAsynchronousPathfindingSystem();
            Debug.Log("Navigation Grid 阶段二自动验收通过");
        }

        // 寻路系统只能注册到服务器或本地模拟 World
        // 纯客户端不能执行只应由服务器负责的寻路逻辑
        private static void TestWorldFilterRegistration()
        {
            // 读取默认系统列表，直接检查 WorldSystemFilter 是否生效
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> localSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.LocalSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);

            Assert(
                ContainsSystem(serverSystems, typeof(ServerNavigationGridPathfindingSystem)),
                "Server World 必须自动注册 ServerNavigationGridPathfindingSystem");
            Assert(
                ContainsSystem(localSystems, typeof(ServerNavigationGridPathfindingSystem)),
                "Local World 必须自动注册 ServerNavigationGridPathfindingSystem");
            Assert(
                !ContainsSystem(clientSystems, typeof(ServerNavigationGridPathfindingSystem)),
                "Client World 不应自动注册 ServerNavigationGridPathfindingSystem");
        }

        // 不同程序集可能存在同名类型，因此按 Type 本身比较
        private static bool ContainsSystem(IReadOnlyList<Type> systems, Type targetType)
        {
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] == targetType)
                {
                    return true;
                }
            }

            return false;
        }

        // 检查世界坐标与格子索引的边界和往返转换
        // 同时确认落在障碍上的端点会纠正到最合适的附近格子
        private static void TestCoordinateConversionAndProjection()
        {
            // 使用不在世界原点的地图，确保坐标转换没有零点假设
            // 设置多个等距候选，检查最终选择顺序明确
            NavigationGridCellData[] cells = CreateWalkableCells(5, 5);
            SetWalkable(cells, 5, 2, 2, false);
            PrepareCells(cells, 5, 5, 0.5f);

            using BlobAssetReference<NavigationGridBlob> gridReference =
                CreateGrid(cells, 5, 5);
            ref NavigationGridBlob grid = ref gridReference.Value;
            float3 blockedCenter = new float3(2.5f, 0f, 2.5f);
            Assert(
                NavigationGridQuery.TryWorldToCell(
                    ref grid,
                    blockedCenter,
                    out int2 coordinate,
                    out int cellIndex),
                "Grid Bounds 内世界坐标必须能转换为 Cell");
            Assert(coordinate.Equals(new int2(2, 2)) && cellIndex == 12, "世界坐标转换结果错误");

            float3 roundTripPosition = NavigationGridQuery.GetCellWorldPosition(
                ref grid,
                cellIndex);
            Assert(
                math.distance(roundTripPosition.xz, blockedCenter.xz) <= 0.0001f,
                "Cell 中心转换回世界坐标后 XZ 应保持一致");

            Assert(
                NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    blockedCenter,
                    0.35f,
                    0f,
                    1,
                    out int firstProjection),
                "阻挡端点应投影到邻近合法 Cell");
            Assert(firstProjection != cellIndex, "端点投影不能保留阻挡 Cell");
            Assert(
                NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    blockedCenter,
                    0.35f,
                    0f,
                    1,
                    out int secondProjection) &&
                firstProjection == secondProjection,
                "相同端点投影必须得到稳定 Cell");
        }

        // 起终点位于不同静态连通区域时，应在 A* 展开节点前直接失败
        // 展开数为 0 可以证明搜索没有真正开始
        private static void TestRegionRejection()
        {
            // 用一整列障碍分开地图，同时让两端点本身仍可站立
            NavigationGridCellData[] cells = CreateWalkableCells(4, 1);
            cells[2].Height = 2f;
            cells[3].Height = 2f;
            PrepareCells(cells, 4, 1, 0.5f);

            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 4, 1);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 0.5f),
                new float3(3.5f, 2f, 0.5f),
                0.35f,
                1,
                maximumProjectionRadiusInCells: 0);
            PathExecutionResult execution = ExecutePath(grid, request);
            Assert(execution.Result.Status == NavigationPathStatus.Failed, "不连通 Region 必须失败");
            Assert(
                execution.Result.FailureReason == NavigationPathFailureReason.RegionMismatch,
                "不连通 Region 应在 A 星前返回 RegionMismatch");
            Assert(execution.Result.ExpandedNodeCount == 0, "Region 拒绝不应展开 A 星节点");
        }

        // 直线检查必须遵守烘焙邻接中禁止斜穿墙角的规则
        // 路径平滑不能重新加入 A* 已经避开的非法斜向连接
        private static void TestCornerLineOfSight()
        {
            // 只封锁斜向移动两侧的格子，目标格子本身保持开放
            // 直线检查应失败，不能偷偷返回一条绕行成本
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            SetWalkable(cells, 3, 1, 2, false);
            SetWalkable(cells, 3, 2, 1, false);
            PrepareCells(cells, 3, 3, 0.5f);

            using BlobAssetReference<NavigationGridBlob> gridReference =
                CreateGrid(cells, 3, 3);
            ref NavigationGridBlob grid = ref gridReference.Value;
            Assert(
                !NavigationGridQuery.TryCalculateLineCost(
                    ref grid,
                    1 + 1 * 3,
                    2 + 2 * 3,
                    0.35f,
                    0f,
                    0f,
                    out _),
                "直线可见性不能斜穿两个正交阻挡之间的角点");
        }

        // 开放地图上的锯齿父节点链应平滑为少量直线路点
        // 起点和终点必须始终保留，供下游移动使用
        private static void TestOpenGridSmoothing()
        {
            // 使用非轴对齐端点，让原始 A* 产生多个八方向节点
            // 开放地图平滑后应只保留可以直连的起终点
            NavigationGridCellData[] cells = CreateWalkableCells(8, 8);
            PrepareCells(cells, 8, 8, 0.5f);

            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 8, 8);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 0.5f),
                new float3(7.5f, 0f, 6.5f),
                0.35f,
                7,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f,
                smoothingCostTolerance: 0f);
            PathExecutionResult execution = ExecutePath(grid, request);
            Assert(execution.Result.Status == NavigationPathStatus.Succeeded, "开放 Grid 路径应成功");
            Assert(execution.PathCells.Length == 2, "开放 Grid 路径应平滑为起点和终点");
            Assert(execution.PathCells[0] == 0, "平滑路径必须保留起点 Cell");
            Assert(execution.PathCells[1] == 7 + 6 * 8, "平滑路径必须保留终点 Cell");
        }

        // 同一张地图中，小体型和大体型角色应选择不同通道
        // 这能确认运行时安全距离检查没有被静态连通区域代替
        private static void TestClearanceChangesRoute()
        {
            // 双通道地图让小体型走窄捷径，大体型绕行宽路
            // 两次请求共用同一 Blob，路线差异只能来自角色半径
            NavigationGridCellData[] cells = CreateWalkableCells(5, 3);
            for (int x = 1; x <= 3; x++)
            {
                cells[x + 1 * 5].Clearance = 0f;
            }

            PrepareCells(cells, 5, 3, 0.5f, preserveClearance: true);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 5, 3);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 1.5f),
                new float3(4.5f, 0f, 1.5f),
                0.8f,
                11,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f,
                smoothingCostTolerance: 0f);
            PathExecutionResult execution = ExecutePath(grid, request);
            Assert(execution.Result.Status == NavigationPathStatus.Succeeded, "较大 Agent 应能绕开窄区");
            Assert(
                ContainsCellOutsideRow(execution.PathCells, 5, 1),
                "路径必须绕开 Clearance 不足的直线路径");

            ref NavigationGridBlob gridData = ref grid.Value;
            for (int index = 0; index < execution.PathCells.Length; index++)
            {
                Assert(
                    NavigationGridQuery.CanAgentOccupy(
                        ref gridData,
                        execution.PathCells[index],
                        request.AgentRadius,
                        request.ClearanceMargin),
                    "平滑路径不能包含 Clearance 不足 Cell");
            }
        }

        // 直线路面成本很高时，搜索应选择距离更长但总成本更低的绕路
        // 重复执行必须得到相同格子序列和相同成本
        private static void TestTerrainCostAndDeterminism()
        {
            // 中间一行仍可通行，只是地形成本更高
            // 连续使用不同 Generation，检查复用临时数组不会改变结果
            NavigationGridCellData[] cells = CreateWalkableCells(5, 3);
            for (int x = 1; x <= 3; x++)
            {
                cells[x + 1 * 5].TerrainCost = 8f;
            }

            PrepareCells(cells, 5, 3, 0.5f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 5, 3);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 1.5f),
                new float3(4.5f, 0f, 1.5f),
                0.35f,
                23,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f,
                smoothingCostTolerance: 0f);

            PathExecutionResult first = ExecutePath(grid, request, 1);
            PathExecutionResult second = ExecutePath(grid, request, 2);
            PathExecutionResult third = ExecutePath(grid, request, 3);
            Assert(first.Result.Status == NavigationPathStatus.Succeeded, "成本地图路径应成功");
            Assert(
                ContainsCellOutsideRow(first.PathCells, 5, 1),
                "高地形成本必须使路径绕开直线高成本区");
            Assert(PathsEqual(first.PathCells, second.PathCells), "相同输入第二次路径不稳定");
            Assert(PathsEqual(first.PathCells, third.PathCells), "相同输入第三次路径不稳定");
        }

        // 搜索半径内没有可站立格子时，应返回明确的端点失败原因
        // 失败请求不能向共享路径数组留下部分数据
        private static void TestProjectionFailure()
        {
            // 将所有格子设为障碍并限制搜索半径，确保没有可用候选
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index].Walkable = false;
            }

            PrepareCells(cells, 3, 3, 0.5f);
            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 3, 3);
            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(1.5f, 0f, 1.5f),
                new float3(2.5f, 0f, 2.5f),
                0.35f,
                31,
                maximumProjectionRadiusInCells: 2);
            PathExecutionResult execution = ExecutePath(grid, request);
            Assert(
                execution.Result.FailureReason ==
                NavigationPathFailureReason.StartProjectionFailed,
                "没有合法 Cell 时应返回 StartProjectionFailed");
        }

        // 在临时 World 中检查请求从 Pending、Searching 到最终状态的异步流程
        // 同时确认版本匹配、路径缓冲区写回和 World 销毁时资源释放
        private static void TestAsynchronousPathfindingSystem()
        {
            // 测试使用正式组件和真实系统，销毁 World 时也会检查持久容器回收
            NavigationGridCellData[] cells = CreateWalkableCells(8, 8);
            PrepareCells(cells, 8, 8, 0.5f);

            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 8, 8);
            using var world = new World("Navigation Grid Stage Two Validation", WorldFlags.Game);
            AniMovementBackendWorldUtility.ConfigureWorld(
                world,
                AniMovementBackend.ClearanceGrid);
            EntityManager entityManager = world.EntityManager;
            Entity gridEntity = entityManager.CreateEntity(typeof(NavigationGridReference));
            entityManager.SetComponentData(gridEntity, new NavigationGridReference
            {
                Value = grid,
            });
            DynamicBuffer<NavigationDynamicOverlayCell> overlayCells =
                entityManager.AddBuffer<NavigationDynamicOverlayCell>(gridEntity);
            overlayCells.ResizeUninitialized(cells.Length);
            for (int cellIndex = 0; cellIndex < overlayCells.Length; cellIndex++)
            {
                overlayCells[cellIndex] = default;
            }

            NavigationPathRequest request = NavigationPathRequest.Create(
                new float3(0.5f, 0f, 0.5f),
                new float3(7.5f, 0f, 6.5f),
                0.35f,
                41,
                maximumProjectionRadiusInCells: 0,
                clearancePenaltyWeight: 0f,
                smoothingCostTolerance: 0f);
            Entity requestEntity = entityManager.CreateEntity(
                typeof(NavigationPathRequest),
                typeof(NavigationPathState));
            entityManager.AddBuffer<NavigationPathWaypoint>(requestEntity);
            entityManager.SetComponentData(requestEntity, request);
            entityManager.SetComponentData(
                requestEntity,
                NavigationPathState.CreatePending(request.Version));

            // 第一帧只能调度为 Searching，不能同步完成并立即写回
            SystemHandle system = world.GetOrCreateSystem<ServerNavigationGridPathfindingSystem>();
            system.Update(world.Unmanaged);
            NavigationPathState pathState =
                entityManager.GetComponentData<NavigationPathState>(requestEntity);
            Assert(
                pathState.Status == NavigationPathStatus.Searching,
                "路径系统首帧只能调度后台任务不能同步写回结果");

            // 主动刷新任务队列只为缩短编辑器测试等待，不改变正式系统逻辑
            JobHandle.ScheduleBatchedJobs();
            for (int updateIndex = 0;
                 updateIndex < 10000 && pathState.Status == NavigationPathStatus.Searching;
                 updateIndex++)
            {
                Thread.Yield();
                system.Update(world.Unmanaged);
                pathState = entityManager.GetComponentData<NavigationPathState>(requestEntity);
            }

            // 完成后同时检查状态、版本、纠正后端点和路径缓冲区
            DynamicBuffer<NavigationPathWaypoint> waypoints =
                entityManager.GetBuffer<NavigationPathWaypoint>(requestEntity);
            Assert(pathState.Status == NavigationPathStatus.Succeeded, "异步路径系统未完成请求");
            Assert(pathState.RequestVersion == request.Version, "异步结果请求版本不匹配");
            Assert(waypoints.Length == 2, "异步路径系统未写回平滑路径");
            Assert(waypoints[0].CellIndex == 0, "异步路径起点写回错误");
            Assert(waypoints[1].CellIndex == 7 + 6 * 8, "异步路径终点写回错误");
        }

        // 纯算法测试使用与正式后台任务相同形状的临时数组
        // 每次运行独立创建和释放，避免测试共享状态掩盖问题
        private static PathExecutionResult ExecutePath(
            BlobAssetReference<NavigationGridBlob> grid,
            NavigationPathRequest request,
            int generation = 1)
        {
            int cellCount = grid.Value.Cells.Length;
            var requests = new NativeArray<NavigationPathJobRequest>(
                1,
                Allocator.TempJob);
            var results = new NativeArray<NavigationPathJobResult>(
                1,
                Allocator.TempJob);
            var pathCells = new NativeList<int>(16, Allocator.TempJob);
            var gCosts = new NativeArray<float>(cellCount, Allocator.TempJob);
            var parents = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heap = new NativeArray<int>(cellCount, Allocator.TempJob);
            var heapPositions = new NativeArray<int>(cellCount, Allocator.TempJob);
            var nodeGenerations = new NativeArray<int>(
                cellCount,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            var dynamicOverlay = new NativeArray<NavigationDynamicOverlayCell>(
                cellCount,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            try
            {
                requests[0] = new NavigationPathJobRequest
                {
                    Entity = Entity.Null,
                    Request = request,
                };
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
                    NodeGenerations = nodeGenerations,
                    GenerationStart = generation,
                    DynamicOverlay = dynamicOverlay,
                };
                JobHandle handle = job.Schedule();
                handle.Complete();

                NavigationPathJobResult result = results[0];
                var copiedPath = new int[result.PathLength];
                for (int pathIndex = 0; pathIndex < result.PathLength; pathIndex++)
                {
                    copiedPath[pathIndex] = pathCells[result.PathOffset + pathIndex];
                }

                return new PathExecutionResult(result, copiedPath);
            }
            finally
            {
                nodeGenerations.Dispose();
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

        // 创建高度和成本一致的开放地图作为基线
        // 各测试只修改与目标行为有关的格子
        private static NavigationGridCellData[] CreateWalkableCells(int width, int height)
        {
            var cells = new NavigationGridCellData[width * height];
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = new NavigationGridCellData
                {
                    Height = 0f,
                    SurfaceNormal = Vector3.up,
                    SlopeDegrees = 0f,
                    TerrainCost = 1f,
                    Clearance = 10f,
                    RegionId = 0,
                    ClusterId = 0,
                    NeighborMask = NavigationNeighborMask.None,
                    Walkable = true,
                };
            }

            return cells;
        }

        // 使用正式烘焙算法生成邻接、分块和连通区域，不手工伪造 NeighborMask
        private static void PrepareCells(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float maximumStepHeight,
            bool preserveClearance = false)
        {
            NavigationGridBakingAlgorithms.BuildConnectivity(
                cells,
                width,
                height,
                maximumStepHeight);
            if (!preserveClearance)
            {
                for (int index = 0; index < cells.Length; index++)
                {
                    NavigationGridCellData cell = cells[index];
                    cell.Clearance = cell.Walkable ? 10f : 0f;
                    cells[index] = cell;
                }
            }

            NavigationGridBakingAlgorithms.AssignClusters(cells, width, height, 4);
            NavigationGridBakingAlgorithms.AssignRegions(cells, width, height);
        }

        // 将托管格子数组转换成与 SubScene 烘焙结果相同的 Blob
        // 每个测试负责释放自己创建的 Blob
        private static BlobAssetReference<NavigationGridBlob> CreateGrid(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float cellSize = 1f,
            float baseAgentRadius = 0.35f)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            root.BoundsMinimum = new float3(0f, -1f, 0f);
            root.BoundsMaximum = new float3(width * cellSize, 3f, height * cellSize);
            root.CellSize = cellSize;
            root.BaseAgentRadius = baseAgentRadius;
            root.BaseAgentHeight = 1.5f;
            root.Width = width;
            root.Height = height;
            root.ClusterSizeInCells = 4;
            root.RegionCount = CountRegions(cells);
            root.DataVersion = NavigationGridBakeAsset.CurrentDataVersion;

            BlobBuilderArray<NavigationGridCell> blobCells =
                builder.Allocate(ref root.Cells, cells.Length);
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

            BlobAssetReference<NavigationGridBlob> result =
                builder.CreateBlobAssetReference<NavigationGridBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }

        // 有效区域从 1 连续编号，因此最大 RegionId 就是区域总数
        private static int CountRegions(NavigationGridCellData[] cells)
        {
            int maximumRegion = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                maximumRegion = Mathf.Max(maximumRegion, cells[index].RegionId);
            }

            return maximumRegion;
        }

        // 按坐标修改格子的可行走状态和地形成本
        // 结构体先复制再写回，避免修改局部副本后丢失
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

        // 检查最终路线是否确实绕开指定的高成本或阻挡行
        private static bool ContainsCellOutsideRow(int[] path, int width, int row)
        {
            for (int index = 0; index < path.Length; index++)
            {
                if (path[index] / width != row)
                {
                    return true;
                }
            }

            return false;
        }

        // 重复结果要求路径长度和每个位置都一致
        // 不能使用集合比较，因为路点顺序也是结果的一部分
        private static bool PathsEqual(int[] left, int[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        // 失败时抛出异常，让 Console 和批处理退出码都能报告原因
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private readonly struct PathExecutionResult
        {
            // 临时数组容量严格等于格子数，用于发现越界或错误容量假设
            // 返回值同时保存搜索结果和已经复制出来的路径数组
            public PathExecutionResult(NavigationPathJobResult result, int[] pathCells)
            {
                Result = result;
                PathCells = pathCells;
            }

            public NavigationPathJobResult Result { get; }

            public int[] PathCells { get; }
        }
    }
}
#endif
