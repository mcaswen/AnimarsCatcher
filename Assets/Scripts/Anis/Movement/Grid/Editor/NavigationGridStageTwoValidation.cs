#if UNITY_EDITOR
using System;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Animars.Movement.Grid.Editor
{
    /// <summary>
    /// 执行阶段二端点投影、普通 A 星和路径平滑自动验收
    /// </summary>
    public static class NavigationGridStageTwoValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Two Validation")]
        private static void RunFromMenu()
        {
            RunAll();
        }

        /// <summary>
        /// 供 Unity 批处理执行阶段二完整验收
        /// </summary>
        public static void RunFromCommandLine()
        {
            RunAll();
        }

        /// <summary>
        /// 执行坐标、投影、路径成本、确定性和失败状态验证
        /// </summary>
        public static void RunAll()
        {
            TestCoordinateConversionAndProjection();
            TestRegionRejection();
            TestCornerLineOfSight();
            TestOpenGridSmoothing();
            TestClearanceChangesRoute();
            TestTerrainCostAndDeterminism();
            TestProjectionFailure();
            TestAsynchronousPathfindingSystem();
            Debug.Log("Navigation Grid 阶段二自动验收通过");
        }

        private static void TestCoordinateConversionAndProjection()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(5, 5);
            SetWalkable(cells, 5, 2, 2, false);
            PrepareCells(cells, 5, 5, 0.5f);

            using BlobAssetReference<NavigationGridBlob> gridReference =
                CreateGrid(cells, 5, 5);
            ref NavigationGridBlob grid = ref gridReference.Value;
            float3 blockedCenter = new float3(2.5f, 0f, 2.5f);
            Assert(
                NavigationGridPathAlgorithms.TryWorldToCell(
                    ref grid,
                    blockedCenter,
                    out int2 coordinate,
                    out int cellIndex),
                "Grid Bounds 内世界坐标必须能转换为 Cell");
            Assert(coordinate.Equals(new int2(2, 2)) && cellIndex == 12, "世界坐标转换结果错误");

            float3 roundTripPosition = NavigationGridPathAlgorithms.GetCellWorldPosition(
                ref grid,
                cellIndex);
            Assert(
                math.distance(roundTripPosition.xz, blockedCenter.xz) <= 0.0001f,
                "Cell 中心转换回世界坐标后 XZ 应保持一致");

            Assert(
                NavigationGridPathAlgorithms.TryProjectToNearestCell(
                    ref grid,
                    blockedCenter,
                    0.35f,
                    0f,
                    1,
                    out int firstProjection),
                "阻挡端点应投影到邻近合法 Cell");
            Assert(firstProjection != cellIndex, "端点投影不能保留阻挡 Cell");
            Assert(
                NavigationGridPathAlgorithms.TryProjectToNearestCell(
                    ref grid,
                    blockedCenter,
                    0.35f,
                    0f,
                    1,
                    out int secondProjection) &&
                firstProjection == secondProjection,
                "相同端点投影必须得到稳定 Cell");
        }

        private static void TestRegionRejection()
        {
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

        private static void TestCornerLineOfSight()
        {
            NavigationGridCellData[] cells = CreateWalkableCells(3, 3);
            SetWalkable(cells, 3, 1, 2, false);
            SetWalkable(cells, 3, 2, 1, false);
            PrepareCells(cells, 3, 3, 0.5f);

            using BlobAssetReference<NavigationGridBlob> gridReference =
                CreateGrid(cells, 3, 3);
            ref NavigationGridBlob grid = ref gridReference.Value;
            Assert(
                !NavigationGridPathAlgorithms.TryCalculateLineCost(
                    ref grid,
                    1 + 1 * 3,
                    2 + 2 * 3,
                    0.35f,
                    0f,
                    0f,
                    out _),
                "直线可见性不能斜穿两个正交阻挡之间的角点");
        }

        private static void TestOpenGridSmoothing()
        {
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

        private static void TestClearanceChangesRoute()
        {
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
                    NavigationGridPathAlgorithms.CanAgentOccupy(
                        ref gridData,
                        execution.PathCells[index],
                        request.AgentRadius,
                        request.ClearanceMargin),
                    "平滑路径不能包含 Clearance 不足 Cell");
            }
        }

        private static void TestTerrainCostAndDeterminism()
        {
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

        private static void TestProjectionFailure()
        {
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

        private static void TestAsynchronousPathfindingSystem()
        {
            // 该用例使用真实 World 和 ISystem 不直接调用内部写回方法
            // 第一次 Update 只能把 Pending 转为 Searching 并调度 Job
            // 后续 Update 在 Handle 完成后才能提交结果
            // 这样可以同时防止意外同步 Complete 和 Buffer 写回遗漏
            NavigationGridCellData[] cells = CreateWalkableCells(8, 8);
            PrepareCells(cells, 8, 8, 0.5f);

            using BlobAssetReference<NavigationGridBlob> grid = CreateGrid(cells, 8, 8);
            using var world = new World("Navigation Grid Stage Two Validation", WorldFlags.Game);
            EntityManager entityManager = world.EntityManager;
            Entity gridEntity = entityManager.CreateEntity(typeof(NavigationGridReference));
            entityManager.SetComponentData(gridEntity, new NavigationGridReference
            {
                Value = grid,
            });

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

            SystemHandle system = world.GetOrCreateSystem<NavigationGridPathfindingSystem>();
            system.Update(world.Unmanaged);
            NavigationPathState pathState =
                entityManager.GetComponentData<NavigationPathState>(requestEntity);
            Assert(
                pathState.Status == NavigationPathStatus.Searching,
                "路径系统首帧只能调度后台任务不能同步写回结果");

            // 主动刷新批处理队列只用于缩短编辑器验收等待 不改变运行时 System 逻辑
            JobHandle.ScheduleBatchedJobs();
            for (int updateIndex = 0;
                 updateIndex < 10000 && pathState.Status == NavigationPathStatus.Searching;
                 updateIndex++)
            {
                Thread.Yield();
                system.Update(world.Unmanaged);
                pathState = entityManager.GetComponentData<NavigationPathState>(requestEntity);
            }

            DynamicBuffer<NavigationPathWaypoint> waypoints =
                entityManager.GetBuffer<NavigationPathWaypoint>(requestEntity);
            Assert(pathState.Status == NavigationPathStatus.Succeeded, "异步路径系统未完成请求");
            Assert(pathState.RequestVersion == request.Version, "异步结果请求版本不匹配");
            Assert(waypoints.Length == 2, "异步路径系统未写回平滑路径");
            Assert(waypoints[0].CellIndex == 0, "异步路径起点写回错误");
            Assert(waypoints[1].CellIndex == 7 + 6 * 8, "异步路径终点写回错误");
        }

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
                heapPositions.Dispose();
                heap.Dispose();
                parents.Dispose();
                gCosts.Dispose();
                pathCells.Dispose();
                results.Dispose();
                requests.Dispose();
            }
        }

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

        private static void PrepareCells(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float maximumStepHeight,
            bool preserveClearance = false)
        {
            NavigationGridAlgorithms.BuildConnectivity(
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

            NavigationGridAlgorithms.AssignClusters(cells, width, height, 4);
            NavigationGridAlgorithms.AssignRegions(cells, width, height);
        }

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

        private static int CountRegions(NavigationGridCellData[] cells)
        {
            int maximumRegion = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                maximumRegion = Mathf.Max(maximumRegion, cells[index].RegionId);
            }

            return maximumRegion;
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
    }
}
#endif
