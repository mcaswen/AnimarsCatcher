using AnimarsCatcher.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 构建 Corridor 内的 Integration Cost 和已验证 Flow Direction
    /// </summary>
    public static class NavigationIntegrationFieldSolver
    {
        private const float CostEpsilon = 0.00001f;

        // Integration 从目标反向扩散，成本必须与 A 星步进成本保持同一尺度
        // Cluster Generation 将搜索严格限制在当前 Corridor 内
        // 动态 Overlay 同时影响阻挡、额外成本和最终方向选择
        // 连续平滑方向不能直接输出，最终必须落回一条已验证的离散边
        // Cell 索引用于所有成本相同情况下的稳定决胜

        internal static bool BuildIntegrationField(
            ref NavigationGridBlob grid,
            int startCellIndex,
            int targetCellIndex,
            NavigationPathRequest request,
            int generation,
            ref NativeList<int> corridorClusters,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            NativeArray<int> generations,
            NativeArray<int> clusterGenerations,
            ref NativeList<int> visitedCells,
            ref NativeList<NavigationFlowFieldCell> output,
            out int expandedCellCount,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            // 用 Generation 将 Corridor Cluster 标成反向传播的允许集合
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                clusterGenerations[corridorClusters[index]] = generation;
            }

            // Corridor 已去重，同一 Cluster 重复写入当前 Generation 也保持幂等
            visitedCells.Clear();

            int heapCount = 0;
            // 从目标反向执行 Dijkstra，使成本表示当前 Cell 到目标的最小代价
            InitializeCell(targetCellIndex, generation, costs, heapPositions, generations);
            // 投影目标是反向 Integration 搜索的唯一零成本 Cell
            costs[targetCellIndex] = 0f;
            PushCell(targetCellIndex, costs, heap, heapPositions, ref heapCount);
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            // 只记录已经确定最短成本的 Cell，写出时无需再扫描整张 Grid
            while (heapCount > 0)
            {
                int current = PopCell(costs, heap, heapPositions, ref heapCount);
                visitedCells.Add(current);
                int currentX = current % grid.Width;
                int currentZ = current / grid.Width;
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    NavigationGridDirections.GetDirection(
                        directionIndex,
                        out int deltaX,
                        out int deltaZ);
                    int predecessorX = currentX + deltaX;
                    int predecessorZ = currentZ + deltaZ;
                    if (!NavigationGridTraversal.IsInside(
                            predecessorX,
                            predecessorZ,
                            grid.Width,
                            grid.Height))
                    {
                        continue;
                    }

                    int predecessor = predecessorX + predecessorZ * grid.Width;
                    if (NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, predecessor) ||
                        NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, current))
                    {
                        continue;
                    }
                    int predecessorCluster = grid.Cells[predecessor].ClusterId;
                    // 前驱必须位于 Corridor，并满足与正向移动相同的边约束
                    if (predecessorCluster < 0 ||
                        predecessorCluster >= clusterGenerations.Length ||
                        clusterGenerations[predecessorCluster] != generation ||
                        !NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                            ref grid,
                            predecessor,
                            current,
                            -deltaX,
                            -deltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            dynamicOverlay))
                    {
                        continue;
                    }

                    // 前驱首次进入本次 Field 时延迟初始化 Scratch
                    if (generations[predecessor] != generation)
                    {
                        InitializeCell(predecessor, generation, costs, heapPositions, generations);
                    }

                    // 反向松弛仍按正向 predecessor 到 current 的移动成本计算
                    float candidate = costs[current] +
                                      NavigationGridCost.CalculateStepCost(
                                          ref grid,
                                          predecessor,
                                          current,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight,
                                          dynamicOverlay) +
                                      NavigationGridCost.GetDynamicExtraCost(dynamicOverlay, current);
                    if (candidate >= costs[predecessor] - CostEpsilon)
                    {
                        continue;
                    }

                    // 严格改善后将前驱重新放入开放堆
                    costs[predecessor] = candidate;
                    PushCell(predecessor, costs, heap, heapPositions, ref heapCount);
                }
            }

            // 确定成本的 Cell 数直接作为局部场展开量
            expandedCellCount = visitedCells.Length;

            // 起点未被反向传播访问时，抽象 Corridor 不能形成实际 Cell 路径
            if (generations[startCellIndex] != generation)
            {
                return false;
            }

            // 输出顺序来自 Dijkstra 确定顺序，消费者应通过 CellIndex 查询稀疏 Field
            for (int index = 0; index < visitedCells.Length; index++)
            {
                int cellIndex = visitedCells[index];
                output.Add(new NavigationFlowFieldCell
                {
                    CellIndex = cellIndex,
                    IntegrationCost = costs[cellIndex],
                    // 每个非目标 Cell 都选择一个成本严格下降的合法方向
                    Direction = NavigationIntegrationFieldSolver.CalculateDirection(
                        ref grid,
                        cellIndex,
                        targetCellIndex,
                        request,
                        generation,
                        costs,
                        generations,
                        dynamicOverlay),
                });
            }

            return true;
        }

        internal static float2 CalculateDirection(
            ref NavigationGridBlob grid,
            int cellIndex,
            int targetCellIndex,
            NavigationPathRequest request,
            int generation,
            NativeArray<float> costs,
            NativeArray<int> generations,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            if (cellIndex == targetCellIndex)
            {
                // 目标 Cell 是局部场唯一允许的零方向终点
                return float2.zero;
            }

            int cellX = cellIndex % grid.Width;
            int cellZ = cellIndex / grid.Width;
            float currentCost = costs[cellIndex];
            float2 smoothedDirection = float2.zero;

            // 先混合所有合法下降邻居，得到更平滑的期望方向
            for (int directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                NavigationGridDirections.GetDirection(
                    directionIndex,
                    out int deltaX,
                    out int deltaZ);
                int neighborX = cellX + deltaX;
                int neighborZ = cellZ + deltaZ;
                if (!NavigationGridTraversal.IsInside(
                        neighborX,
                        neighborZ,
                        grid.Width,
                        grid.Height))
                {
                    continue;
                }

                int neighbor = neighborX + neighborZ * grid.Width;
                if (NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, neighbor))
                {
                    continue;
                }
                // 当前 Generation 同时限定 Corridor Field 成员和本次成本有效性
                if (generations[neighbor] != generation ||
                    costs[neighbor] >= currentCost - CostEpsilon ||
                    !NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                        ref grid,
                        cellIndex,
                        neighbor,
                        deltaX,
                        deltaZ,
                        request.AgentRadius,
                        request.ClearanceMargin,
                        dynamicOverlay))
                {
                    continue;
                }

                // 成本下降越大的邻居对平滑方向影响越强
                float costDrop = currentCost - costs[neighbor];
                smoothedDirection += math.normalizesafe(new float2(deltaX, deltaZ)) * costDrop;
            }

            // 没有合法下降邻居时返回零，验收会将非目标零方向视为失败
            if (math.lengthsq(smoothedDirection) <= CostEpsilon)
            {
                return float2.zero;
            }

            // 连续混合方向不能直接输出，否则可能穿过未验证的离散边
            smoothedDirection = math.normalize(smoothedDirection);

            // 从合法下降邻居中选择最接近平滑方向的一条离散边
            int bestNeighbor = -1;
            float bestAlignment = float.NegativeInfinity;
            float2 bestDirection = float2.zero;
            for (int directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                NavigationGridDirections.GetDirection(
                    directionIndex,
                    out int deltaX,
                    out int deltaZ);
                int neighborX = cellX + deltaX;
                int neighborZ = cellZ + deltaZ;
                if (!NavigationGridTraversal.IsInside(
                        neighborX,
                        neighborZ,
                        grid.Width,
                        grid.Height))
                {
                    continue;
                }

                int neighbor = neighborX + neighborZ * grid.Width;
                if (NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, neighbor))
                {
                    continue;
                }
                float2 direction = math.normalizesafe(new float2(deltaX, deltaZ));
                float alignment = math.dot(direction, smoothedDirection);
                // 对齐度相同时选择更小 Cell 索引，保证跨运行的确定性
                if (generations[neighbor] != generation ||
                    costs[neighbor] >= currentCost - CostEpsilon ||
                    !NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                        ref grid,
                        cellIndex,
                        neighbor,
                        deltaX,
                        deltaZ,
                        request.AgentRadius,
                        request.ClearanceMargin,
                        dynamicOverlay) ||
                    (alignment < bestAlignment + CostEpsilon &&
                     !(math.abs(alignment - bestAlignment) <= CostEpsilon &&
                       (bestNeighbor < 0 || neighbor < bestNeighbor))))
                {
                    continue;
                }

                bestNeighbor = neighbor;
                bestAlignment = alignment;
                bestDirection = direction;
            }

            // 返回已验证的单位八方向，移动层不会跨越未验证边
            return bestDirection;
        }

        internal static bool TryGetIntegrationCost(
            ref NativeList<NavigationFlowFieldCell> cells,
            int offset,
            int count,
            int cellIndex,
            out float cost)
        {
            // 稀疏 Field 没有索引表，单次回退查询顺序扫描当前切片
            for (int index = 0; index < count; index++)
            {
                NavigationFlowFieldCell cell = cells[offset + index];
                if (cell.CellIndex == cellIndex)
                {
                    cost = cell.IntegrationCost;
                    return true;
                }
            }

            cost = 0f;
            return false;
        }

        private static void InitializeCell(
            int cellIndex,
            int generation,
            NativeArray<float> costs,
            NativeArray<int> heapPositions,
            NativeArray<int> generations)
        {
            // 初始化顺序保证 Generation 可见时其成本和堆位置也属于本次搜索
            generations[cellIndex] = generation;
            costs[cellIndex] = float.PositiveInfinity;
            heapPositions[cellIndex] = -1;
        }

        private static void PushCell(
            int cellIndex,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            IndexedFloatHeap.PushMin(cellIndex, costs, heap, positions, ref count, CostEpsilon);
        }

        private static int PopCell(
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            return IndexedFloatHeap.PopMin(
                costs,
                heap,
                positions,
                ref count,
                CostEpsilon,
                -2);
        }

    }
}
