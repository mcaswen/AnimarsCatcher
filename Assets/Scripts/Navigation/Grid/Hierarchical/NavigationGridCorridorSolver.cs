using AnimarsCatcher.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 求解确定性的 HPA Corridor 及其局部 Portal 成本
    /// </summary>
    public static class NavigationGridCorridorSolver
    {
        private const float CostEpsilon = 0.00001f;

        // 起点和终点作为虚拟节点接入各自 Cluster 的 Portal 图
        // Portal 初值来自真实局部搜索，不能用穿越障碍的直线距离替代
        // 抽象边仍按 MinimumClearance 过滤，保证大体型请求不会穿过窄门
        // 相同成本使用稳定节点索引决胜，避免不同运行得到不同 Corridor
        // Solver 只写 Native 容器，不读取 World 或管理 ECS 请求生命周期

        internal static bool TryBuildAbstractCorridor(
            ref NavigationGridBlob grid,
            int startCellIndex,
            int endCellIndex,
            int startCluster,
            int endCluster,
            float requiredClearance,
            NavigationPathRequest request,
            int generationStart,
            NativeArray<float> cellCosts,
            NativeArray<int> cellHeap,
            NativeArray<int> cellHeapPositions,
            NativeArray<int> cellGenerations,
            NativeArray<float> abstractCosts,
            NativeArray<float> abstractEndCosts,
            NativeArray<int> abstractParents,
            NativeArray<int> abstractHeap,
            NativeArray<int> abstractHeapPositions,
            NativeArray<int> abstractGenerations,
            ref NativeList<int> corridorClusters,
            ref NativeList<int> corridorPortals,
            ref NativeList<int> nodeChain,
            out int expandedNodeCount,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            expandedNodeCount = 0;
            int abstractGeneration = generationStart;
            int startCostGeneration = generationStart;
            // 在起点 Cluster 内计算真实局部成本，避免用直线距离穿过障碍
            RunLocalCosts(
                ref grid,
                startCellIndex,
                startCluster,
                request,
                false,
                startCostGeneration,
                cellCosts,
                    cellHeap,
                    cellHeapPositions,
                    cellGenerations,
                    dynamicOverlay);

            int abstractHeapCount = 0;
            NavigationGridCluster startClusterData = grid.Clusters[startCluster];
            // 可达 Portal 的局部成本作为抽象搜索的多源初值
            for (int index = 0; index < startClusterData.PortalNodeCount; index++)
            {
                int nodeIndex = grid.ClusterPortalNodeIndices[
                    startClusterData.PortalNodeOffset + index];
                int nodeCell = grid.PortalNodes[nodeIndex].CellIndex;
                if (cellGenerations[nodeCell] != startCostGeneration)
                {
                    continue;
                }

                // 首次触达的 Portal Node 初始化为当前抽象 Generation
                InitializeAbstractNode(
                    nodeIndex,
                    abstractGeneration,
                    abstractCosts,
                    abstractParents,
                    abstractHeapPositions,
                    abstractGenerations);
                // 虚拟起点到 Portal 的局部成本成为多源 Dijkstra 初值
                abstractCosts[nodeIndex] = cellCosts[nodeCell];
                PushAbstract(
                    nodeIndex,
                    abstractCosts,
                    abstractHeap,
                    abstractHeapPositions,
                    ref abstractHeapCount);
            }

            // 起点无法到达任何 Portal 时，抽象图不存在合法入口
            if (abstractHeapCount == 0)
            {
                return false;
            }

            int endCostGeneration = generationStart + 1;

            // 沿反向边计算终点 Cluster 内各 Portal 到虚拟终点的成本
            RunLocalCosts(
                ref grid,
                endCellIndex,
                endCluster,
                request,
                true,
                endCostGeneration,
                cellCosts,
                    cellHeap,
                    cellHeapPositions,
                    cellGenerations,
                    dynamicOverlay);
            // 反向成本保留进入目标 Cell 的地形语义，不能交换起终点复用正向结果
            NavigationGridCluster endClusterData = grid.Clusters[endCluster];
            for (int index = 0; index < endClusterData.PortalNodeCount; index++)
            {
                int nodeIndex = grid.ClusterPortalNodeIndices[
                    endClusterData.PortalNodeOffset + index];
                int nodeCell = grid.PortalNodes[nodeIndex].CellIndex;
                // 不可达 Portal 保持正无穷，抽象搜索不会把它当作有效出口
                abstractEndCosts[nodeIndex] = cellGenerations[nodeCell] == endCostGeneration
                    ? cellCosts[nodeCell]
                    : float.PositiveInfinity;
            }

            float bestTotalCost = float.PositiveInfinity;
            int bestGoalNode = -1;

            // 静态边已包含 Cluster 内最短成本，抽象图直接使用 Dijkstra
            while (abstractHeapCount > 0)
            {
                int current = PopAbstract(
                    abstractCosts,
                    abstractHeap,
                    abstractHeapPositions,
                    ref abstractHeapCount);
                // 后续节点成本只会更高，不能改善当前最优出口时即可停止
                if (abstractCosts[current] >= bestTotalCost - CostEpsilon)
                {
                    break;
                }

                expandedNodeCount++;
                NavigationGridPortalNode currentNode = grid.PortalNodes[current];
                // 进入目标 Cluster 的节点可与预计算的终点侧局部成本拼接
                if (currentNode.ClusterId == endCluster &&
                    !float.IsPositiveInfinity(abstractEndCosts[current]))
                {
                    float candidateTotal = abstractCosts[current] + abstractEndCosts[current];
                    if (candidateTotal < bestTotalCost - CostEpsilon ||
                        (math.abs(candidateTotal - bestTotalCost) <= CostEpsilon &&
                         current < bestGoalNode))
                    {
                        bestTotalCost = candidateTotal;
                        bestGoalNode = current;
                    }
                }

                for (int edgeIndex = 0; edgeIndex < currentNode.EdgeCount; edgeIndex++)
                {
                    NavigationGridAbstractEdge edge =
                        grid.AbstractEdges[currentNode.EdgeOffset + edgeIndex];
                    // Clearance 是硬约束，不足的抽象边不能进入候选路线
                    if (edge.MinimumClearance + CostEpsilon < requiredClearance)
                    {
                        continue;
                    }

                    int target = edge.ToNodeIndex;
                    int targetCell = grid.PortalNodes[target].CellIndex;
                    if (!NavigationGridTraversal.CanAgentOccupyDynamic(
                            ref grid,
                            targetCell,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            dynamicOverlay))
                    {
                        continue;
                    }
                    // 抽象 Scratch 延迟初始化，避免每个请求清空全部 Portal Node
                    if (abstractGenerations[target] != abstractGeneration)
                    {
                        InitializeAbstractNode(
                            target,
                            abstractGeneration,
                            abstractCosts,
                            abstractParents,
                            abstractHeapPositions,
                            abstractGenerations);
                    }

                    // 负二表示节点成本已经确定，不重新打开关闭节点
                    if (abstractHeapPositions[target] == -2)
                    {
                        continue;
                    }

                    // 额外 Clearance 只形成软惩罚，不改变已经通过的可达性判断
                    float extraClearance = math.max(0f, edge.MinimumClearance - requiredClearance);
                    float clearancePenalty = math.max(0f, request.ClearancePenaltyWeight) *
                                             grid.CellSize /
                                             (grid.CellSize + extraClearance);
                    float candidateCost = abstractCosts[current] +
                                          math.max(0f, edge.StaticCost) +
                                          clearancePenalty * grid.CellSize;
                    // 稳定排序的出边配合严格改善判断，保持等价路线的确定性
                    if (candidateCost >= abstractCosts[target] - CostEpsilon)
                    {
                        continue;
                    }

                    // 只记录严格更低的父链，等价路线保留先到达结果
                    abstractCosts[target] = candidateCost;
                    abstractParents[target] = current;
                    PushAbstract(
                        target,
                        abstractCosts,
                        abstractHeap,
                        abstractHeapPositions,
                        ref abstractHeapCount);
                }
            }

            if (bestGoalNode < 0)
            {
                return false;
            }

            nodeChain.Clear();

            int node = bestGoalNode;
            // Parent 链先按终点到起点保存，避免在 NativeList 头部插入
            while (node >= 0)
            {
                nodeChain.Add(node);
                node = abstractParents[node];
            }

            corridorClusters.Clear();
            corridorPortals.Clear();
            corridorClusters.Add(startCluster);
            int currentCluster = startCluster;
            // 逆序读取父链，只在真正跨 Cluster 时追加 Portal 和新 Cluster
            for (int index = nodeChain.Length - 1; index >= 0; index--)
            {
                NavigationGridPortalNode pathNode = grid.PortalNodes[nodeChain[index]];
                // Cluster 内连续 Portal Node 不会推进 Corridor
                if (pathNode.ClusterId == currentCluster)
                {
                    continue;
                }

                // 跨 Cluster 的节点同时确定经过的 Portal
                corridorPortals.Add(pathNode.PortalIndex);
                corridorClusters.Add(pathNode.ClusterId);
                currentCluster = pathNode.ClusterId;
            }

            // 未落到目标 Cluster 表示父链引用了不完整的抽象拓扑
            return currentCluster == endCluster;
        }

        private static void RunLocalCosts(
            ref NavigationGridBlob grid,
            int sourceCellIndex,
            int clusterId,
            NavigationPathRequest request,
            bool reverseEdges,
            int generation,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            NativeArray<int> generations,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            int heapCount = 0;
            // Generation 标记让本次源点搜索无需清空整张 Grid 的 Scratch
            InitializeCell(sourceCellIndex, generation, costs, heapPositions, generations);
            // 源 Cell 是本次局部 Dijkstra 唯一的零成本节点
            costs[sourceCellIndex] = 0f;
            PushCell(sourceCellIndex, costs, heap, heapPositions, ref heapCount);
            // 体型阈值在整个 Cluster 局部搜索中保持不变
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);

            // 堆只会接收当前 Cluster 的 Cell，局部搜索不会退化为全图展开
            while (heapCount > 0)
            {
                int current = PopCell(costs, heap, heapPositions, ref heapCount);
                int currentX = current % grid.Width;
                int currentZ = current / grid.Width;
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    NavigationGridDirections.GetDirection(
                        directionIndex,
                        out int deltaX,
                        out int deltaZ);
                    int neighborX = currentX + deltaX;
                    int neighborZ = currentZ + deltaZ;
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
                    // Cluster 边界在此处截断空间搜索
                    if (grid.Cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 反向模式只交换成本边方向，邻居枚举范围保持不变
                    int from = reverseEdges ? neighbor : current;
                    int to = reverseEdges ? current : neighbor;
                    int edgeDeltaX = reverseEdges ? -deltaX : deltaX;
                    int edgeDeltaZ = reverseEdges ? -deltaZ : deltaZ;
                    // 统一边校验保留 Clearance、邻接与对角穿角约束
                    if (!NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                            ref grid,
                            from,
                            to,
                            edgeDeltaX,
                            edgeDeltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            dynamicOverlay))
                    {
                        continue;
                    }

                    // 邻居首次进入本次 Generation 时再初始化堆位置和成本
                    if (generations[neighbor] != generation)
                    {
                        InitializeCell(neighbor, generation, costs, heapPositions, generations);
                    }

                    // 代价由有向步长、目标地形和 Clearance 惩罚共同组成
                    float candidate = costs[current] +
                                      NavigationGridCost.CalculateStepCost(
                                          ref grid,
                                          from,
                                          to,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight,
                                          dynamicOverlay) +
                                      NavigationGridCost.GetDynamicExtraCost(dynamicOverlay, to);
                    if (candidate >= costs[neighbor] - CostEpsilon)
                    {
                        continue;
                    }

                    // 严格改善后更新成本并调整最小堆
                    costs[neighbor] = candidate;
                    PushCell(neighbor, costs, heap, heapPositions, ref heapCount);
                }
            }
        }

        internal static uint CalculateHash(ref NativeList<int> corridorClusters)
        {
            // FNV-1a 仅承担快速筛选，正确性由命中后的完整序列比较保证
            uint hash = 2166136261u;
            // FNV-1a 按 Corridor 顺序混合每个 ClusterId
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                hash = (hash ^ (uint)corridorClusters[index]) * 16777619u;
            }

            return hash;
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

        private static void InitializeAbstractNode(
            int nodeIndex,
            int generation,
            NativeArray<float> costs,
            NativeArray<int> parents,
            NativeArray<int> heapPositions,
            NativeArray<int> generations)
        {
            // 抽象节点首次触达时同时清除旧父节点和旧堆位置
            generations[nodeIndex] = generation;
            costs[nodeIndex] = float.PositiveInfinity;
            parents[nodeIndex] = -1;
            heapPositions[nodeIndex] = -1;
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

        private static void PushAbstract(
            int nodeIndex,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            IndexedFloatHeap.PushMin(nodeIndex, costs, heap, positions, ref count, CostEpsilon);
        }

        private static int PopAbstract(
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
