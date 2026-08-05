using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供 Burst 可执行的 HPA 星 Corridor 和局部 Flow Field 算法
    /// </summary>
    public static class NavigationGridFlowFieldAlgorithms
    {
        private const float CostEpsilon = 0.00001f;
        private const int MaximumCacheEntries = 64;

        internal static NavigationFlowFieldJobResult Build(
            ref NavigationGridBlob grid,
            NavigationFlowFieldJobRequest jobRequest,
            int generationStart,
            uint cacheVersion,
            ref NativeList<int> corridorClusters,
            ref NativeList<int> corridorPortals,
            ref NativeList<int> hierarchicalWaypointCells,
            ref NativeList<NavigationFlowFieldCell> flowCells,
            NativeArray<float> cellCosts,
            NativeArray<int> cellHeap,
            NativeArray<int> cellHeapPositions,
            NativeArray<int> cellGenerations,
            NativeArray<int> clusterGenerations,
            NativeArray<float> abstractCosts,
            NativeArray<float> abstractEndCosts,
            NativeArray<int> abstractParents,
            NativeArray<int> abstractHeap,
            NativeArray<int> abstractHeapPositions,
            NativeArray<int> abstractGenerations,
            ref NativeList<int> workVisitedCells,
            ref NativeList<int> workCorridorClusters,
            ref NativeList<int> workCorridorPortals,
            ref NativeList<int> workNodeChain,
            ref NativeList<NavigationFlowFieldCacheEntry> cacheEntries,
            ref NativeList<int> cacheCorridorClusters,
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells)
        {
            NavigationPathRequest request = jobRequest.Request.PathRequest;
            // 先初始化失败结果，后续分支只覆盖更具体的失败原因
            NavigationFlowFieldJobResult result = CreateFailureResult(
                jobRequest.Entity,
                request.Version,
                NavigationPathFailureReason.InvalidRequest,
                cacheVersion);
            // Scratch 形状必须与当前 Grid 一致，Generation 才能安全隔离复用数据
            if (!IsShapeValid(
                    ref grid,
                    cellCosts,
                    cellHeap,
                    cellHeapPositions,
                    cellGenerations,
                    clusterGenerations,
                    abstractCosts,
                    abstractEndCosts,
                    abstractParents,
                    abstractHeap,
                    abstractHeapPositions,
                    abstractGenerations) ||
                generationStart <= 0 ||
                !NavigationGridPathAlgorithms.IsRequestValid(request))
            {
                return result;
            }

            // 起点投影失败时保留默认的负 Cell 索引
            if (!NavigationGridPathAlgorithms.TryProjectToNearestCell(
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
            // 终点使用同一体型和最大投影半径，避免两端规则不一致
            if (!NavigationGridPathAlgorithms.TryProjectToNearestCell(
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
            NavigationGridCell startCell = grid.Cells[startCellIndex];
            NavigationGridCell endCell = grid.Cells[endCellIndex];
            // 静态 Region 不连通时无需分配 Corridor 和 Field 输出
            if (startCell.RegionId <= 0 || startCell.RegionId != endCell.RegionId)
            {
                result.FailureReason = NavigationPathFailureReason.RegionMismatch;
                return result;
            }

            // 投影 Cell 的 ClusterId 必须能索引当前分层 Blob
            int startCluster = startCell.ClusterId;
            int endCluster = endCell.ClusterId;
            if (startCluster < 0 ||
                startCluster >= grid.Clusters.Length ||
                endCluster < 0 ||
                endCluster >= grid.Clusters.Length)
            {
                result.FailureReason = NavigationPathFailureReason.InvalidGrid;
                return result;
            }

            // 工作列表按请求复用，构建新 Corridor 前清除上一请求内容
            workCorridorClusters.Clear();
            workCorridorPortals.Clear();
            workNodeChain.Clear();

            // Clearance 同时约束端点局部搜索、Portal 过滤和最终 Integration Field
            float requiredClearance = NavigationGridPathAlgorithms.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            int abstractExpandedNodeCount = 0;
            // 同 Cluster 请求跳过抽象图，只生成该 Cluster 的局部 Field
            if (startCluster == endCluster)
            {
                workCorridorClusters.Add(startCluster);
            }
            // 跨 Cluster 时把起终点作为虚拟节点连接到各自的 Portal Node
            else if (!TryBuildAbstractCorridor(
                         ref grid,
                         startCellIndex,
                         endCellIndex,
                         startCluster,
                         endCluster,
                         requiredClearance,
                         request,
                         generationStart,
                         cellCosts,
                         cellHeap,
                         cellHeapPositions,
                         cellGenerations,
                         abstractCosts,
                         abstractEndCosts,
                         abstractParents,
                         abstractHeap,
                         abstractHeapPositions,
                         abstractGenerations,
                         ref workCorridorClusters,
                         ref workCorridorPortals,
                         ref workNodeChain,
                         out abstractExpandedNodeCount))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }

            // 保存切片起点，后续失败时可恢复到本请求写入前的长度
            int corridorClusterOffset = corridorClusters.Length;

            // Corridor 追加到共享列表，Result 只保存对应偏移和长度
            for (int index = 0; index < workCorridorClusters.Length; index++)
            {
                corridorClusters.Add(workCorridorClusters[index]);
            }

            // Portal 使用独立连续切片，顺序与 Corridor 的 Cluster 跨越顺序一致
            int corridorPortalOffset = corridorPortals.Length;
            for (int index = 0; index < workCorridorPortals.Length; index++)
            {
                corridorPortals.Add(workCorridorPortals[index]);
            }

            // 宏观路点由投影起点、Portal 代表 Cell 和投影终点组成
            int waypointOffset = hierarchicalWaypointCells.Length;
            AppendUnique(hierarchicalWaypointCells, startCellIndex);
            for (int index = workNodeChain.Length - 1; index >= 0; index--)
            {
                AppendUnique(
                    hierarchicalWaypointCells,
                    grid.PortalNodes[workNodeChain[index]].CellIndex);
            }
            AppendUnique(hierarchicalWaypointCells, endCellIndex);

            // 缓存键使用宏观 Cluster 序列，不依赖临时 Portal Node 父链
            uint corridorHash = CalculateCorridorHash(ref workCorridorClusters);
            int fieldOffset = flowCells.Length;
            int integrationExpandedCellCount = 0;

            // Corridor Hash 只作缓存初筛，命中函数还会比较完整 Cluster 序列
            bool cacheHit = TryAppendCachedField(
                endCellIndex,
                requiredClearance,
                request.ClearancePenaltyWeight,
                corridorHash,
                cacheVersion,
                ref workCorridorClusters,
                ref cacheEntries,
                ref cacheCorridorClusters,
                ref cacheFlowCells,
                ref flowCells);
            int integrationGeneration = generationStart + 2;
            // 只有缓存未命中时才在 Corridor 内反向生成 Integration Field
            if (!cacheHit)
            {
                if (!BuildIntegrationField(
                        ref grid,
                        startCellIndex,
                        endCellIndex,
                        request,
                        integrationGeneration,
                        ref workCorridorClusters,
                        cellCosts,
                        cellHeap,
                        cellHeapPositions,
                        cellGenerations,
                        clusterGenerations,
                        ref workVisitedCells,
                        ref flowCells,
                        out integrationExpandedCellCount))
                {
                    // 恢复 Corridor 输出到本请求写入前的切片起点
                    corridorClusters.ResizeUninitialized(corridorClusterOffset);
                    corridorPortals.ResizeUninitialized(corridorPortalOffset);
                    // 路点和 Field 也必须同步回滚，避免留下不完整结果
                    hierarchicalWaypointCells.ResizeUninitialized(waypointOffset);
                    flowCells.ResizeUninitialized(fieldOffset);
                    // 完成回滚后再返回，下一请求才能安全复用共享列表
                    result.FailureReason = NavigationPathFailureReason.NoPath;
                    return result;
                }

                AddCachedField(
                    endCellIndex,
                    requiredClearance,
                    request.ClearancePenaltyWeight,
                    corridorHash,
                    cacheVersion,
                    fieldOffset,
                    flowCells.Length - fieldOffset,
                    ref workCorridorClusters,
                    ref flowCells,
                    ref cacheEntries,
                    ref cacheCorridorClusters,
                    ref cacheFlowCells);
            }

            // 新建 Field 可直接读取 Scratch，缓存命中则从复制后的稀疏 Field 查询
            float totalCost = 0f;
            if (!cacheHit && cellGenerations[startCellIndex] == integrationGeneration)
            {
                totalCost = cellCosts[startCellIndex];
            }
            else
            {
                TryGetIntegrationCost(
                    ref flowCells,
                    fieldOffset,
                    flowCells.Length - fieldOffset,
                    startCellIndex,
                    out totalCost);
            }

            // 所有输出切片都完整后才把结果切换为成功
            result.Status = NavigationPathStatus.Succeeded;
            result.FailureReason = NavigationPathFailureReason.None;
            result.CorridorClusterOffset = corridorClusterOffset;
            result.CorridorClusterCount = workCorridorClusters.Length;
            result.CorridorPortalOffset = corridorPortalOffset;
            result.CorridorPortalCount = workCorridorPortals.Length;
            result.HierarchicalWaypointOffset = waypointOffset;
            result.HierarchicalWaypointCount = hierarchicalWaypointCells.Length - waypointOffset;
            result.FieldOffset = fieldOffset;
            result.FieldCount = flowCells.Length - fieldOffset;
            result.AbstractExpandedNodeCount = abstractExpandedNodeCount;
            result.IntegrationExpandedCellCount = integrationExpandedCellCount;
            result.TotalCost = totalCost;
            result.CacheHit = cacheHit ? (byte)1 : (byte)0;
            return result;
        }

        internal static NavigationFlowFieldJobResult CreateFailureResult(
            Entity entity,
            uint requestVersion,
            NavigationPathFailureReason failureReason,
            uint cacheVersion)
        {
            // 失败结果统一使用负偏移，System 写回前仍会验证切片范围
            return new NavigationFlowFieldJobResult
            {
                Entity = entity,
                RequestVersion = requestVersion,
                Status = NavigationPathStatus.Failed,
                FailureReason = failureReason,
                ProjectedStartCellIndex = -1,
                ProjectedEndCellIndex = -1,
                CorridorClusterOffset = -1,
                CorridorPortalOffset = -1,
                HierarchicalWaypointOffset = -1,
                FieldOffset = -1,
                CacheVersion = cacheVersion,
            };
        }

        private static bool TryBuildAbstractCorridor(
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
            out int expandedNodeCount)
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
                cellGenerations);

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
                cellGenerations);
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
            NativeArray<int> generations)
        {
            int heapCount = 0;
            // Generation 标记让本次源点搜索无需清空整张 Grid 的 Scratch
            InitializeCell(sourceCellIndex, generation, costs, heapPositions, generations);
            // 源 Cell 是本次局部 Dijkstra 唯一的零成本节点
            costs[sourceCellIndex] = 0f;
            PushCell(sourceCellIndex, costs, heap, heapPositions, ref heapCount);
            // 体型阈值在整个 Cluster 局部搜索中保持不变
            float requiredClearance = NavigationGridPathAlgorithms.CalculateRequiredClearance(
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
                    NavigationGridPathAlgorithms.GetDirection(
                        directionIndex,
                        out int deltaX,
                        out int deltaZ);
                    int neighborX = currentX + deltaX;
                    int neighborZ = currentZ + deltaZ;
                    if (!NavigationGridPathAlgorithms.IsInside(
                            neighborX,
                            neighborZ,
                            grid.Width,
                            grid.Height))
                    {
                        continue;
                    }

                    int neighbor = neighborX + neighborZ * grid.Width;
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
                    if (!NavigationGridPathAlgorithms.CanAgentTraverseEdge(
                            ref grid,
                            from,
                            to,
                            edgeDeltaX,
                            edgeDeltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin))
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
                                      NavigationGridPathAlgorithms.CalculateStepCost(
                                          ref grid,
                                          from,
                                          to,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight);
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

        private static bool BuildIntegrationField(
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
            out int expandedCellCount)
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
            float requiredClearance = NavigationGridPathAlgorithms.CalculateRequiredClearance(
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
                    NavigationGridPathAlgorithms.GetDirection(
                        directionIndex,
                        out int deltaX,
                        out int deltaZ);
                    int predecessorX = currentX + deltaX;
                    int predecessorZ = currentZ + deltaZ;
                    if (!NavigationGridPathAlgorithms.IsInside(
                            predecessorX,
                            predecessorZ,
                            grid.Width,
                            grid.Height))
                    {
                        continue;
                    }

                    int predecessor = predecessorX + predecessorZ * grid.Width;
                    int predecessorCluster = grid.Cells[predecessor].ClusterId;
                    // 前驱必须位于 Corridor，并满足与正向移动相同的边约束
                    if (predecessorCluster < 0 ||
                        predecessorCluster >= clusterGenerations.Length ||
                        clusterGenerations[predecessorCluster] != generation ||
                        !NavigationGridPathAlgorithms.CanAgentTraverseEdge(
                            ref grid,
                            predecessor,
                            current,
                            -deltaX,
                            -deltaZ,
                            request.AgentRadius,
                            request.ClearanceMargin))
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
                                      NavigationGridPathAlgorithms.CalculateStepCost(
                                          ref grid,
                                          predecessor,
                                          current,
                                          requiredClearance,
                                          request.ClearancePenaltyWeight);
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
                    Direction = CalculateFlowDirection(
                        ref grid,
                        cellIndex,
                        targetCellIndex,
                        request,
                        generation,
                        costs,
                        generations),
                });
            }

            return true;
        }

        private static float2 CalculateFlowDirection(
            ref NavigationGridBlob grid,
            int cellIndex,
            int targetCellIndex,
            NavigationPathRequest request,
            int generation,
            NativeArray<float> costs,
            NativeArray<int> generations)
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
                NavigationGridPathAlgorithms.GetDirection(
                    directionIndex,
                    out int deltaX,
                    out int deltaZ);
                int neighborX = cellX + deltaX;
                int neighborZ = cellZ + deltaZ;
                if (!NavigationGridPathAlgorithms.IsInside(
                        neighborX,
                        neighborZ,
                        grid.Width,
                        grid.Height))
                {
                    continue;
                }

                int neighbor = neighborX + neighborZ * grid.Width;
                // 当前 Generation 同时限定 Corridor Field 成员和本次成本有效性
                if (generations[neighbor] != generation ||
                    costs[neighbor] >= currentCost - CostEpsilon ||
                    !NavigationGridPathAlgorithms.CanAgentTraverseEdge(
                        ref grid,
                        cellIndex,
                        neighbor,
                        deltaX,
                        deltaZ,
                        request.AgentRadius,
                        request.ClearanceMargin))
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
                NavigationGridPathAlgorithms.GetDirection(
                    directionIndex,
                    out int deltaX,
                    out int deltaZ);
                int neighborX = cellX + deltaX;
                int neighborZ = cellZ + deltaZ;
                if (!NavigationGridPathAlgorithms.IsInside(
                        neighborX,
                        neighborZ,
                        grid.Width,
                        grid.Height))
                {
                    continue;
                }

                int neighbor = neighborX + neighborZ * grid.Width;
                float2 direction = math.normalizesafe(new float2(deltaX, deltaZ));
                float alignment = math.dot(direction, smoothedDirection);
                // 对齐度相同时选择更小 Cell 索引，保证跨运行的确定性
                if (generations[neighbor] != generation ||
                    costs[neighbor] >= currentCost - CostEpsilon ||
                    !NavigationGridPathAlgorithms.CanAgentTraverseEdge(
                        ref grid,
                        cellIndex,
                        neighbor,
                        deltaX,
                        deltaZ,
                        request.AgentRadius,
                        request.ClearanceMargin) ||
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

        private static bool TryAppendCachedField(
            int targetCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight,
            uint corridorHash,
            uint cacheVersion,
            ref NativeList<int> corridorClusters,
            ref NativeList<NavigationFlowFieldCacheEntry> cacheEntries,
            ref NativeList<int> cacheCorridorClusters,
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells,
            ref NativeList<NavigationFlowFieldCell> output)
        {
            // 浮点参数按位进入键，避免近似比较合并体型或代价不同的请求
            int requiredClearanceBits = math.asint(requiredClearance);
            int penaltyBits = math.asint(clearancePenaltyWeight);
            for (int entryIndex = 0; entryIndex < cacheEntries.Length; entryIndex++)
            {
                NavigationFlowFieldCacheEntry entry = cacheEntries[entryIndex];
                // 版本随 Grid 变化或容量回收递增，过期切片不能跨代复用
                if (entry.TargetCellIndex != targetCellIndex ||
                    entry.RequiredClearanceBits != requiredClearanceBits ||
                    entry.ClearancePenaltyWeightBits != penaltyBits ||
                    entry.CorridorHash != corridorHash ||
                    entry.CorridorCount != corridorClusters.Length ||
                    entry.CacheVersion != cacheVersion ||
                    entry.CorridorOffset < 0 ||
                    entry.CorridorOffset + entry.CorridorCount > cacheCorridorClusters.Length ||
                    entry.FieldOffset < 0 ||
                    entry.FieldOffset + entry.FieldCount > cacheFlowCells.Length)
                {
                    continue;
                }

                bool corridorMatches = true;

                for (int index = 0; index < corridorClusters.Length; index++)
                {
                    // Hash 碰撞时仍需逐项比较，Cluster 顺序也是缓存键的一部分
                    if (cacheCorridorClusters[entry.CorridorOffset + index] !=
                        corridorClusters[index])
                    {
                        corridorMatches = false;
                        break;
                    }
                }

                // 任一 Cluster 不同都使 Hash 初筛失效
                if (!corridorMatches)
                {
                    continue;
                }

                // 批次输出拥有独立连续切片，不能直接暴露跨批次缓存容器
                for (int index = 0; index < entry.FieldCount; index++)
                {
                    // 复制保持缓存 Field 的 CellIndex 与成本配对关系
                    output.Add(cacheFlowCells[entry.FieldOffset + index]);
                }

                return true;
            }

            return false;
        }

        private static void AddCachedField(
            int targetCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight,
            uint corridorHash,
            uint cacheVersion,
            int fieldOffset,
            int fieldCount,
            ref NativeList<int> corridorClusters,
            ref NativeList<NavigationFlowFieldCell> flowCells,
            ref NativeList<NavigationFlowFieldCacheEntry> cacheEntries,
            ref NativeList<int> cacheCorridorClusters,
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells)
        {
            // 空 Field 或达到容量上限时跳过缓存，当前请求结果仍保持有效
            if (cacheEntries.Length >= MaximumCacheEntries || fieldCount <= 0)
            {
                return;
            }

            // 缓存只追加切片，不搬移可能正被当前批次引用的数据
            int corridorOffset = cacheCorridorClusters.Length;
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                cacheCorridorClusters.Add(corridorClusters[index]);
            }

            // FieldOffset 在复制前捕获，随后 Field 作为一个连续值切片追加
            int cacheFieldOffset = cacheFlowCells.Length;
            for (int index = 0; index < fieldCount; index++)
            {
                cacheFlowCells.Add(flowCells[fieldOffset + index]);
            }

            // 元数据最后发布，任何可见缓存项都已经拥有完整 Corridor 和 Field
            cacheEntries.Add(new NavigationFlowFieldCacheEntry
            {
                TargetCellIndex = targetCellIndex,
                RequiredClearanceBits = math.asint(requiredClearance),
                ClearancePenaltyWeightBits = math.asint(clearancePenaltyWeight),
                CorridorHash = corridorHash,
                CorridorOffset = corridorOffset,
                CorridorCount = corridorClusters.Length,
                FieldOffset = cacheFieldOffset,
                FieldCount = fieldCount,
                CacheVersion = cacheVersion,
            });
        }

        private static uint CalculateCorridorHash(ref NativeList<int> corridorClusters)
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

        private static bool TryGetIntegrationCost(
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

        private static void AppendUnique(NativeList<int> values, int value)
        {
            // 只消除连续重复路点，不改变真实的路径回访顺序
            if (values.Length == 0 || values[values.Length - 1] != value)
            {
                values.Add(value);
            }
        }

        private static bool IsShapeValid(
            ref NavigationGridBlob grid,
            NativeArray<float> cellCosts,
            NativeArray<int> cellHeap,
            NativeArray<int> cellHeapPositions,
            NativeArray<int> cellGenerations,
            NativeArray<int> clusterGenerations,
            NativeArray<float> abstractCosts,
            NativeArray<float> abstractEndCosts,
            NativeArray<int> abstractParents,
            NativeArray<int> abstractHeap,
            NativeArray<int> abstractHeapPositions,
            NativeArray<int> abstractGenerations)
        {
            int cellCount = grid.Cells.Length;
            int clusterCount = grid.Clusters.Length;
            int nodeCount = grid.PortalNodes.Length;
            // Scratch 可大于当前需求，但每类数组都必须覆盖对应索引上界
            return NavigationGridPathAlgorithms.IsGridShapeValid(ref grid) &&
                   grid.DataVersion >= 3 &&
                   grid.ClusterWidth > 0 &&
                   grid.ClusterHeight > 0 &&
                   clusterCount == grid.ClusterWidth * grid.ClusterHeight &&
                   nodeCount == grid.Portals.Length * 2 &&
                   grid.ClusterPortalNodeIndices.Length == nodeCount &&
                   cellCosts.Length >= cellCount &&
                   cellHeap.Length >= cellCount &&
                   cellHeapPositions.Length >= cellCount &&
                   cellGenerations.Length >= cellCount &&
                   clusterGenerations.Length >= clusterCount &&
                   abstractCosts.Length >= nodeCount &&
                   abstractEndCosts.Length >= nodeCount &&
                   abstractParents.Length >= nodeCount &&
                   abstractHeap.Length >= nodeCount &&
                   abstractHeapPositions.Length >= nodeCount &&
                   abstractGenerations.Length >= nodeCount;
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
            Push(cellIndex, costs, heap, positions, ref count);
        }

        private static int PopCell(
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            return Pop(costs, heap, positions, ref count, -2);
        }

        private static void PushAbstract(
            int nodeIndex,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            Push(nodeIndex, costs, heap, positions, ref count);
        }

        private static int PopAbstract(
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            return Pop(costs, heap, positions, ref count, -2);
        }

        private static void Push(
            int value,
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count)
        {
            int position = positions[value];
            // 负位置表示节点尚未进入开放堆
            if (position < 0)
            {
                position = count++;
                heap[position] = value;
                positions[value] = position;
            }

            // 调用方只会降低现有节点成本，因此从当前位置向上恢复堆序
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (!IsLower(heap[position], heap[parent], costs))
                {
                    break;
                }

                Swap(position, parent, heap, positions);
                position = parent;
            }
        }

        private static int Pop(
            NativeArray<float> costs,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count,
            int closedValue)
        {
            int result = heap[0];
            // closedValue 阻止同一 Generation 内已确定节点再次进入开放堆
            positions[result] = closedValue;
            count--;
            if (count == 0)
            {
                return result;
            }

            // 用末尾节点补根后向下恢复最小堆
            heap[0] = heap[count];
            positions[heap[0]] = 0;
            int position = 0;
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= count)
                {
                    break;
                }

                int right = left + 1;
                int best = right < count && IsLower(heap[right], heap[left], costs)
                    ? right
                    : left;
                if (!IsLower(heap[best], heap[position], costs))
                {
                    break;
                }

                Swap(position, best, heap, positions);
                position = best;
            }

            return result;
        }

        private static bool IsLower(
            int left,
            int right,
            NativeArray<float> costs)
        {
            // 等价成本以更小索引打破平局，保持堆弹出顺序稳定
            return costs[left] < costs[right] - CostEpsilon ||
                   (math.abs(costs[left] - costs[right]) <= CostEpsilon && left < right);
        }

        private static void Swap(
            int left,
            int right,
            NativeArray<int> heap,
            NativeArray<int> positions)
        {
            int value = heap[left];
            heap[left] = heap[right];
            heap[right] = value;
            positions[heap[left]] = left;
            positions[heap[right]] = right;
        }
    }
}
