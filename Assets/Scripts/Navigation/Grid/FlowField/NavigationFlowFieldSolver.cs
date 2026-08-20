using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 协调 HPA Corridor、Integration Field 和 Cache 的纯数据构建流程
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
            // 先初始化失败结果，后续分支只覆盖更具体的失败原因
            NavigationFlowFieldJobResult result = CreateFailureResult(
                jobRequest.Entity,
                request.Version,
                NavigationPathFailureReason.InvalidRequest,
                cacheVersion);
            result.DynamicOverlayVersion = dynamicOverlayVersion;
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
                !NavigationGridQuery.IsRequestValid(request))
            {
                return result;
            }

            // 起点投影失败时保留默认的负 Cell 索引
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
            // 终点使用同一体型和最大投影半径，避免两端规则不一致
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
            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
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
                         dynamicOverlay))
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
            uint corridorHash = NavigationGridCorridorSolver.CalculateHash(ref workCorridorClusters);
            uint dynamicOverlaySignature = NavigationFlowFieldCache.CalculateOverlaySignature(
                ref workCorridorClusters,
                dynamicOverlayClusters);
            int fieldOffset = flowCells.Length;
            int integrationExpandedCellCount = 0;

            // Corridor Hash 只作缓存初筛，命中函数还会比较完整 Cluster 序列
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
            int integrationGeneration = generationStart + 2;
            // 只有缓存未命中时才在 Corridor 内反向生成 Integration Field
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

            // 新建 Field 可直接读取 Scratch，缓存命中则从复制后的稀疏 Field 查询
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
