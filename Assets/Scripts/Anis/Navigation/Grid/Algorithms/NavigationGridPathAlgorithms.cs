using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Animars.Navigation.Grid
{
    /// <summary>
    /// 提供不依赖 World 和主线程 API 的确定性 Grid 路径算法
    /// </summary>
    public static partial class NavigationGridPathAlgorithms
    {
        private const float CostEpsilon = 0.00001f;
        private const float MinimumTerrainCost = 0.01f;
        private const float SquareRootTwo = 1.41421356237f;

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
            NavigationPathRequest request = jobRequest.Request;
            // 先构造完整失败结果 后续每个提前返回只覆盖确定的失败原因
            NavigationPathJobResult result = CreateFailureResult(
                jobRequest.Entity,
                request.Version,
                NavigationPathFailureReason.InvalidRequest);
            int cellCount = grid.Cells.Length;
            if (!IsGridShapeValid(ref grid) ||
                generation <= 0 ||
                gCosts.Length < cellCount ||
                parents.Length < cellCount ||
                heap.Length < cellCount ||
                heapPositions.Length < cellCount ||
                nodeGenerations.Length < cellCount ||
                !IsRequestValid(request))
            {
                return result;
            }

            // 起点投影失败时终点尚未参与计算 保持结果字段的阶段性含义
            if (!TryProjectToNearestCell(
                    ref grid,
                    request.StartPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    out int startCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.StartProjectionFailed;
                return result;
            }

            result.ProjectedStartCellIndex = startCellIndex;
            if (!TryProjectToNearestCell(
                    ref grid,
                    request.EndPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    out int endCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.EndProjectionFailed;
                return result;
            }

            result.ProjectedEndCellIndex = endCellIndex;
            // RegionId 只表达静态 Blob 连通性 动态 Overlay 将在后续阶段增加二次判定
            if (grid.Cells[startCellIndex].RegionId <= 0 ||
                grid.Cells[startCellIndex].RegionId != grid.Cells[endCellIndex].RegionId)
            {
                result.FailureReason = NavigationPathFailureReason.RegionMismatch;
                return result;
            }

            int pathOffset = pathCells.Length;
            if (startCellIndex == endCellIndex)
            {
                // 同 Cell 请求仍返回一个路径点 让下游不需要处理空成功路径
                pathCells.Add(startCellIndex);
                result.Status = NavigationPathStatus.Succeeded;
                result.FailureReason = NavigationPathFailureReason.None;
                result.PathOffset = pathOffset;
                result.PathLength = 1;
                return result;
            }

            // 当前 generation 首次触碰节点时才初始化 避免每个请求清空所有 Cell
            // GCosts 保存起点到节点的最小已知成本
            // Parents 保存稳定重建链
            // HeapPositions 非负表示节点仍位于 Open Set
            // NodeGenerations 决定以上三个数组槽位是否属于本次请求
            InitializeNode(
                startCellIndex,
                generation,
                gCosts,
                parents,
                heapPositions,
                nodeGenerations);
            gCosts[startCellIndex] = 0f;
            int heapCount = 0;
            PushHeap(
                startCellIndex,
                endCellIndex,
                ref grid,
                gCosts,
                heap,
                heapPositions,
                ref heapCount);

            float requiredClearance = CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            bool found = false;
            int expandedNodeCount = 0;

            // Pop 后 HeapPosition 被置为负数 同时承担 Closed Set 标记
            // ExpandedNodeCount 只统计真正从 Open Set 展开的节点
            // Region 预拒绝和投影失败不会增加展开数
            // 找到终点后立即结束 不继续扫描其余等价节点
            while (heapCount > 0)
            {
                int currentIndex = PopHeap(
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
                // 固定从北方向开始顺时针展开 相同输入保持稳定访问次序
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    if ((neighborMask & (1 << directionIndex)) == 0)
                    {
                        continue;
                    }

                    GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighborX = currentX + deltaX;
                    int neighborZ = currentZ + deltaZ;
                    if (!IsInside(neighborX, neighborZ, grid.Width, grid.Height))
                    {
                        continue;
                    }

                    int neighborIndex = neighborX + neighborZ * grid.Width;
                    if (!CanAgentTraverseEdge(
                            ref grid,
                            currentIndex,
                            neighborIndex,
                            deltaX,
                            deltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin))
                    {
                        continue;
                    }

                    // generation 不匹配表示数组槽位仍属于更早请求 其中数据不可读取
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

                    float tentativeCost = gCosts[currentIndex] + CalculateStepCost(
                        ref grid,
                        currentIndex,
                        neighborIndex,
                        requiredClearance,
                        request.ClearancePenaltyWeight);
                    bool lowerCost = tentativeCost < gCosts[neighborIndex] - CostEpsilon;
                    // 相同成本使用较小 Parent Index 使重建路径不依赖先后松弛偶然性
                    bool stableParent =
                        math.abs(tentativeCost - gCosts[neighborIndex]) <= CostEpsilon &&
                        (parents[neighborIndex] < 0 || currentIndex < parents[neighborIndex]);
                    if (!lowerCost && !stableParent)
                    {
                        continue;
                    }

                    gCosts[neighborIndex] = tentativeCost;
                    parents[neighborIndex] = currentIndex;
                    if (unseen)
                    {
                        PushHeap(
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
                        SiftUp(
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
            // 搜索完成后 Heap 数组不再承载 Open Set 可以安全复用为反向重建缓冲区
            // 复用只发生在 found 之后
            // 重建失败不会留下部分成功结果
            // PathOffset 始终指向本请求写入前的共享数组长度
            // 失败请求不会向共享 PathCells 追加任何 Cell
            // 后续结果因此不会被前一个失败请求改变切片起点
            if (!found || !AppendSmoothedPath(
                    ref grid,
                    request,
                    startCellIndex,
                    endCellIndex,
                    ref pathCells,
                    gCosts,
                    parents,
                    heap,
                    out int pathLength))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }

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

        private static bool AppendSmoothedPath(
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            int startCellIndex,
            int endCellIndex,
            ref NativeList<int> pathCells,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> reconstruction,
            out int pathLength)
        {
            pathLength = 0;
            int rawPathLength = 0;
            int currentIndex = endCellIndex;
            // Parent 链从终点逆向写入 reconstruction 不额外分配临时 NativeList
            while (currentIndex >= 0 && rawPathLength < reconstruction.Length)
            {
                reconstruction[rawPathLength++] = currentIndex;
                if (currentIndex == startCellIndex)
                {
                    break;
                }

                currentIndex = parents[currentIndex];
            }

            if (rawPathLength <= 0 || reconstruction[rawPathLength - 1] != startCellIndex)
            {
                return false;
            }

            int outputStart = pathCells.Length;
            // 成功路径始终显式保留投影起点
            pathCells.Add(startCellIndex);
            int anchorOrderedIndex = 0;

            // 贪心选择最远可见节点 但直接线段成本不能超过原 A 星路径允许的容差
            // 从最远候选向近处扫描可在保持确定性的同时尽量减少路径点
            // 可见性只解决几何和 Clearance 合法性
            // 成本约束阻止平滑线切过高 Terrain Cost 区域
            // 容差为零时直线不得比原 A 星分段更贵
            // 容差为正时允许用少量成本换取更少路径点
            while (anchorOrderedIndex < rawPathLength - 1)
            {
                int anchorCellIndex = GetOrderedRawPathCell(
                    reconstruction,
                    rawPathLength,
                    anchorOrderedIndex);
                int selectedOrderedIndex = anchorOrderedIndex + 1;
                for (int candidateOrderedIndex = rawPathLength - 1;
                     candidateOrderedIndex > anchorOrderedIndex;
                     candidateOrderedIndex--)
                {
                    int candidateCellIndex = GetOrderedRawPathCell(
                        reconstruction,
                        rawPathLength,
                        candidateOrderedIndex);
                    // A 星 Parent 链上的 G Cost 单调递增 差值就是原路径分段成本
                    float rawSegmentCost = math.max(
                        0f,
                        gCosts[candidateCellIndex] - gCosts[anchorCellIndex]);
                    if (TryCalculateLineCost(
                            ref grid,
                            anchorCellIndex,
                            candidateCellIndex,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            request.ClearancePenaltyWeight,
                            out float directCost) &&
                        directCost <=
                        rawSegmentCost * (1f + request.SmoothingCostTolerance) + CostEpsilon)
                    {
                        selectedOrderedIndex = candidateOrderedIndex;
                        break;
                    }
                }

                pathCells.Add(GetOrderedRawPathCell(
                    reconstruction,
                    rawPathLength,
                    selectedOrderedIndex));
                anchorOrderedIndex = selectedOrderedIndex;
            }

            pathLength = pathCells.Length - outputStart;
            return pathLength > 0;
        }

        private static float CalculateRequiredClearance(
            ref NavigationGridBlob grid,
            float agentRadius,
            float clearanceMargin)
        {
            // 烘焙占用已经包含 BaseAgentRadius 这里只计算运行时增量
            return math.max(0f, agentRadius - grid.BaseAgentRadius) +
                   math.max(0f, clearanceMargin);
        }

        private static float CalculateStepCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight)
        {
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int toX = toCellIndex % grid.Width;
            int toZ = toCellIndex / grid.Width;
            bool diagonal = fromX != toX && fromZ != toZ;
            float distance = grid.CellSize * (diagonal ? SquareRootTwo : 1f);
            // 使用目标 Cell 成本使每条有向边只采样一次 并与 A 星和直线检查保持一致
            NavigationGridCell targetCell = grid.Cells[toCellIndex];
            float extraClearance = math.max(0f, targetCell.Clearance - requiredClearance);
            // 通道越宽比例越接近零 惩罚连续衰减而不会形成新的硬阻挡
            float clearanceRatio = grid.CellSize / (grid.CellSize + extraClearance);
            float weightedTerrainCost =
                math.max(MinimumTerrainCost, targetCell.TerrainCost) +
                math.max(0f, clearancePenaltyWeight) * clearanceRatio;
            return distance * weightedTerrainCost;
        }

        private static bool CanAgentTraverseEdge(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            int deltaX,
            int deltaZ,
            float agentRadius,
            float clearanceMargin)
        {
            // NeighborMask 负责静态可行走和高度规则 CanAgentOccupy 负责当前体型
            if (!TryGetDirectionIndex(deltaX, deltaZ, out int directionIndex) ||
                (grid.Cells[fromCellIndex].NeighborMask & (1 << directionIndex)) == 0 ||
                !CanAgentOccupy(ref grid, toCellIndex, agentRadius, clearanceMargin))
            {
                return false;
            }

            if (deltaX == 0 || deltaZ == 0)
            {
                return true;
            }

            // 大体型对角移动还要占用两个正交侧边 防止从低 Clearance 角点挤过
            int fromX = fromCellIndex % grid.Width;
            int fromZ = fromCellIndex / grid.Width;
            int sideXCellIndex = fromX + deltaX + fromZ * grid.Width;
            int sideZCellIndex = fromX + (fromZ + deltaZ) * grid.Width;
            return CanAgentOccupy(
                       ref grid,
                       sideXCellIndex,
                       agentRadius,
                       clearanceMargin) &&
                   CanAgentOccupy(
                       ref grid,
                       sideZCellIndex,
                       agentRadius,
                       clearanceMargin);
        }

        private static bool IsBetterProjectionCandidate(
            float distanceSquared,
            float terrainCost,
            float clearance,
            int cellIndex,
            float bestDistanceSquared,
            float bestTerrainCost,
            float bestClearance,
            int bestCellIndex)
        {
            // 比较顺序必须与投影契约一致 距离优先于地形和 Clearance
            if (bestCellIndex < 0 || distanceSquared < bestDistanceSquared - CostEpsilon)
            {
                return true;
            }

            if (math.abs(distanceSquared - bestDistanceSquared) > CostEpsilon)
            {
                return false;
            }

            if (terrainCost < bestTerrainCost - CostEpsilon)
            {
                return true;
            }

            if (math.abs(terrainCost - bestTerrainCost) > CostEpsilon)
            {
                return false;
            }

            if (clearance > bestClearance + CostEpsilon)
            {
                return true;
            }

            return math.abs(clearance - bestClearance) <= CostEpsilon &&
                   cellIndex < bestCellIndex;
        }

        private static bool IsRequestValid(NavigationPathRequest request)
        {
            return math.all(math.isfinite(request.StartPosition)) &&
                   math.all(math.isfinite(request.EndPosition)) &&
                   math.isfinite(request.AgentRadius) &&
                   math.isfinite(request.ClearanceMargin) &&
                   math.isfinite(request.ClearancePenaltyWeight) &&
                   math.isfinite(request.SmoothingCostTolerance) &&
                   request.AgentRadius >= 0f &&
                   request.ClearanceMargin >= 0f &&
                   request.ClearancePenaltyWeight >= 0f &&
                   request.SmoothingCostTolerance >= 0f &&
                   request.MaximumProjectionRadiusInCells >= 0;
        }

        private static bool IsGridShapeValid(ref NavigationGridBlob grid)
        {
            return grid.Width > 0 &&
                   grid.Height > 0 &&
                   grid.CellSize > 0f &&
                   grid.Cells.Length == grid.Width * grid.Height;
        }

        private static void InitializeNode(
            int cellIndex,
            int generation,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> heapPositions,
            NativeArray<int> nodeGenerations)
        {
            // 初始化顺序先写 generation 后续读取方才能把其余槽位视为当前请求数据
            nodeGenerations[cellIndex] = generation;
            gCosts[cellIndex] = float.PositiveInfinity;
            parents[cellIndex] = -1;
            heapPositions[cellIndex] = -1;
        }

        private static int GetOrderedRawPathCell(
            NativeArray<int> reconstruction,
            int rawPathLength,
            int orderedIndex)
        {
            // reconstruction 逆序保存 Parent 链 此方法暴露起点到终点的正序视图
            return reconstruction[rawPathLength - 1 - orderedIndex];
        }

        private static bool TryGetDirectionIndex(
            int deltaX,
            int deltaZ,
            out int directionIndex)
        {
            directionIndex = -1;
            if (deltaX == 0 && deltaZ == 1) directionIndex = 0;
            else if (deltaX == 1 && deltaZ == 1) directionIndex = 1;
            else if (deltaX == 1 && deltaZ == 0) directionIndex = 2;
            else if (deltaX == 1 && deltaZ == -1) directionIndex = 3;
            else if (deltaX == 0 && deltaZ == -1) directionIndex = 4;
            else if (deltaX == -1 && deltaZ == -1) directionIndex = 5;
            else if (deltaX == -1 && deltaZ == 0) directionIndex = 6;
            else if (deltaX == -1 && deltaZ == 1) directionIndex = 7;
            return directionIndex >= 0;
        }

        private static void GetDirection(int directionIndex, out int deltaX, out int deltaZ)
        {
            switch (directionIndex)
            {
                case 0: deltaX = 0; deltaZ = 1; return;
                case 1: deltaX = 1; deltaZ = 1; return;
                case 2: deltaX = 1; deltaZ = 0; return;
                case 3: deltaX = 1; deltaZ = -1; return;
                case 4: deltaX = 0; deltaZ = -1; return;
                case 5: deltaX = -1; deltaZ = -1; return;
                case 6: deltaX = -1; deltaZ = 0; return;
                default: deltaX = -1; deltaZ = 1; return;
            }
        }

        private static bool IsInside(int x, int z, int width, int height)
        {
            return x >= 0 && x < width && z >= 0 && z < height;
        }
    }
}
