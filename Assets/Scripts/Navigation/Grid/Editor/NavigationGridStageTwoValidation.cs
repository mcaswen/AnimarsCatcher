#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace AnimarsCatcher.Animars.Navigation.Grid.Editor
{
    /// <summary>
    /// 执行阶段二端点投影、普通 A 星和路径平滑自动验收
    /// </summary>
    public static class NavigationGridStageTwoValidation
    {
        [MenuItem("Tools/Animars Catcher/Navigation/Run Stage Two Validation")]
        // 菜单入口复用与批处理完全相同的测试集合
        // 验收过程只创建临时 World 和 NativeContainer
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
            // 查询契约和失败路径先运行 随后验证成本与异步 System
            // 每项使用独立 Blob 和 NativeContainer 防止状态串扰
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

        // 路径 System 只允许注册到 Server 和 Local Simulation World
        // Client World 不应隐式承担权威寻路工作
        private static void TestWorldFilterRegistration()
        {
            // 读取系统发现列表验证 WorldFilter 在注册阶段已经生效
            IReadOnlyList<Type> serverSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ServerSimulation);
            IReadOnlyList<Type> localSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.LocalSimulation);
            IReadOnlyList<Type> clientSystems = DefaultWorldInitialization.GetAllSystems(
                WorldSystemFilterFlags.ClientSimulation);

            Assert(
                ContainsSystem(serverSystems, typeof(NavigationGridPathfindingSystem)),
                "Server World 必须自动注册 NavigationGridPathfindingSystem");
            Assert(
                ContainsSystem(localSystems, typeof(NavigationGridPathfindingSystem)),
                "Local World 必须自动注册 NavigationGridPathfindingSystem");
            Assert(
                !ContainsSystem(clientSystems, typeof(NavigationGridPathfindingSystem)),
                "Client World 不应自动注册 NavigationGridPathfindingSystem");
        }

        // 系统列表可能包含不同程序集中的同名类型 因而按 Type 身份比较
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

        // 验证世界坐标与行主序 Cell 之间的边界和往返契约
        // 同时覆盖阻挡端点向最近合法 Cell 的稳定投影
        private static void TestCoordinateConversionAndProjection()
        {
            // 使用非零 Bounds 原点确保转换没有世界零点假设
            // 阻挡中心与等距候选同时验证稳定比较键
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

        // 起终点位于不同静态 Region 时应在 A 星展开前失败
        // ExpandedNodeCount 保持零证明预拒绝没有进入搜索热路径
        private static void TestRegionRejection()
        {
            // 完整阻挡列创建两个 Region 且保持两端点各自可站立
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

        // 直线检查必须继承烘焙邻接的禁止穿角规则
        // 路径平滑不能重新引入 A 星已经避开的非法对角边
        private static void TestCornerLineOfSight()
        {
            // 只封锁对角边所需侧格并保留目标 Cell
            // 直线成本必须失败而不是返回绕行成本
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

        // 开放 Grid 上锯齿 Parent 链应收敛为少量直线路径点
        // 起点和终点必须始终显式保留供下游跟随
        private static void TestOpenGridSmoothing()
        {
            // 非轴对齐端点促使原始 A 星产生多个八方向节点
            // 平滑后预期只保留可直连的两个端点
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

        // 相同几何对不同 Agent 半径应选择不同可行通道
        // 该测试证明运行时 Clearance 约束没有被静态 Region 替代
        private static void TestClearanceChangesRoute()
        {
            // 双通道地图让小体型走短路 大体型选择宽路
            // 两次请求共用同一 Blob 证明差异来自运行时半径
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

        // 高地形成本应使搜索选择更长但总代价更低的绕行路径
        // 重复执行必须得到完全相同的 Cell 序列和成本
        private static void TestTerrainCostAndDeterminism()
        {
            // 直线路径保持几何可行但提高中间行成本
            // 连续 generation 验证 Scratch 复用不会改变结果
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

        // 投影半径内没有合法 Cell 时返回明确端点失败原因
        // 失败请求不得向共享路径数组写入残留数据
        private static void TestProjectionFailure()
        {
            // 全部 Cell 阻挡并限制半径确保不存在投影候选
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

        // 临时 World 验证请求从 Pending 到 Searching 再到终态的异步链路
        // 同时覆盖版本匹配 Buffer 写回和 World 销毁时的 NativeContainer 释放
        private static void TestAsynchronousPathfindingSystem()
        {
            // Grid 和请求 Entity 使用正式组件与 Buffer 结构
            // World Dispose 同时验证 System 持久容器回收
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

            // 首帧只允许进入 Searching 不能同步完成并写回
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

            // 终态同时校验状态 版本和端点 Buffer
            DynamicBuffer<NavigationPathWaypoint> waypoints =
                entityManager.GetBuffer<NavigationPathWaypoint>(requestEntity);
            Assert(pathState.Status == NavigationPathStatus.Succeeded, "异步路径系统未完成请求");
            Assert(pathState.RequestVersion == request.Version, "异步结果请求版本不匹配");
            Assert(waypoints.Length == 2, "异步路径系统未写回平滑路径");
            Assert(waypoints[0].CellIndex == 0, "异步路径起点写回错误");
            Assert(waypoints[1].CellIndex == 7 + 6 * 8, "异步路径终点写回错误");
        }

        // 为纯算法用例分配与生产 Job 相同形状的 Scratch 容器
        // 每次执行都独立释放资源避免测试间共享状态掩盖错误
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

        // 创建统一成本和高度的可行走 Grid 作为路径测试基线
        // 特定用例只修改与目标行为有关的 Cell
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

        // 依次生成邻接 Clearance Cluster 和 Region 以模拟正式烘焙拓扑阶段
        // 测试不能手工伪造 NeighborMask 否则可能绕过真实约束
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

        // 将托管 Cell 数组转换为与 SubScene 烘焙结果一致的 Blob 结构
        // Blob 生命周期由调用用例显式释放
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

        // RegionId 从一开始连续编号 最大值即当前静态区域数量
        private static int CountRegions(NavigationGridCellData[] cells)
        {
            int maximumRegion = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                maximumRegion = Mathf.Max(maximumRegion, cells[index].RegionId);
            }

            return maximumRegion;
        }

        // 统一修改行主序 Cell 的可行走状态和地形成本
        // 复制写回避免结构体值修改丢失
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

        // 检查路径是否确实绕开指定高成本或阻挡行
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

        // 确定性要求 Cell 数量和每个位置都完全一致
        // 不使用集合比较因为路径顺序本身属于结果契约
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

        // 抛出异常使菜单 Console 和批处理退出码都能暴露失败原因
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private readonly struct PathExecutionResult
        {
            // Scratch 容量严格等于 Cell 数以暴露容量假设和越界
            // 把值类型结果与已经复制出的路径数组绑定为一个测试返回值
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
