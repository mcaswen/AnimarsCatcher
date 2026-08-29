using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 依次完成端点纠正、分层通道搜索、Flow Field 构建和缓存复用
    /// </summary>
    public static class NavigationFlowFieldSolver
    {
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
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            NativeArray<NavigationDynamicOverlayCluster> dynamicOverlayClusters,
            uint dynamicOverlayVersion)
        {
            NavigationPathRequest request = jobRequest.Request.PathRequest;
            // 先准备完整失败结果，后续提前返回时只需填写具体原因
            NavigationFlowFieldJobResult result = CreateFailureResult(
                jobRequest.Entity,
                request.Version,
                NavigationPathFailureReason.InvalidRequest,
                cacheVersion);
            result.DynamicOverlayVersion = dynamicOverlayVersion;
            // 临时数组必须覆盖当前导航网格，Generation 才能安全区分不同请求的数据
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
                !NavigationGridQuery.IsRequestValid(request))
            {
                return result;
            }

            // 起点无法纠正到可站立格子时保留负索引并返回对应失败原因
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
            // 起终点使用相同的角色体型和搜索半径，避免两端采用不同规则
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
            if (NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, startCellIndex) ||
                NavigationGridTraversal.IsDynamicCellBlocked(dynamicOverlay, endCellIndex))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }
            NavigationGridCell startCell = grid.Cells[startCellIndex];
            NavigationGridCell endCell = grid.Cells[endCellIndex];
            // 起终点不在同一静态连通区域时，无需继续构建宏观通道和 Flow Field
            if (startCell.RegionId <= 0 || startCell.RegionId != endCell.RegionId)
            {
                result.FailureReason = NavigationPathFailureReason.RegionMismatch;
                return result;
            }

            // 纠正后格子的分块编号必须能在当前分层数据中找到
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

            if (jobRequest.Request.CoverageMode ==
                NavigationFlowFieldCoverageMode.GoalRegion)
            {
                // 目标区域场从终点覆盖整个静态连通区域，结果不再依赖构建代表的精确起点
                return BuildGoalRegionField(
                    ref grid,
                    request,
                    startCellIndex,
                    endCellIndex,
                    generationStart,
                    ref result,
                    ref corridorClusters,
                    ref corridorPortals,
                    ref hierarchicalWaypointCells,
                    ref flowCells,
                    cellCosts,
                    cellHeap,
                    cellHeapPositions,
                    cellGenerations,
                    clusterGenerations,
                    ref workVisitedCells,
                    ref workCorridorClusters,
                    dynamicOverlay,
                    dynamicOverlayClusters);
            }

            // 临时列表会在请求之间复用，开始新通道前先清除上一条内容
            workCorridorClusters.Clear();
            workCorridorPortals.Clear();
            workNodeChain.Clear();

            // 同一个角色空间需求用于端点搜索、分块入口筛选和最终 Flow Field
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            int abstractExpandedNodeCount = 0;
            bool dynamicOverlayMayBeActive = dynamicOverlayVersion > 1u;
            // 起终点在同一分块时无需搜索抽象图，只为该分块生成局部 Flow Field
            if (startCluster == endCluster)
            {
                workCorridorClusters.Add(startCluster);
            }
            // 跨分块时将起点和终点作为临时节点，连接到各自分块的通道节点
            else if (!NavigationGridCorridorSolver.TryBuildAbstractCorridor(
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
                         out abstractExpandedNodeCount,
                         dynamicOverlay,
                         dynamicOverlayMayBeActive))
            {
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }

            // 先记录各输出列表的长度；后续失败时可以撤销本请求已写入的部分
            int corridorClusterOffset = corridorClusters.Length;

            // 通道追加到共享列表，单条结果只记录自己的起始位置和长度
            for (int index = 0; index < workCorridorClusters.Length; index++)
            {
                corridorClusters.Add(workCorridorClusters[index]);
            }

            // 分块入口单独连续保存，顺序与通道跨越分块的顺序一致
            int corridorPortalOffset = corridorPortals.Length;
            for (int index = 0; index < workCorridorPortals.Length; index++)
            {
                corridorPortals.Add(workCorridorPortals[index]);
            }

            // 宏观路点由纠正后的起点、各通道代表格子和终点组成
            int waypointOffset = hierarchicalWaypointCells.Length;
            AppendUnique(hierarchicalWaypointCells, startCellIndex);
            for (int index = workNodeChain.Length - 1; index >= 0; index--)
            {
                AppendUnique(
                    hierarchicalWaypointCells,
                    grid.PortalNodes[workNodeChain[index]].CellIndex);
            }
            AppendUnique(hierarchicalWaypointCells, endCellIndex);

            // 缓存键使用最终分块序列，不依赖搜索过程中临时生成的父节点链
            uint corridorHash = NavigationGridCorridorSolver.CalculateHash(ref workCorridorClusters);
            uint dynamicOverlaySignature = NavigationFlowFieldCache.CalculateOverlaySignature(
                ref workCorridorClusters,
                dynamicOverlayClusters);
            result.DynamicOverlaySignature = dynamicOverlaySignature;
            int fieldOffset = flowCells.Length;
            int integrationExpandedCellCount = 0;

            // 通道哈希只用于快速筛选，实际命中还会比较完整分块序列
            bool cacheHit = NavigationFlowFieldCache.TryAppendCachedField(
                endCellIndex,
                requiredClearance,
                request.ClearancePenaltyWeight,
                corridorHash,
                cacheVersion,
                dynamicOverlaySignature,
                ref workCorridorClusters,
                ref cacheEntries,
                ref cacheCorridorClusters,
                ref cacheFlowCells,
                ref flowCells);
            int integrationGeneration = generationStart +
                                        (dynamicOverlayMayBeActive
                                            ? grid.PortalNodes.Length + 2
                                            : 2);
            // 缓存未命中时，才从目标开始在通道内反向生成 Integration Field
            if (!cacheHit)
            {
                if (!NavigationIntegrationFieldSolver.BuildIntegrationField(
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
                        out integrationExpandedCellCount,
                        dynamicOverlay))
                {
                    // Flow Field 构建失败时，将通道输出恢复到本请求开始前的长度
                    corridorClusters.ResizeUninitialized(corridorClusterOffset);
                    corridorPortals.ResizeUninitialized(corridorPortalOffset);
                    // 路点和 Flow Field 也一起撤销，避免共享列表中留下残缺结果
                    hierarchicalWaypointCells.ResizeUninitialized(waypointOffset);
                    flowCells.ResizeUninitialized(fieldOffset);
                    // 撤销完成后再返回，下一条请求即可安全复用这些列表
                    result.FailureReason = NavigationPathFailureReason.NoPath;
                    return result;
                }

                // 稀疏流向场按 CellIndex 固定排序，万人移动可以二分读取且缓存内容保持确定
                int builtFieldCount = flowCells.Length - fieldOffset;
                var fieldSlice = new NativeSlice<NavigationFlowFieldCell>(
                    flowCells.AsArray(),
                    fieldOffset,
                    builtFieldCount);
                fieldSlice.Sort(new NavigationFlowFieldCellIndexComparer());

                NavigationFlowFieldCache.AddCachedField(
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
                    ref cacheFlowCells,
                    dynamicOverlaySignature);
            }

            // 新建结果可直接读取临时成本；缓存命中时则在已复制的稀疏 Flow Field 中查找
            float totalCost = 0f;
            if (!cacheHit && cellGenerations[startCellIndex] == integrationGeneration)
            {
                totalCost = cellCosts[startCellIndex];
            }
            else
            {
                NavigationIntegrationFieldSolver.TryGetIntegrationCost(
                    ref flowCells,
                    fieldOffset,
                    flowCells.Length - fieldOffset,
                    startCellIndex,
                    out totalCost);
            }

            // 所有输出片段都完整生成后，才将请求标记为成功
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

        private static NavigationFlowFieldJobResult BuildGoalRegionField(
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            int startCellIndex,
            int endCellIndex,
            int generationStart,
            ref NavigationFlowFieldJobResult result,
            ref NativeList<int> corridorClusters,
            ref NativeList<int> corridorPortals,
            ref NativeList<int> hierarchicalWaypointCells,
            ref NativeList<NavigationFlowFieldCell> flowCells,
            NativeArray<float> cellCosts,
            NativeArray<int> cellHeap,
            NativeArray<int> cellHeapPositions,
            NativeArray<int> cellGenerations,
            NativeArray<int> clusterGenerations,
            ref NativeList<int> workVisitedCells,
            ref NativeList<int> workCoverageClusters,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            NativeArray<NavigationDynamicOverlayCluster> dynamicOverlayClusters)
        {
            int coverageGeneration = generationStart;
            int targetRegionId = grid.Cells[endCellIndex].RegionId;
            workCoverageClusters.Clear();

            // Cluster 只要包含目标静态 Region 的 Cell 就进入覆盖集合，实际可走范围由反向搜索裁定
            for (int cellIndex = 0; cellIndex < grid.Cells.Length; cellIndex++)
            {
                NavigationGridCell cell = grid.Cells[cellIndex];
                int clusterIndex = cell.ClusterId;
                if (cell.RegionId != targetRegionId ||
                    clusterIndex < 0 ||
                    clusterIndex >= clusterGenerations.Length ||
                    clusterGenerations[clusterIndex] == coverageGeneration)
                {
                    continue;
                }

                clusterGenerations[clusterIndex] = coverageGeneration;
                workCoverageClusters.Add(clusterIndex);
            }

            int clusterOffset = corridorClusters.Length;
            for (int index = 0; index < workCoverageClusters.Length; index++)
            {
                corridorClusters.Add(workCoverageClusters[index]);
            }

            int portalOffset = corridorPortals.Length;
            int waypointOffset = hierarchicalWaypointCells.Length;
            hierarchicalWaypointCells.Add(endCellIndex);
            int fieldOffset = flowCells.Length;
            int integrationGeneration = generationStart + 1;

            // 共享场不能因构建代表被动态障碍隔开而整体失败，发布时会逐请求检查起点是否在场内
            if (!NavigationIntegrationFieldSolver.BuildIntegrationField(
                    ref grid,
                    startCellIndex,
                    endCellIndex,
                    request,
                    integrationGeneration,
                    ref workCoverageClusters,
                    cellCosts,
                    cellHeap,
                    cellHeapPositions,
                    cellGenerations,
                    clusterGenerations,
                    ref workVisitedCells,
                    ref flowCells,
                    out int integrationExpandedCellCount,
                    dynamicOverlay,
                    requireStartReachable: false))
            {
                corridorClusters.ResizeUninitialized(clusterOffset);
                hierarchicalWaypointCells.ResizeUninitialized(waypointOffset);
                flowCells.ResizeUninitialized(fieldOffset);
                result.FailureReason = NavigationPathFailureReason.NoPath;
                return result;
            }

            int fieldCount = flowCells.Length - fieldOffset;
            var fieldSlice = new NativeSlice<NavigationFlowFieldCell>(
                flowCells.AsArray(),
                fieldOffset,
                fieldCount);
            fieldSlice.Sort(new NavigationFlowFieldCellIndexComparer());

            float totalCost = 0f;
            if (cellGenerations[startCellIndex] == integrationGeneration)
            {
                totalCost = cellCosts[startCellIndex];
            }

            result.Status = NavigationPathStatus.Succeeded;
            result.FailureReason = NavigationPathFailureReason.None;
            result.CorridorClusterOffset = clusterOffset;
            result.CorridorClusterCount = workCoverageClusters.Length;
            result.CorridorPortalOffset = portalOffset;
            result.CorridorPortalCount = 0;
            result.HierarchicalWaypointOffset = waypointOffset;
            result.HierarchicalWaypointCount = 1;
            result.FieldOffset = fieldOffset;
            result.FieldCount = fieldCount;
            result.AbstractExpandedNodeCount = 0;
            result.IntegrationExpandedCellCount = integrationExpandedCellCount;
            result.TotalCost = totalCost;
            result.CacheHit = 0;
            result.DynamicOverlaySignature = NavigationFlowFieldCache.CalculateOverlaySignature(
                ref workCoverageClusters,
                dynamicOverlayClusters);
            return result;
        }

        internal static NavigationFlowFieldJobResult CreateFailureResult(
            Entity entity,
            uint requestVersion,
            NavigationPathFailureReason failureReason,
            uint cacheVersion)
        {
            // 失败结果使用负的切片起点；调度系统写回前仍会检查所有范围
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

        private struct NavigationFlowFieldCellIndexComparer : IComparer<NavigationFlowFieldCell>
        {
            public int Compare(NavigationFlowFieldCell left, NavigationFlowFieldCell right)
            {
                return left.CellIndex.CompareTo(right.CellIndex);
            }
        }

        private static void AppendUnique(NativeList<int> values, int value)
        {
            // 只删除连续重复的路点，不会重排路线真实经过的顺序
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
            // 临时数组可以比当前需求更大，但不能小于格子、分块或节点的实际索引范围
            return NavigationGridTraversal.IsGridShapeValid(ref grid) &&
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

    }
}
