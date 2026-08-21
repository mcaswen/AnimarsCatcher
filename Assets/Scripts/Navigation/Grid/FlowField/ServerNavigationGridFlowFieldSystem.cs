using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在 Server 或本地 World 异步处理 HPA 星 Corridor 与局部 Flow Field 请求
    /// </summary>
    [WorldSystemFilter(
        WorldSystemFilterFlags.ServerSimulation |
        WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    public partial struct ServerNavigationGridFlowFieldSystem : ISystem
    {
        private const int MaximumRequestsPerBatch = 16;
        private EntityQuery _gridQuery;
        private EntityQuery _requestQuery;
        private JobHandle _activeJobHandle;
        private bool _activeJobScheduled;
        private BlobAssetReference<NavigationGridBlob> _activeGrid;
        private NativeArray<NavigationFlowFieldJobRequest> _activeRequests;
        private NativeArray<NavigationFlowFieldJobResult> _activeResults;
        private NativeArray<NavigationDynamicOverlayCell> _activeOverlay;
        private NativeArray<NavigationDynamicOverlayCluster> _activeOverlayClusters;
        private uint _activeOverlayVersion;
        private NativeList<int> _activeCorridorClusters;
        private NativeList<int> _activeCorridorPortals;
        private NativeList<int> _activeWaypointCells;
        private NativeList<NavigationFlowFieldCell> _activeFlowCells;

        private NativeArray<float> _cellCosts;
        private NativeArray<int> _cellHeap;
        private NativeArray<int> _cellHeapPositions;
        private NativeArray<int> _cellGenerations;
        private NativeArray<int> _clusterGenerations;
        private NativeArray<float> _abstractCosts;
        private NativeArray<float> _abstractEndCosts;
        private NativeArray<int> _abstractParents;
        private NativeArray<int> _abstractHeap;
        private NativeArray<int> _abstractHeapPositions;
        private NativeArray<int> _abstractGenerations;
        private NativeList<int> _workVisitedCells;
        private NativeList<int> _workCorridorClusters;
        private NativeList<int> _workCorridorPortals;
        private NativeList<int> _workNodeChain;

        private NativeList<NavigationFlowFieldCacheEntry> _cacheEntries;
        private NativeList<int> _cacheCorridorClusters;
        private NativeList<NavigationFlowFieldCell> _cacheFlowCells;
        private Unity.Entities.Hash128 _cacheGridHash;
        private uint _cacheOverlayVersion;
        private uint _cacheVersion;
        private int _scratchCellCount;
        private int _scratchClusterCount;
        private int _scratchNodeCount;
        private int _nextGeneration;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            // Query 固定系统读取的 Grid 和完整请求 Entity 形状
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            _requestQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationFlowFieldRequest>(),
                ComponentType.ReadWrite<NavigationFlowFieldState>(),
                ComponentType.ReadWrite<NavigationCorridorCluster>(),
                ComponentType.ReadWrite<NavigationCorridorPortal>(),
                ComponentType.ReadWrite<NavigationHierarchicalWaypoint>(),
                ComponentType.ReadWrite<NavigationFlowFieldCell>());
            _nextGeneration = 1;
            _cacheVersion = 1;
            // 可跨帧复用的工作列表和缓存只属于当前 World 中的这个系统
            _cacheEntries = new NativeList<NavigationFlowFieldCacheEntry>(64, Allocator.Persistent);
            // 缓存将通道分块和 Flow Field 数据分别连续保存
            _cacheCorridorClusters = new NativeList<int>(128, Allocator.Persistent);
            _cacheFlowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.Persistent);
            // 临时列表在请求之间复用，每次构建前都会清空现有内容
            _workVisitedCells = new NativeList<int>(256, Allocator.Persistent);
            _workCorridorClusters = new NativeList<int>(16, Allocator.Persistent);
            _workCorridorPortals = new NativeList<int>(16, Allocator.Persistent);
            _workNodeChain = new NativeList<int>(32, Allocator.Persistent);
        }

        public void OnUpdate(ref SystemState state)
        {
            // 先检查正在运行的批次，再考虑调度新请求
            if (_activeJobScheduled)
            {
                if (!_activeJobHandle.IsCompleted)
                {
                    // 后台任务未完成时保留所有输入输出到下一帧，不阻塞主线程等待
                    return;
                }

                // 只有任务完成后才能读取输出或释放本批容器
                _activeJobHandle.Complete();
                // 先将结果写回 ECS，再释放活动批次的容器
                ApplyActiveResults(ref state);
                SetFlowJobActivity(ref state, false);
                DisposeActiveBatch();
                // 每批完成后留一帧给动态障碍层更新，避免连续请求让它一直无法写入
                return;
            }

            if (_requestQuery.IsEmptyIgnoreFilter)
            {
                // 没有等待处理的请求时，不必查询导航网格单例
                return;
            }

            int gridCount = _gridQuery.CalculateEntityCount();
            if (gridCount == 0)
            {
                // 场景导航网格尚未加载时保留 Pending，请求稍后可以继续执行
                return;
            }

            if (gridCount != 1)
            {
                // 同一 World 出现多张导航网格时，格子索引和缓存版本无法确定归属，因此拒绝本轮请求
                FailPendingRequests(ref state, NavigationPathFailureReason.InvalidGrid);
                return;
            }

            // 本批所有格子和节点索引都以这张唯一导航网格为准
            Entity gridEntity = _gridQuery.GetSingletonEntity();
            NavigationGridReference gridReference = state.EntityManager.GetComponentData<
                NavigationGridReference>(gridEntity);
            // Entity 存在但 Blob 未创建同样属于无效 Grid
            if (!gridReference.Value.IsCreated)
            {
                FailPendingRequests(ref state, NavigationPathFailureReason.InvalidGrid);
                return;
            }

            if (state.EntityManager.HasBuffer<NavigationDynamicOverlayDelta>(gridEntity) &&
                !state.EntityManager.GetBuffer<NavigationDynamicOverlayDelta>(
                    gridEntity,
                    isReadOnly: true).IsEmpty)
            {
                // 有动态障碍变化等待写入时不启动新任务，让障碍层先更新
                return;
            }

            // 导航网格内容哈希变化后，先清空旧缓存再处理请求
            NavigationDynamicOverlayState overlayState =
                state.EntityManager.HasComponent<NavigationDynamicOverlayState>(gridEntity)
                    ? state.EntityManager.GetComponentData<NavigationDynamicOverlayState>(gridEntity)
                    : new NavigationDynamicOverlayState { Version = 1 };
            DynamicBuffer<NavigationDynamicOverlayCell> overlay =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(
                        gridEntity,
                        isReadOnly: true)
                    : default;
            DynamicBuffer<NavigationDynamicOverlayCluster> overlayClusters =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCluster>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCluster>(
                        gridEntity,
                        isReadOnly: true)
                    : default;

            RefreshCacheForGrid(
                gridReference.Value,
                overlayClusters.IsCreated ? overlayClusters.AsNativeArray() : default,
                overlayState.Version);
            // 只有在没有活动批次时才调度，因此共享临时数组始终只有一个写入任务
            SchedulePendingRequests(
                ref state,
                gridReference.Value,
                overlay,
                overlayClusters,
                overlayState.Version);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_activeJobScheduled)
            {
                // 系统销毁时必须主动等待未完成任务，确保可以安全释放 Native 容器
                _activeJobHandle.Complete();
                SetFlowJobActivity(ref state, false);
            }

            // 先释放当前批次和临时数组，再清理跨批次缓存与工作列表
            DisposeActiveBatch();
            DisposeScratch();
            if (_cacheEntries.IsCreated) _cacheEntries.Dispose();
            if (_cacheCorridorClusters.IsCreated) _cacheCorridorClusters.Dispose();
            if (_cacheFlowCells.IsCreated) _cacheFlowCells.Dispose();
            if (_workVisitedCells.IsCreated) _workVisitedCells.Dispose();
            if (_workCorridorClusters.IsCreated) _workCorridorClusters.Dispose();
            if (_workCorridorPortals.IsCreated) _workCorridorPortals.Dispose();
            if (_workNodeChain.IsCreated) _workNodeChain.Dispose();
        }

        private void RefreshCacheForGrid(
            BlobAssetReference<NavigationGridBlob> grid,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            uint dynamicOverlayVersion)
        {
            Unity.Entities.Hash128 dataHash = grid.Value.DataHash;
            if (_scratchCellCount == 0 || !_cacheGridHash.Equals(dataHash))
            {
                // DataHash 覆盖格子和分层数据；一旦变化，旧 Flow Field 的索引与成本都不能复用
                _cacheGridHash = dataHash;
                ClearCache();
                // 清理缓存时递增版本，旧结果不会被误认为当前缓存命中
                _cacheVersion++;
                if (_cacheVersion == 0)
                {
                    _cacheVersion = 1;
                }
            }

            if (_cacheOverlayVersion != dynamicOverlayVersion)
            {
                _cacheOverlayVersion = dynamicOverlayVersion;
                InvalidateChangedOverlayEntries(overlayClusters);
            }
            else if (_cacheEntries.Length >= 64 ||
                     _cacheFlowCells.Length > math.max(256, grid.Value.Cells.Length * 4))
            {
                // 缓存整代清空而不搬移数据，后台任务持有的切片起点在整个批次内保持不变
                ClearCache();
                _cacheVersion++;
            }
        }

        private void SchedulePendingRequests(
            ref SystemState state,
            BlobAssetReference<NavigationGridBlob> grid,
            DynamicBuffer<NavigationDynamicOverlayCell> overlay,
            DynamicBuffer<NavigationDynamicOverlayCluster> overlayClusters,
            uint overlayVersion)
        {
            using NativeArray<Entity> requestEntities = _requestQuery.ToEntityArray(Allocator.Temp);
            using var pendingEntities = new NativeList<Entity>(requestEntities.Length, Allocator.Temp);
            // Query 还会返回 Searching 和已经结束的 Entity，这里只收集 Pending 请求
            for (int index = 0; index < requestEntities.Length; index++)
            {
                Entity entity = requestEntities[index];
                if (state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity).Status ==
                    NavigationPathStatus.Pending)
                {
                    pendingEntities.Add(entity);
                }
            }

            // 没有等待请求时不创建空的后台任务
            if (pendingEntities.IsEmpty)
            {
                return;
            }

            // 按 Entity 的 Index 和 Version 排序，固定请求处理顺序、输出切片位置和缓存版本分配
            SortEntities(pendingEntities);

            // 单批数量上限同时控制后台任务耗时和下一帧主线程写回工作量
            int batchCount = math.min(MaximumRequestsPerBatch, pendingEntities.Length);
            // 临时数组只按导航网格尺寸扩容，不随每批请求数重新分配
            EnsureScratchCapacity(
                grid.Value.Cells.Length,
                grid.Value.Clusters.Length,
                grid.Value.PortalNodes.Length);
            int generationStride = NavigationGridFlowFieldJob.CalculateGenerationStride(
                grid.Value.PortalNodes.Length,
                overlayVersion);
            EnsureGenerationCapacity(batchCount * generationStride);
            _activeGrid = grid;
            _activeOverlay = overlay.IsCreated ? overlay.AsNativeArray() : default;
            _activeOverlayClusters = overlayClusters.IsCreated
                ? overlayClusters.AsNativeArray()
                : default;
            _activeOverlayVersion = overlayVersion;
            // 请求与结果数组使用相同下标，保持一一对应
            _activeRequests = new NativeArray<NavigationFlowFieldJobRequest>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _activeResults = new NativeArray<NavigationFlowFieldJobResult>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            // 输出容量只是预估值，NativeList 会按实际通道长度继续增长
            _activeCorridorClusters = new NativeList<int>(batchCount * 8, Allocator.Persistent);
            _activeCorridorPortals = new NativeList<int>(batchCount * 8, Allocator.Persistent);
            _activeWaypointCells = new NativeList<int>(batchCount * 16, Allocator.Persistent);
            // Flow Field 初始容量按每条请求一个分块估算，跨分块路线可以继续扩容
            _activeFlowCells = new NativeList<NavigationFlowFieldCell>(
                math.max(64, batchCount * grid.Value.ClusterSizeInCells * grid.Value.ClusterSizeInCells),
                Allocator.Persistent);

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                Entity entity = pendingEntities[batchIndex];
                // 调度前复制请求，后台任务不再读取可能变化的 ECS 组件
                NavigationFlowFieldRequest request =
                    state.EntityManager.GetComponentData<NavigationFlowFieldRequest>(entity);
                _activeRequests[batchIndex] = new NavigationFlowFieldJobRequest
                {
                    Entity = entity,
                    Request = request,
                };
                NavigationFlowFieldState fieldState =
                    state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
                // 记录版本并将状态改为 Searching，写回时用版本检查结果是否过期
                fieldState.Status = NavigationPathStatus.Searching;
                fieldState.FailureReason = NavigationPathFailureReason.None;
                fieldState.RequestVersion = request.PathRequest.Version;
                fieldState.ProjectedStartCellIndex = -1;
                fieldState.ProjectedEndCellIndex = -1;
                fieldState.CorridorClusterCount = 0;
                fieldState.CorridorPortalCount = 0;
                fieldState.HierarchicalWaypointCount = 0;
                fieldState.FieldCellCount = 0;
                fieldState.AbstractExpandedNodeCount = 0;
                fieldState.IntegrationExpandedCellCount = 0;
                fieldState.TotalCost = 0f;
                fieldState.CacheHit = 0;
                fieldState.DynamicOverlayVersion = overlayVersion;
                state.EntityManager.SetComponentData(entity, fieldState);
                // 清空上一版本的结果缓冲区，计算期间不会对外暴露旧路线
                ClearResultBuffers(state.EntityManager, entity);
            }

            // 后台任务独占共享输出列表和临时数组的写权限
            var job = new NavigationGridFlowFieldJob
            {
                Grid = grid,
                Requests = _activeRequests,
                Results = _activeResults,
                CorridorClusters = _activeCorridorClusters,
                CorridorPortals = _activeCorridorPortals,
                HierarchicalWaypointCells = _activeWaypointCells,
                FlowCells = _activeFlowCells,
                CellCosts = _cellCosts,
                CellHeap = _cellHeap,
                CellHeapPositions = _cellHeapPositions,
                CellGenerations = _cellGenerations,
                ClusterGenerations = _clusterGenerations,
                AbstractCosts = _abstractCosts,
                AbstractEndCosts = _abstractEndCosts,
                AbstractParents = _abstractParents,
                AbstractHeap = _abstractHeap,
                AbstractHeapPositions = _abstractHeapPositions,
                AbstractGenerations = _abstractGenerations,
                WorkVisitedCells = _workVisitedCells,
                WorkCorridorClusters = _workCorridorClusters,
                WorkCorridorPortals = _workCorridorPortals,
                WorkNodeChain = _workNodeChain,
                CacheEntries = _cacheEntries,
                CacheCorridorClusters = _cacheCorridorClusters,
                CacheFlowCells = _cacheFlowCells,
                CacheVersion = _cacheVersion,
                DynamicOverlay = _activeOverlay,
                DynamicOverlayClusters = _activeOverlayClusters,
                DynamicOverlayVersion = _activeOverlayVersion,
                GenerationStart = _nextGeneration,
            };
            // 动态通道成本可能为每个通道节点使用独立 Generation，不同批次不能重叠使用编号区间
            _nextGeneration += batchCount * generationStride;
            // 私有句柄用于跨帧检查完成状态；System Dependency 保证 ECS 结构变化前等待读取结束
            _activeJobHandle = job.Schedule(state.Dependency);
            state.Dependency = _activeJobHandle;
            _activeJobScheduled = true;
            SetFlowJobActivity(ref state, true);
        }

        private void ApplyActiveResults(ref SystemState state)
        {
            for (int resultIndex = 0; resultIndex < _activeResults.Length; resultIndex++)
            {
                NavigationFlowFieldJobResult result = _activeResults[resultIndex];
                Entity entity = result.Entity;
                // Job 运行期间 Entity 可能已经销毁或改变组件形状
                if (!state.EntityManager.Exists(entity) ||
                    !state.EntityManager.HasComponent<NavigationFlowFieldRequest>(entity) ||
                    !state.EntityManager.HasComponent<NavigationFlowFieldState>(entity))
                {
                    continue;
                }

                NavigationFlowFieldRequest currentRequest =
                    state.EntityManager.GetComponentData<NavigationFlowFieldRequest>(entity);
                NavigationFlowFieldState fieldState =
                    state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
                if (fieldState.Status != NavigationPathStatus.Searching ||
                    fieldState.RequestVersion != result.RequestVersion ||
                    currentRequest.PathRequest.Version != result.RequestVersion)
                {
                    // Entity 复用、取消或新请求覆盖后都不能把旧批次结果写入当前 Buffer
                    continue;
                }

                // 读取共享输出前先检查所有切片范围是否有效
                bool validRanges = IsRangeValid(
                                       result.CorridorClusterOffset,
                                       result.CorridorClusterCount,
                                       _activeCorridorClusters.Length) &&
                                   IsRangeValid(
                                       result.CorridorPortalOffset,
                                       result.CorridorPortalCount,
                                       _activeCorridorPortals.Length) &&
                                   IsRangeValid(
                                       result.HierarchicalWaypointOffset,
                                       result.HierarchicalWaypointCount,
                                       _activeWaypointCells.Length) &&
                                   IsRangeValid(
                                       result.FieldOffset,
                                       result.FieldCount,
                                       _activeFlowCells.Length);
                // 无论本次成功还是失败，都先清空可能残留的旧结果
                ClearResultBuffers(state.EntityManager, entity);
                // 通道分块、入口、宏观路点和 Flow Field 四类切片全部有效时才写回成功结果
                if (result.Status == NavigationPathStatus.Succeeded && validRanges)
                {
                    // 分块缓冲区记录宏观路线经过的范围
                    DynamicBuffer<NavigationCorridorCluster> clusters =
                        state.EntityManager.GetBuffer<NavigationCorridorCluster>(entity);
                    for (int index = 0; index < result.CorridorClusterCount; index++)
                    {
                        clusters.Add(new NavigationCorridorCluster
                        {
                            ClusterId = _activeCorridorClusters[
                                result.CorridorClusterOffset + index],
                        });
                    }

                    // 入口缓冲区记录路线穿过相邻分块的实际顺序
                    DynamicBuffer<NavigationCorridorPortal> portals =
                        state.EntityManager.GetBuffer<NavigationCorridorPortal>(entity);
                    for (int index = 0; index < result.CorridorPortalCount; index++)
                    {
                        portals.Add(new NavigationCorridorPortal
                        {
                            PortalIndex = _activeCorridorPortals[
                                result.CorridorPortalOffset + index],
                        });
                    }

                    // 写回宏观路点时，将格子索引转换为地面世界坐标
                    DynamicBuffer<NavigationHierarchicalWaypoint> waypoints =
                        state.EntityManager.GetBuffer<NavigationHierarchicalWaypoint>(entity);
                    for (int index = 0; index < result.HierarchicalWaypointCount; index++)
                    {
                        int cellIndex = _activeWaypointCells[
                            result.HierarchicalWaypointOffset + index];
                        waypoints.Add(new NavigationHierarchicalWaypoint
                        {
                            CellIndex = cellIndex,
                            Position = NavigationGridQuery.GetCellWorldPosition(
                                ref _activeGrid.Value,
                                cellIndex),
                        });
                    }

                    // 稀疏 Flow Field 原样保留后台任务算出的格子索引、成本和方向
                    DynamicBuffer<NavigationFlowFieldCell> field =
                        state.EntityManager.GetBuffer<NavigationFlowFieldCell>(entity);
                    for (int index = 0; index < result.FieldCount; index++)
                    {
                        field.Add(_activeFlowCells[result.FieldOffset + index]);
                    }
                }

                // 所有缓冲区写完后才更新最终状态，外部不会看到半套结果
                fieldState.Status = validRanges ? result.Status : NavigationPathStatus.Failed;
                // 任一切片损坏都按 InvalidGrid 失败处理，不保留部分结果
                fieldState.FailureReason = validRanges
                    ? result.FailureReason
                    : NavigationPathFailureReason.InvalidGrid;
                // 搜索统计和缓存版本与最终状态一起写回
                fieldState.CacheVersion = result.CacheVersion;
                fieldState.DynamicOverlayVersion = result.DynamicOverlayVersion;
                fieldState.ProjectedStartCellIndex = result.ProjectedStartCellIndex;
                fieldState.ProjectedEndCellIndex = result.ProjectedEndCellIndex;
                fieldState.CorridorClusterCount = validRanges ? result.CorridorClusterCount : 0;
                fieldState.CorridorPortalCount = validRanges ? result.CorridorPortalCount : 0;
                fieldState.HierarchicalWaypointCount = validRanges
                    ? result.HierarchicalWaypointCount
                    : 0;
                fieldState.FieldCellCount = validRanges ? result.FieldCount : 0;
                fieldState.AbstractExpandedNodeCount = result.AbstractExpandedNodeCount;
                fieldState.IntegrationExpandedCellCount = result.IntegrationExpandedCellCount;
                fieldState.TotalCost = result.TotalCost;
                fieldState.CacheHit = result.CacheHit;
                state.EntityManager.SetComponentData(entity, fieldState);
            }
        }

        private void FailPendingRequests(
            ref SystemState state,
            NavigationPathFailureReason failureReason)
        {
            using NativeArray<Entity> entities = _requestQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                Entity entity = entities[index];
                NavigationFlowFieldState fieldState =
                    state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
                // Searching 请求由活动批次负责；这里只终止尚未调度的 Pending 请求
                if (fieldState.Status != NavigationPathStatus.Pending)
                {
                    continue;
                }

                // Pending 请求失败时保留原版本号，调用方仍能对应到原始请求
                fieldState.Status = NavigationPathStatus.Failed;
                fieldState.FailureReason = failureReason;
                fieldState.ProjectedStartCellIndex = -1;
                fieldState.ProjectedEndCellIndex = -1;
                state.EntityManager.SetComponentData(entity, fieldState);
                ClearResultBuffers(state.EntityManager, entity);
            }
        }

        private void EnsureScratchCapacity(int cellCount, int clusterCount, int nodeCount)
        {
            // 临时数组与请求数量无关；导航网格尺寸没变时直接复用
            if (_scratchCellCount == cellCount &&
                _scratchClusterCount == clusterCount &&
                _scratchNodeCount == nodeCount &&
                _cellCosts.IsCreated)
            {
                return;
            }

            // 导航网格尺寸变化时，先释放旧数组，再按新尺寸分配
            DisposeScratch();

            // 格子成本、堆位置和 Generation 都使用同一套格子索引
            _cellCosts = new NativeArray<float>(cellCount, Allocator.Persistent);
            _cellHeap = new NativeArray<int>(cellCount, Allocator.Persistent);
            _cellHeapPositions = new NativeArray<int>(cellCount, Allocator.Persistent);
            _cellGenerations = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            // 分块 Generation 只用来标记当前通道包含哪些分块
            _clusterGenerations = new NativeArray<int>(
                clusterCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            // 抽象搜索数组按通道节点数量分配
            _abstractCosts = new NativeArray<float>(nodeCount, Allocator.Persistent);
            _abstractEndCosts = new NativeArray<float>(nodeCount, Allocator.Persistent);
            _abstractParents = new NativeArray<int>(nodeCount, Allocator.Persistent);
            _abstractHeap = new NativeArray<int>(nodeCount, Allocator.Persistent);
            _abstractHeapPositions = new NativeArray<int>(nodeCount, Allocator.Persistent);
            _abstractGenerations = new NativeArray<int>(
                nodeCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _scratchCellCount = cellCount;
            _scratchClusterCount = clusterCount;
            _scratchNodeCount = nodeCount;
            _nextGeneration = 1;
            // 导航网格尺寸变化后，旧缓存中的格子和节点索引全部失效
            ClearCache();
        }

        private void EnsureGenerationCapacity(int requiredGenerations)
        {
            // 正常情况下只递增 Generation，不必每批清空与整张网格等长的标记数组
            if (_nextGeneration > 0 && _nextGeneration <= int.MaxValue - requiredGenerations)
            {
                return;
            }

            // 计数即将溢出时清空标记，防止旧 Generation 与新请求编号重复
            for (int index = 0; index < _cellGenerations.Length; index++)
            {
                _cellGenerations[index] = 0;
            }
            for (int index = 0; index < _clusterGenerations.Length; index++)
            {
                // 格子和分块使用不同标记数组，溢出时需要一起清空
                _clusterGenerations[index] = 0;
            }
            for (int index = 0; index < _abstractGenerations.Length; index++)
            {
                // 通道节点标记共用同一个 Generation 计数，也要同步清空
                _abstractGenerations[index] = 0;
            }
            _nextGeneration = 1;
        }

        private void ClearCache()
        {
            if (_cacheEntries.IsCreated) _cacheEntries.Clear();
            if (_cacheCorridorClusters.IsCreated) _cacheCorridorClusters.Clear();
            if (_cacheFlowCells.IsCreated) _cacheFlowCells.Clear();
        }

        private static void ClearResultBuffers(EntityManager entityManager, Entity entity)
        {
            entityManager.GetBuffer<NavigationCorridorCluster>(entity).Clear();
            entityManager.GetBuffer<NavigationCorridorPortal>(entity).Clear();
            entityManager.GetBuffer<NavigationHierarchicalWaypoint>(entity).Clear();
            entityManager.GetBuffer<NavigationFlowFieldCell>(entity).Clear();
        }

        private static bool IsRangeValid(int offset, int count, int totalLength)
        {
            // 空切片可以使用 -1 作为起点；非空切片必须完整位于列表范围内
            return count == 0
                ? offset >= -1
                : offset >= 0 && count > 0 && offset + count <= totalLength;
        }

        private static void SortEntities(NativeList<Entity> entities)
        {
            // 单批请求数较小，使用原地插入排序可以避免额外容器并固定顺序
            for (int index = 1; index < entities.Length; index++)
            {
                Entity value = entities[index];
                int insertionIndex = index - 1;
                while (insertionIndex >= 0 && IsEntityAfter(entities[insertionIndex], value))
                {
                    entities[insertionIndex + 1] = entities[insertionIndex];
                    insertionIndex--;
                }
                entities[insertionIndex + 1] = value;
            }
        }

        private static bool IsEntityAfter(Entity left, Entity right)
        {
            return left.Index > right.Index ||
                   (left.Index == right.Index && left.Version > right.Version);
        }

        private void DisposeActiveBatch()
        {
            // 活动批次容器只能在后台任务完成或 World 销毁后释放
            if (_activeRequests.IsCreated) _activeRequests.Dispose();
            if (_activeResults.IsCreated) _activeResults.Dispose();
            if (_activeCorridorClusters.IsCreated) _activeCorridorClusters.Dispose();
            if (_activeCorridorPortals.IsCreated) _activeCorridorPortals.Dispose();
            if (_activeWaypointCells.IsCreated) _activeWaypointCells.Dispose();
            if (_activeFlowCells.IsCreated) _activeFlowCells.Dispose();
            _activeRequests = default;
            _activeResults = default;
            _activeCorridorClusters = default;
            _activeCorridorPortals = default;
            _activeWaypointCells = default;
            _activeFlowCells = default;
            _activeOverlay = default;
            _activeOverlayClusters = default;
            _activeOverlayVersion = 0;
            _activeGrid = default;
            _activeJobHandle = default;
            _activeJobScheduled = false;
        }

        private void InvalidateChangedOverlayEntries(
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            if (!_cacheEntries.IsCreated || !_cacheCorridorClusters.IsCreated)
            {
                return;
            }

            for (int entryIndex = _cacheEntries.Length - 1; entryIndex >= 0; entryIndex--)
            {
                NavigationFlowFieldCacheEntry entry = _cacheEntries[entryIndex];
                if (entry.CorridorOffset < 0 ||
                    entry.CorridorCount < 0 ||
                    entry.CorridorOffset + entry.CorridorCount > _cacheCorridorClusters.Length ||
                    CalculateOverlaySignature(entry, overlayClusters) !=
                    entry.DynamicOverlaySignature)
                {
                    // 删除缓存索引后，尾部残留的通道和 Flow Field 数据不会再被读取，换代时会统一回收
                    _cacheEntries.RemoveAtSwapBack(entryIndex);
                }
            }
        }

        private uint CalculateOverlaySignature(
            NavigationFlowFieldCacheEntry entry,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            uint hash = 2166136261u;
            for (int index = 0; index < entry.CorridorCount; index++)
            {
                int clusterIndex = _cacheCorridorClusters[entry.CorridorOffset + index];
                hash ^= (uint)clusterIndex;
                hash *= 16777619u;
                uint version = overlayClusters.IsCreated &&
                               clusterIndex >= 0 &&
                               clusterIndex < overlayClusters.Length
                    ? overlayClusters[clusterIndex].Version
                    : 0u;
                hash ^= version;
                hash *= 16777619u;
            }

            return hash == 0u ? 1u : hash;
        }

        private void SetFlowJobActivity(ref SystemState state, bool active)
        {
            if (_gridQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity gridEntity = _gridQuery.GetSingletonEntity();
            if (!state.EntityManager.HasComponent<NavigationGridJobActivity>(gridEntity))
            {
                return;
            }

            NavigationGridJobActivity activity = state.EntityManager.GetComponentData<
                NavigationGridJobActivity>(gridEntity);
            activity.FlowFieldJobActive = active ? (byte)1 : (byte)0;
            state.EntityManager.SetComponentData(gridEntity, activity);
        }

        private void DisposeScratch()
        {
            // 临时数组没有单独的任务句柄，调用方必须确认活动批次结束后再释放
            if (_cellCosts.IsCreated) _cellCosts.Dispose();
            if (_cellHeap.IsCreated) _cellHeap.Dispose();
            if (_cellHeapPositions.IsCreated) _cellHeapPositions.Dispose();
            if (_cellGenerations.IsCreated) _cellGenerations.Dispose();
            if (_clusterGenerations.IsCreated) _clusterGenerations.Dispose();
            if (_abstractCosts.IsCreated) _abstractCosts.Dispose();
            if (_abstractEndCosts.IsCreated) _abstractEndCosts.Dispose();
            if (_abstractParents.IsCreated) _abstractParents.Dispose();
            if (_abstractHeap.IsCreated) _abstractHeap.Dispose();
            if (_abstractHeapPositions.IsCreated) _abstractHeapPositions.Dispose();
            if (_abstractGenerations.IsCreated) _abstractGenerations.Dispose();
            _cellCosts = default;
            _cellHeap = default;
            _cellHeapPositions = default;
            _cellGenerations = default;
            _clusterGenerations = default;
            _abstractCosts = default;
            _abstractEndCosts = default;
            _abstractParents = default;
            _abstractHeap = default;
            _abstractHeapPositions = default;
            _abstractGenerations = default;
            _scratchCellCount = 0;
            _scratchClusterCount = 0;
            _scratchNodeCount = 0;
        }
    }
}
