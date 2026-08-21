using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 执行纯格子 A* 寻路，不依赖 ECS World 或主线程 API，可直接在 Burst 任务中运行
    /// </summary>
    public static class NavigationGridPathfinder
    {
        private const float CostEpsilon = 0.00001f;

        internal static NavigationPathJobResult FindPath(
            ref NavigationGridBlob grid,
            NavigationPathJobRequest jobRequest,
            int generation,
            ref NativeList<int> pathCells,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            NativeArray<int> nodeGenerations)
        {
            return FindPath(
                ref grid,
                jobRequest,
                generation,
                ref pathCells,
                gCosts,
                parents,
                heap,
                heapPositions,
                nodeGenerations,
                default);
        }

        internal static NavigationPathJobResult FindPath(
            ref NavigationGridBlob grid,
            NavigationPathJobRequest jobRequest,
            int generation,
            ref NativeList<int> pathCells,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            NativeArray<int> nodeGenerations,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            NavigationPathRequest request = jobRequest.Request;

            // 先准备完整的失败结果，后续任何提前返回只需填写具体原因
            NavigationPathJobResult result = CreateFailureResult(
                jobRequest.Entity,
                request.Version,
                NavigationPathFailureReason.InvalidRequest);
            int cellCount = grid.Cells.Length;
            if (!NavigationGridTraversal.IsGridShapeValid(ref grid) ||
                generation <= 0 ||
                gCosts.Length < cellCount ||
                parents.Length < cellCount ||
                heap.Length < cellCount ||
                heapPositions.Length < cellCount ||
                nodeGenerations.Length < cellCount ||
                !NavigationGridQuery.IsRequestValid(request))
            {
                return result;
            }

            // 起点和终点分别纠正到可站立格子，便于准确报告是哪一端无效
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    request.StartPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    dynamicOverlay,
                    out int startCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.StartProjectionFailed;
                return result;
            }

            result.ProjectedStartCellIndex = startCellIndex;
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    request.EndPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    dynamicOverlay,
                    out int endCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.EndProjectionFailed;
                return result;
            }

            result.ProjectedEndCellIndex = endCellIndex;
            if (NavigationGridTraversal.IsDynamicCellBlocked(
                    dynamicOverlay, startCellIndex) ||
                NavigationGridTraversal.IsDynamicCellBlocked(
                    dynamicOverlay, endCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }
            // 起终点不在同一个静态连通区域时必然无路，可以在开始 A* 前直接返回
            // RegionId 只反映烘焙地图；动态障碍仍会在实际搜索每一步时检查
            if (grid.Cells[startCellIndex].RegionId <= 0 ||
                grid.Cells[startCellIndex].RegionId != grid.Cells[endCellIndex].RegionId)
            {
                result.FailureReason = NavigationPathFailureReason.RegionMismatch;
                return result;
            }

            // 先记下本条路径在共享输出数组中的起点，批量请求才能各自找到结果
            int pathOffset = pathCells.Length;
            if (startCellIndex == endCellIndex)
            {
                // 起终点在同一格时仍返回一个路径点，下游无需处理“成功但路径为空”
                pathCells.Add(startCellIndex);
                result.Status = NavigationPathStatus.Succeeded;
                result.FailureReason = NavigationPathFailureReason.None;
                result.PathOffset = pathOffset;
                result.PathLength = 1;
                return result;
            }

            // 只在本次搜索首次访问节点时初始化它，避免每次请求清空整张网格的临时数组
            InitializeNode(
                startCellIndex,
                generation,
                gCosts,
                parents,
                heapPositions,
                nodeGenerations);
            gCosts[startCellIndex] = 0f;
            int heapCount = 0;
            NavigationAStarOpenSet.PushHeap(
                startCellIndex,
                endCellIndex,
                ref grid,
                gCosts,
                heap,
                heapPositions,
                ref heapCount);

            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            bool found = false;
            int expandedNodeCount = 0;

            // 每轮展开总成本最低的节点；一致启发函数保证关闭后的节点无需重新打开
            // ExpandedNodeCount 只统计真正从待搜索集合中取出的节点
            while (heapCount > 0)
            {
                int currentIndex = NavigationAStarOpenSet.PopHeap(
                    endCellIndex,
                    ref grid,
                    gCosts,
                    heap,
                    heapPositions,
                    ref heapCount);
                expandedNodeCount++;
                if (currentIndex == endCellIndex)
                {
                    found = true;
                    break;
                }

                int currentX = currentIndex % grid.Width;
                int currentZ = currentIndex / grid.Width;
                byte neighborMask = grid.Cells[currentIndex].NeighborMask;
                // 相邻格子固定从北向开始顺时针检查，相同输入会得到相同访问顺序
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    if ((neighborMask & (1 << directionIndex)) == 0)
                    {
                        continue;
                    }

                    NavigationGridDirections.GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighborX = currentX + deltaX;
                    int neighborZ = currentZ + deltaZ;
                    if (!NavigationGridTraversal.IsInside(neighborX, neighborZ, grid.Width, grid.Height))
                    {
                        continue;
                    }

                    int neighborIndex = neighborX + neighborZ * grid.Width;
                    if (!NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                            ref grid,
                            currentIndex,
                            neighborIndex,
                            deltaX,
                            deltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            dynamicOverlay))
                    {
                        continue;
                    }

                    // generation 不匹配说明数组里还是旧请求的数据，必须先重新初始化
                    bool unseen = nodeGenerations[neighborIndex] != generation;
                    if (unseen)
                    {
                        InitializeNode(
                            neighborIndex,
                            generation,
                            gCosts,
                            parents,
                            heapPositions,
                            nodeGenerations);
                    }
                    else if (heapPositions[neighborIndex] < 0)
                    {
                        // 当前启发函数一致，已关闭的节点无需再次加入搜索
                        continue;
                    }

                    // 新候选成本等于到当前节点的最低成本加上这一步的实际成本
                    float tentativeCost = gCosts[currentIndex] + NavigationGridCost.CalculateStepCost(
                        ref grid,
                        currentIndex,
                        neighborIndex,
                        requiredClearance,
                        request.ClearancePenaltyWeight,
                        dynamicOverlay);
                    tentativeCost += NavigationGridCost.GetDynamicExtraCost(dynamicOverlay, neighborIndex);
                    bool lowerCost = tentativeCost < gCosts[neighborIndex] - CostEpsilon;
                    // 成本相同时选择索引更小的父节点，避免路径受检查先后影响
                    bool stableParent =
                        math.abs(tentativeCost - gCosts[neighborIndex]) <= CostEpsilon &&
                        (parents[neighborIndex] < 0 || currentIndex < parents[neighborIndex]);
                    // 成本没有降低、父节点顺序也没有改善时，保留原来的路径树
                    if (!lowerCost && !stableParent)
                    {
                        continue;
                    }

                    gCosts[neighborIndex] = tentativeCost;
                    parents[neighborIndex] = currentIndex;
                    if (unseen)
                    {
                        NavigationAStarOpenSet.PushHeap(
                            neighborIndex,
                            endCellIndex,
                            ref grid,
                            gCosts,
                            heap,
                            heapPositions,
                            ref heapCount);
                    }
                    else
                    {
                        // 已在堆中的节点只会降低成本，因此向上调整即可
                        NavigationAStarOpenSet.SiftUp(
                            heapPositions[neighborIndex],
                            endCellIndex,
                            ref grid,
                            gCosts,
                            heap,
                            heapPositions);
                    }
                }
            }

            result.ExpandedNodeCount = expandedNodeCount;

            // 搜索成功后复用临时堆数组重建父链；重建失败时不输出残缺路径
            if (!found || !NavigationPathSmoothing.AppendSmoothedPath(
                    ref grid,
                    request,
                    startCellIndex,
                    endCellIndex,
                    ref pathCells,
                    gCosts,
                    parents,
                    heap,
                    dynamicOverlay,
                    out int pathLength))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }

            // 平滑后的路径全部写完后才发布成功结果
            result.Status = NavigationPathStatus.Succeeded;
            result.FailureReason = NavigationPathFailureReason.None;
            result.PathOffset = pathOffset;
            result.PathLength = pathLength;
            result.TotalCost = gCosts[endCellIndex];
            return result;
        }

        internal static NavigationPathJobResult CreateFailureResult(
            Entity entity,
            uint requestVersion,
            NavigationPathFailureReason failureReason)
        {
            // 失败结果会明确初始化所有可见字段，调用方可以直接写回，不会混入上一次请求的数据
            return new NavigationPathJobResult
            {
                Entity = entity,
                RequestVersion = requestVersion,
                Status = NavigationPathStatus.Failed,
                FailureReason = failureReason,
                ProjectedStartCellIndex = -1,
                ProjectedEndCellIndex = -1,
                PathOffset = 0,
                PathLength = 0,
                ExpandedNodeCount = 0,
                TotalCost = 0f,
            };
        }

        private static void InitializeNode(
            int cellIndex,
            int generation,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> heapPositions,
            NativeArray<int> nodeGenerations)
        {
            // Generation 标记这些数组值属于哪次搜索，其余字段重置为“尚未发现”
            nodeGenerations[cellIndex] = generation;
            gCosts[cellIndex] = float.PositiveInfinity;
            parents[cellIndex] = -1;
            heapPositions[cellIndex] = -1;
        }

    }
}
