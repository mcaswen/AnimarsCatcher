using AnimarsCatcher.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 用分层寻路选择起终点之间应经过的分块和入口，并在动态障碍变化后重算受影响的局部成本
    /// </summary>
    public static class NavigationGridCorridorSolver
    {
        private const float CostEpsilon = 0.00001f;

        // 起点和终点作为临时节点，分别连接到所在分块的入口图
        // 临时连接成本来自格子内的真实搜索，不能用可能穿墙的直线距离代替
        // 抽象边按最窄可用空间过滤，大体型角色不会被引向过窄入口
        // 路线成本相同时使用节点索引决定顺序，让重复计算得到同一条通道
        // Solver 只写 Native 容器，不读取 World 或管理 ECS 请求生命周期
        // 分块没有动态变化时直接使用烘焙好的内部成本
        // 动态障碍可能挡断入口之间的路线，附加成本也可能让另一组分块更划算
        // 因此受影响分块需要从当前入口重新执行局部 Dijkstra，统一考虑阻挡、成本和空间缩减
        // 每个入口使用独立的格子 Generation，不能读取上一个入口留下的成本
        // Generation 根据入口节点索引分配，不受抽象搜索先后顺序影响
        // Integration Field 使用所有入口局部搜索之后的独立 Generation
        // 动态障碍版本仍为初始值时不扫描分块，直接走静态快速路径
        // 障碍移除后版本不会倒退，因此还要确认分块内当前是否仍有动态影响
        // 局部扫描只遍历当前分块，不会为每条抽象边检查整张地图
        // 一个抽象节点只有在最低成本确定后才进行一次局部重算
        // 穿过分块入口只是一格真实移动，直接使用统一的通行和成本规则
        // 分块内部在当前动态障碍下无法到达的连接，会从宏观路线候选中移除
        // 最终 Flow Field 还会逐格验证选中的通道，移动系统不会收到未经验证的动态连接

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
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            bool dynamicOverlayMayBeActive)
        {
            expandedNodeCount = 0;
            int abstractGeneration = generationStart;
            int startCostGeneration = generationStart;
            // 在起点分块内实际寻路到各入口，避免直线估价穿过障碍
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
            // 可到达入口的局部成本作为抽象图搜索的多个起始值
            for (int index = 0; index < startClusterData.PortalNodeCount; index++)
            {
                int nodeIndex = grid.ClusterPortalNodeIndices[
                    startClusterData.PortalNodeOffset + index];
                int nodeCell = grid.PortalNodes[nodeIndex].CellIndex;
                if (cellGenerations[nodeCell] != startCostGeneration)
                {
                    continue;
                }

                // 本次搜索首次访问入口节点时再初始化它的临时数据
                InitializeAbstractNode(
                    nodeIndex,
                    abstractGeneration,
                    abstractCosts,
                    abstractParents,
                    abstractHeapPositions,
                    abstractGenerations);
                // 从起点到入口的真实成本成为分层 Dijkstra 的初始成本
                abstractCosts[nodeIndex] = cellCosts[nodeCell];
                PushAbstract(
                    nodeIndex,
                    abstractCosts,
                    abstractHeap,
                    abstractHeapPositions,
                    ref abstractHeapCount);
            }

            // 起点到不了所在分块的任何入口时，宏观路线没有合法起点
            if (abstractHeapCount == 0)
            {
                return false;
            }

            int endCostGeneration = generationStart + 1;

            // 在终点分块内反向计算各入口到终点的真实成本
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
            // 有向成本取决于进入哪个格子，因此不能简单交换起终点复用正向结果
            NavigationGridCluster endClusterData = grid.Clusters[endCluster];
            for (int index = 0; index < endClusterData.PortalNodeCount; index++)
            {
                int nodeIndex = grid.ClusterPortalNodeIndices[
                    endClusterData.PortalNodeOffset + index];
                int nodeCell = grid.PortalNodes[nodeIndex].CellIndex;
                // 到不了终点的入口保持无穷成本，分层搜索不会把它当成出口
                abstractEndCosts[nodeIndex] = cellGenerations[nodeCell] == endCostGeneration
                    ? cellCosts[nodeCell]
                    : float.PositiveInfinity;
            }

            float bestTotalCost = float.PositiveInfinity;
            int bestGoalNode = -1;

            // 烘焙边已包含分块内部最低成本，因此抽象图直接使用 Dijkstra
            while (abstractHeapCount > 0)
            {
                int current = PopAbstract(
                    abstractCosts,
                    abstractHeap,
                    abstractHeapPositions,
                    ref abstractHeapCount);
                // 待处理节点的成本已不可能优于当前最佳终点路线时，可以提前结束
                if (abstractCosts[current] >= bestTotalCost - CostEpsilon)
                {
                    break;
                }

                expandedNodeCount++;
                NavigationGridPortalNode currentNode = grid.PortalNodes[current];
                // 到达终点分块的入口后，与预先计算的入口到终点成本相加即可得到完整路线成本
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

                // 动态障碍发生过变化后，只为当前仍受影响的分块重算内部连接
                // 每个源入口使用自己的 Generation，避免临时成本互相串用
                bool useDynamicLocalCosts = dynamicOverlayMayBeActive &&
                                            HasActiveDynamicOverlayInCluster(
                                                ref grid,
                                                currentNode.ClusterId,
                                                dynamicOverlay);
                int dynamicLocalGeneration = generationStart + 2 + current;
                if (useDynamicLocalCosts)
                {
                    RunLocalCosts(
                        ref grid,
                        currentNode.CellIndex,
                        currentNode.ClusterId,
                        request,
                        false,
                        dynamicLocalGeneration,
                        cellCosts,
                        cellHeap,
                        cellHeapPositions,
                        cellGenerations,
                        dynamicOverlay);
                }

                for (int edgeIndex = 0; edgeIndex < currentNode.EdgeCount; edgeIndex++)
                {
                    NavigationGridAbstractEdge edge =
                        grid.AbstractEdges[currentNode.EdgeOffset + edgeIndex];
                    // 入口或路线空间不足时直接排除，不允许进入候选宏观路线
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
                    // 抽象节点临时数据在首次访问时初始化，避免每次请求清空所有节点
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

                    // 堆位置 -2 表示节点成本已经确定，不会再次打开
                    if (abstractHeapPositions[target] == -2)
                    {
                        continue;
                    }

                    float edgeCost;
                    if (edge.CrossesPortal != 0)
                    {
                        int currentCell = currentNode.CellIndex;
                        int deltaX = targetCell % grid.Width - currentCell % grid.Width;
                        int deltaZ = targetCell / grid.Width - currentCell / grid.Width;
                        if (!NavigationGridTraversal.CanAgentTraverseEdgeDynamic(
                                ref grid,
                                currentCell,
                                targetCell,
                                deltaX,
                                deltaZ,
                                request.AgentRadius,
                                request.ClearanceMargin,
                                dynamicOverlay))
                        {
                            continue;
                        }

                        // 穿过入口只有一步，直接使用运行时统一成本即可包含动态影响
                        edgeCost = NavigationGridCost.CalculateStepCost(
                                       ref grid,
                                       currentCell,
                                       targetCell,
                                       requiredClearance,
                                       request.ClearancePenaltyWeight,
                                       dynamicOverlay) +
                                   NavigationGridCost.GetDynamicExtraCost(
                                       dynamicOverlay,
                                       targetCell);
                    }
                    else if (useDynamicLocalCosts)
                    {
                        // 受影响分块的内部连接必须重新确认可达，不能沿用会穿过新障碍的静态成本
                        if (cellGenerations[targetCell] != dynamicLocalGeneration)
                        {
                            continue;
                        }

                        edgeCost = cellCosts[targetCell];
                    }
                    else
                    {
                        // 未受影响时继续使用烘焙成本，并保留对狭窄通道的偏好惩罚
                        float extraClearance = math.max(
                            0f,
                            edge.MinimumClearance - requiredClearance);
                        float clearancePenalty = math.max(0f, request.ClearancePenaltyWeight) *
                                                 grid.CellSize /
                                                 (grid.CellSize + extraClearance);
                        edgeCost = math.max(0f, edge.StaticCost) +
                                   clearancePenalty * grid.CellSize;
                    }

                    float candidateCost = abstractCosts[current] +
                                          math.max(0f, edgeCost);
                    // 出边顺序固定，且只接受成本严格降低的更新，等价路线会保持同一选择
                    if (candidateCost >= abstractCosts[target] - CostEpsilon)
                    {
                        continue;
                    }

                    // 只有成本更低时才更新父节点；成本相同则保留先找到的路线
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
            // 父节点链先按终点到起点写入，避免不断向 NativeList 头部插入
            while (node >= 0)
            {
                nodeChain.Add(node);
                node = abstractParents[node];
            }

            corridorClusters.Clear();
            corridorPortals.Clear();
            corridorClusters.Add(startCluster);
            int currentCluster = startCluster;
            // 再逆序读取父节点链，只有真正进入相邻分块时才追加入口和新分块
            for (int index = nodeChain.Length - 1; index >= 0; index--)
            {
                NavigationGridPortalNode pathNode = grid.PortalNodes[nodeChain[index]];
                // 同一分块内在多个入口节点之间移动，不会增加通道分块
                if (pathNode.ClusterId == currentCluster)
                {
                    continue;
                }

                // 进入相邻分块时，也就确定了实际穿过的入口
                corridorPortals.Add(pathNode.PortalIndex);
                corridorClusters.Add(pathNode.ClusterId);
                currentCluster = pathNode.ClusterId;
            }

            // 父节点链最后没有进入目标分块，说明抽象路线不完整
            return currentCluster == endCluster;
        }

        private static bool HasActiveDynamicOverlayInCluster(
            ref NavigationGridBlob grid,
            int clusterId,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay)
        {
            if (!dynamicOverlay.IsCreated ||
                dynamicOverlay.Length < grid.Cells.Length ||
                clusterId < 0 ||
                clusterId >= grid.Clusters.Length)
            {
                return false;
            }

            NavigationGridCluster cluster = grid.Clusters[clusterId];
            // 版本只增不减，即使障碍已经移除也不会回退，所以要检查分块内当前是否仍有实际影响
            for (int z = cluster.MinimumZ; z < cluster.MaximumZExclusive; z++)
            {
                for (int x = cluster.MinimumX; x < cluster.MaximumXExclusive; x++)
                {
                    NavigationDynamicOverlayCell overlay = dynamicOverlay[x + z * grid.Width];
                    if (overlay.BlockCount > 0 ||
                        overlay.ExtraCost > 0f ||
                        overlay.ClearanceReduction > 0f)
                    {
                        return true;
                    }
                }
            }

            return false;
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
            // Generation 标记区分本次局部搜索，无需清空整张网格的临时数组
            InitializeCell(sourceCellIndex, generation, costs, heapPositions, generations);
            // 源格子是本次局部 Dijkstra 唯一的零成本起点
            costs[sourceCellIndex] = 0f;
            PushCell(sourceCellIndex, costs, heap, heapPositions, ref heapCount);
            // 整个分块搜索都使用同一角色空间需求
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);

            // 堆中只加入当前分块的格子，局部搜索不会扩散到整张地图
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
                    // 相邻格子离开当前分块时停止扩展
                    if (grid.Cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 反向搜索只改变单步成本的方向，仍检查同样的相邻格子
                    int from = reverseEdges ? neighbor : current;
                    int to = reverseEdges ? current : neighbor;
                    int edgeDeltaX = reverseEdges ? -deltaX : deltaX;
                    int edgeDeltaZ = reverseEdges ? -deltaZ : deltaZ;
                    // 使用统一通行检查，角色空间、烘焙邻接和斜向穿角规则都会生效
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

                    // 邻居在本次搜索中首次出现时才初始化成本和堆位置
                    if (generations[neighbor] != generation)
                    {
                        InitializeCell(neighbor, generation, costs, heapPositions, generations);
                    }

                    // 单步成本包含移动距离、进入格子的地形成本和狭窄空间惩罚
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

                    // 只有找到更低成本时才更新并调整最小堆
                    costs[neighbor] = candidate;
                    PushCell(neighbor, costs, heap, heapPositions, ref heapCount);
                }
            }
        }

        internal static uint CalculateHash(ref NativeList<int> corridorClusters)
        {
            // FNV-1a 只用于快速筛选，缓存命中仍要比较完整分块序列
            uint hash = 2166136261u;
            // 按通道顺序将每个分块编号混入 FNV-1a 哈希
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
            // 最后写 Generation，确保标记生效时成本和堆位置也已经属于本次搜索
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
            // 抽象节点首次访问时清除上一请求留下的父节点和堆位置
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
