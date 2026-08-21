using AnimarsCatcher.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 从目标反向计算通道内每个格子的最低剩余成本和可行前进方向
    /// </summary>
    public static class NavigationIntegrationFieldSolver
    {
        private const float CostEpsilon = 0.00001f;

        // Integration Field 从目标反向扩散，使用与 A* 相同的单步成本
        // 分块 Generation 将计算范围严格限制在当前宏观通道中
        // 动态障碍会同时影响格子阻挡、附加成本和最终方向
        // 平滑后的连续方向不能直接输出，最终仍要选中一条已经验证可走的相邻边
        // 成本相同时使用格子索引决定顺序，保证结果可重复
        // IntegrationCost 表示从当前格子走到目标还需要的最低成本
        // 正确的下一格必须满足：下一格剩余成本加这一步成本，等于当前最低成本
        // 只看剩余成本下降可能选到总成本更高的绕路，因此必须检查完整等式
        // 动态附加成本与反向 Dijkstra 使用同一口径，计入正向移动所进入的格子
        // 第一遍找出最低成本和一个可靠的备用下一格
        // 第二遍只混合成本同样最优的方向，用于减少格子感
        // 第三遍把混合方向映射回最接近的真实最优相邻格
        // 每一遍都只检查八个邻居，因此每格的方向计算仍是固定开销
        // 对称方向可能完全抵消；这种零向量不代表已经到达目标
        // 抵消时选择索引最小的备用下一格，保证 Burst 重复运行结果一致
        // 只有目标格子或数据不完整的异常格子才允许最终方向为零
        // 移动系统遇到零方向必须停下，不能擅自穿过障碍

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
            // 用本次 Generation 标出通道包含的分块，反向搜索不会走出这个范围
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                clusterGenerations[corridorClusters[index]] = generation;
            }

            // 通道已经去重；即使重复标记同一分块，结果也不会变化
            visitedCells.Clear();

            int heapCount = 0;
            // 从目标反向执行 Dijkstra，让每个格子的值表示到目标的最低成本
            InitializeCell(targetCellIndex, generation, costs, heapPositions, generations);
            // 纠正后的目标格子是反向搜索唯一的零成本起点
            costs[targetCellIndex] = 0f;
            PushCell(targetCellIndex, costs, heap, heapPositions, ref heapCount);
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            // 只记录已经确定最低成本的格子，输出时无需扫描整张导航网格
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
                    // 前驱格子必须在通道内，而且从前驱走到当前格要满足正常移动规则
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

                    // 前驱格子在本次计算中首次出现时再初始化临时数据
                    if (generations[predecessor] != generation)
                    {
                        InitializeCell(predecessor, generation, costs, heapPositions, generations);
                    }

                    // 虽然搜索方向反向，单步成本仍按正向从前驱进入当前格来计算
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

                    // 找到更低成本后，把前驱格子重新放入待处理堆
                    costs[predecessor] = candidate;
                    PushCell(predecessor, costs, heap, heapPositions, ref heapCount);
                }
            }

            // 已确定最低成本的格子数就是这次局部场实际展开量
            expandedCellCount = visitedCells.Length;

            // 如果反向搜索到不了起点，说明宏观通道无法落实为实际格子路线
            if (generations[startCellIndex] != generation)
            {
                return false;
            }

            // 输出是稀疏列表且顺序取决于 Dijkstra，使用方应按 CellIndex 查找
            for (int index = 0; index < visitedCells.Length; index++)
            {
                int cellIndex = visitedCells[index];
                output.Add(new NavigationFlowFieldCell
                {
                    CellIndex = cellIndex,
                    IntegrationCost = costs[cellIndex],
                    // 每个非目标格子都必须得到一条合法且保持最低总成本的下一步方向
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
                // 只有目标格子可以用零方向表示已经到达
                return float2.zero;
            }

            int cellX = cellIndex % grid.Width;
            int cellZ = cellIndex / grid.Width;
            float currentCost = costs[cellIndex];
            float2 smoothedDirection = float2.zero;
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            float bestBellmanCost = float.PositiveInfinity;
            int fallbackNeighbor = -1;
            float2 fallbackDirection = float2.zero;

            // 先找出满足 Bellman 最优条件的下一格；剩余成本下降并不一定代表整条路线最优
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
                // Generation 同时确认邻居属于当前通道，且它的成本来自本次计算
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

                float successorCost = costs[neighbor] +
                                      NavigationGridCost.CalculateStepCost(
                                          ref grid,
                                          cellIndex,
                                          neighbor,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight,
                                          dynamicOverlay) +
                                      NavigationGridCost.GetDynamicExtraCost(
                                          dynamicOverlay,
                                          neighbor);
                float2 direction = math.normalizesafe(new float2(deltaX, deltaZ));
                if (successorCost < bestBellmanCost - CostEpsilon ||
                    (math.abs(successorCost - bestBellmanCost) <= CostEpsilon &&
                     (fallbackNeighbor < 0 || neighbor < fallbackNeighbor)))
                {
                    bestBellmanCost = successorCost;
                    fallbackNeighbor = neighbor;
                    fallbackDirection = direction;
                }
            }

            // 非目标格找不到合法下一步说明 Flow Field 不完整，保留零方向以便验证立即发现问题
            if (fallbackNeighbor < 0)
            {
                return float2.zero;
            }

            // 只混合同样达到最低总成本的下一格，避免平滑后走上更贵的路线
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

                float successorCost = costs[neighbor] +
                                      NavigationGridCost.CalculateStepCost(
                                          ref grid,
                                          cellIndex,
                                          neighbor,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight,
                                          dynamicOverlay) +
                                      NavigationGridCost.GetDynamicExtraCost(
                                          dynamicOverlay,
                                          neighbor);
                if (successorCost > bestBellmanCost + CostEpsilon)
                {
                    continue;
                }

                // 在总成本同样最优的方向中，剩余成本下降更多的方向权重更大
                smoothedDirection += direction * (currentCost - costs[neighbor]);
            }

            // 多个对称方向可能互相抵消，此时改用第一遍选出的可靠备用方向
            if (math.lengthsq(smoothedDirection) <= CostEpsilon)
            {
                return fallbackDirection;
            }

            // 混合向量只用于选择，最终输出仍对应一条已经验证的最优相邻边
            smoothedDirection = math.normalize(smoothedDirection);
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
                if (generations[neighbor] != generation ||
                    costs[neighbor] >= currentCost - CostEpsilon ||
                    NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, neighbor))
                {
                    continue;
                }

                float2 direction = math.normalizesafe(new float2(deltaX, deltaZ));
                float successorCost = costs[neighbor] +
                                      NavigationGridCost.CalculateStepCost(
                                          ref grid,
                                          cellIndex,
                                          neighbor,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight,
                                          dynamicOverlay) +
                                      NavigationGridCost.GetDynamicExtraCost(
                                          dynamicOverlay,
                                          neighbor);
                float alignment = math.dot(direction, smoothedDirection);
                // 与混合方向同样接近时，选择索引更小的格子以保持结果一致
                if (successorCost > bestBellmanCost + CostEpsilon ||
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

            // 浮点边界导致第二遍找不到候选时，回退到第一遍已经确认的最优下一格
            return bestNeighbor >= 0 ? bestDirection : fallbackDirection;
        }

        internal static bool TryGetIntegrationCost(
            ref NativeList<NavigationFlowFieldCell> cells,
            int offset,
            int count,
            int cellIndex,
            out float cost)
        {
            // 稀疏 Flow Field 没有单独索引表，偶发回退查询直接扫描当前切片
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
            // 最后写 Generation，确保标记生效时，成本和堆位置也已属于本次计算
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
