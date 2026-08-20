using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供不依赖 World 和主线程 API 的确定性 Grid 路径算法
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

            // 先构造完整失败结果，提前返回只需覆盖已确定的原因
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

            // 端点分别投影到合法节点，让上层能区分两端的输入问题
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
            // 静态 Region 不同必然无路，可以在分配 Open Set 前立即拒绝
            // RegionId 只表达静态 Blob 连通性，动态 Overlay 将在后续阶段增加二次判定
            if (grid.Cells[startCellIndex].RegionId <= 0 ||
                grid.Cells[startCellIndex].RegionId != grid.Cells[endCellIndex].RegionId)
            {
                result.FailureReason = NavigationPathFailureReason.RegionMismatch;
                return result;
            }

            // 记录共享输出数组的起始偏移后才能安全服务批量请求
            int pathOffset = pathCells.Length;
            if (startCellIndex == endCellIndex)
            {
                // 同 Cell 请求仍返回一个路径点，让下游不需要处理空成功路径
                pathCells.Add(startCellIndex);
                result.Status = NavigationPathStatus.Succeeded;
                result.FailureReason = NavigationPathFailureReason.None;
                result.PathOffset = pathOffset;
                result.PathLength = 1;
                return result;
            }

            // 首次触碰节点时按 generation 初始化，避免每次请求清空整张 Grid
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

            // 每轮展开最小 F Cost 节点，一致启发函数保证 Closed 节点无需重开
            // ExpandedNodeCount 只统计真正从 Open Set 弹出的节点
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
                // 固定从北方向开始顺时针展开，相同输入保持稳定访问次序
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

                    // generation 不匹配表示数组槽位仍属于更早请求，其中数据不可读取
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
                        // 可采纳且一致的启发函数下 Closed 节点不需要重新打开
                        continue;
                    }

                    // 松弛候选成本使用当前最优 G Cost 加单边真实成本
                    float tentativeCost = gCosts[currentIndex] + NavigationGridCost.CalculateStepCost(
                        ref grid,
                        currentIndex,
                        neighborIndex,
                        requiredClearance,
                        request.ClearancePenaltyWeight,
                        dynamicOverlay);
                    tentativeCost += NavigationGridCost.GetDynamicExtraCost(dynamicOverlay, neighborIndex);
                    bool lowerCost = tentativeCost < gCosts[neighborIndex] - CostEpsilon;
                    // 相同成本使用较小 Parent Index 使重建路径不依赖先后松弛偶然性
                    bool stableParent =
                        math.abs(tentativeCost - gCosts[neighborIndex]) <= CostEpsilon &&
                        (parents[neighborIndex] < 0 || currentIndex < parents[neighborIndex]);
                    // 成本没有改善且稳定 Parent 也不更优时保持原路径树
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
                        // 节点只会降低 G Cost 因此现有 Heap 节点只需向上修复
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

            // 搜索结束后复用 Heap 重建父链，失败时不向共享路径追加部分结果
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

            // 成功结果只在平滑路径完整写入后一次性发布
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
            // 失败结果统一初始化所有可观测字段
            // 调用方可以直接写回而不依赖默认值残留或前一请求状态
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
            // Generation 标记槽位归属，其余值恢复为未发现节点状态
            nodeGenerations[cellIndex] = generation;
            gCosts[cellIndex] = float.PositiveInfinity;
            parents[cellIndex] = -1;
            heapPositions[cellIndex] = -1;
        }

    }
}
