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
            // Query 固定系统读取的 Grid 和完整请求实体形状
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
            // Persistent 工作列表和缓存由当前 World 的 System 独占
            _cacheEntries = new NativeList<NavigationFlowFieldCacheEntry>(64, Allocator.Persistent);
            // Corridor 和 Field 缓存分别保存键切片和值切片
            _cacheCorridorClusters = new NativeList<int>(128, Allocator.Persistent);
            _cacheFlowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.Persistent);
            // 工作列表跨请求复用，但每次 Build 都会清除逻辑长度
            _workVisitedCells = new NativeList<int>(256, Allocator.Persistent);
            _workCorridorClusters = new NativeList<int>(16, Allocator.Persistent);
            _workCorridorPortals = new NativeList<int>(16, Allocator.Persistent);
            _workNodeChain = new NativeList<int>(32, Allocator.Persistent);
        }

        public void OnUpdate(ref SystemState state)
        {
            // 活动批次优先于新请求处理
            if (_activeJobScheduled)
            {
                if (!_activeJobHandle.IsCompleted)
                {
                    // 未完成时保留 Persistent 输入输出，避免主线程同步等待
                    return;
                }

                // Handle 完成后才能读取共享输出并释放批次容器
                _activeJobHandle.Complete();
                // 写回发生在释放活动批次容器之前
                ApplyActiveResults(ref state);
                SetFlowJobActivity(ref state, false);
                DisposeActiveBatch();
                // 留出一个无读者 Tick 让 Overlay 优先发布，避免持续请求使差量长期饥饿
                return;
            }

            if (_requestQuery.IsEmptyIgnoreFilter)
            {
                // 没有请求时不读取 Grid 单例
                return;
            }

            int gridCount = _gridQuery.CalculateEntityCount();
            if (gridCount == 0)
            {
                // 场景 Grid 尚未加载时保留 Pending 请求
                return;
            }

            if (gridCount != 1)
            {
                // World 内多个 Grid 会让缓存版本和 Cell 索引失去唯一语义，必须整体拒绝
                FailPendingRequests(ref state, NavigationPathFailureReason.InvalidGrid);
                return;
            }

            // 唯一 Grid 的 Blob 引用是本批次所有索引的共同命名空间
            Entity gridEntity = _gridQuery.GetSingletonEntity();
            NavigationGridReference gridReference = state.EntityManager.GetComponentData<
                NavigationGridReference>(gridEntity);
            // 实体存在但 Blob 未创建同样属于无效 Grid
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
                // 差量等待发布时不启动新读者，Overlay 将在所有活动批次退出后优先更新
                return;
            }

            // Grid Hash 变化必须先让旧缓存失效
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
            // 当前没有活动批次，调度后共享 Scratch 仍只有一个写入者
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
                // World 销毁是唯一允许主动等待未完成 Job 的路径
                _activeJobHandle.Complete();
                SetFlowJobActivity(ref state, false);
            }

            // 先释放批次和 Scratch，再释放跨批次缓存与工作列表
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
                // DataHash 覆盖 Cell 和分层数据，变化后旧 Field 的所有索引与成本均不可复用
                _cacheGridHash = dataHash;
                ClearCache();
                // 版本递增让仍携带旧版本的结果无法伪装成缓存命中
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
                // 缓存采用整代回收而非切片搬移，保证活动 Job 持有的偏移在批次内稳定
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
            // Query 还包含 Searching 和终态实体，只收集 Pending 请求
            for (int index = 0; index < requestEntities.Length; index++)
            {
                Entity entity = requestEntities[index];
                if (state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity).Status ==
                    NavigationPathStatus.Pending)
                {
                    pendingEntities.Add(entity);
                }
            }

            // 没有新增版本时不创建空 Job
            if (pendingEntities.IsEmpty)
            {
                return;
            }

            // Entity Index 和 Version 排序固定请求顺序，进而固定共享输出切片与缓存版本分配
            SortEntities(pendingEntities);

            // 单批上限同时限制 Job 独占时间和下一帧的主线程写回量
            int batchCount = math.min(MaximumRequestsPerBatch, pendingEntities.Length);
            // Scratch 只按 Grid 拓扑扩容，不随请求数量重新分配
            EnsureScratchCapacity(
                grid.Value.Cells.Length,
                grid.Value.Clusters.Length,
                grid.Value.PortalNodes.Length);
            EnsureGenerationCapacity(batchCount * 4);
            _activeGrid = grid;
            _activeOverlay = overlay.IsCreated ? overlay.AsNativeArray() : default;
            _activeOverlayClusters = overlayClusters.IsCreated
                ? overlayClusters.AsNativeArray()
                : default;
            _activeOverlayVersion = overlayVersion;
            // 请求和结果数组使用相同下标形成固定一一对应
            _activeRequests = new NativeArray<NavigationFlowFieldJobRequest>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _activeResults = new NativeArray<NavigationFlowFieldJobResult>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            // 输出列表容量只是初始估计，NativeList 仍可按实际 Corridor 增长
            _activeCorridorClusters = new NativeList<int>(batchCount * 8, Allocator.Persistent);
            _activeCorridorPortals = new NativeList<int>(batchCount * 8, Allocator.Persistent);
            _activeWaypointCells = new NativeList<int>(batchCount * 16, Allocator.Persistent);
            // Field 初始容量按每请求一个 Cluster 估算，跨 Cluster 时允许扩容
            _activeFlowCells = new NativeList<NavigationFlowFieldCell>(
                math.max(64, batchCount * grid.Value.ClusterSizeInCells * grid.Value.ClusterSizeInCells),
                Allocator.Persistent);

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                Entity entity = pendingEntities[batchIndex];
                // 调度前复制请求，Job 不再读取可能变化的 ECS 组件
                NavigationFlowFieldRequest request =
                    state.EntityManager.GetComponentData<NavigationFlowFieldRequest>(entity);
                _activeRequests[batchIndex] = new NavigationFlowFieldJobRequest
                {
                    Entity = entity,
                    Request = request,
                };
                NavigationFlowFieldState fieldState =
                    state.EntityManager.GetComponentData<NavigationFlowFieldState>(entity);
                // 捕获版本并切换 Searching，写回时据此拒绝过期结果
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
                // 清除上一版本 Buffer，Searching 状态不暴露旧路径数据
                ClearResultBuffers(state.EntityManager, entity);
            }

            // Job 取得共享输出与 Scratch 的唯一写权限
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
            // 每个请求使用四个连续 Generation，批次间不得重叠
            _nextGeneration += batchCount * 4;
            // 私有句柄负责跨 Tick 轮询，System Dependency 同时让 ECS 在结构变化前等待读取者
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
                // Job 运行期间实体可能已经销毁或改变组件形状
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
                    // 实体复用、取消或新请求覆盖后都不能把旧批次结果写入当前 Buffer
                    continue;
                }

                // 访问共享列表前必须先验证全部输出切片边界
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
                // 无论成功或失败都先清除可能存在的旧 Buffer
                ClearResultBuffers(state.EntityManager, entity);
                // 只有四类切片都有效时才写回成功结果
                if (result.Status == NavigationPathStatus.Succeeded && validRanges)
                {
                    // Cluster Buffer 保存宏观路线覆盖范围
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

                    // Portal Buffer 保存相邻 Cluster 之间的实际跨越顺序
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

                    // 宏观路点在写回时由 CellIndex 转换为世界位置
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

                    // 稀疏 Field 保留 Job 计算的 CellIndex、成本和方向
                    DynamicBuffer<NavigationFlowFieldCell> field =
                        state.EntityManager.GetBuffer<NavigationFlowFieldCell>(entity);
                    for (int index = 0; index < result.FieldCount; index++)
                    {
                        field.Add(_activeFlowCells[result.FieldOffset + index]);
                    }
                }

                // Buffer 全部写完后再提交终态，避免观察到半写入结果
                fieldState.Status = validRanges ? result.Status : NavigationPathStatus.Failed;
                // 损坏切片统一映射为 InvalidGrid，不保留部分结果
                fieldState.FailureReason = validRanges
                    ? result.FailureReason
                    : NavigationPathFailureReason.InvalidGrid;
                // 统计字段和缓存版本与终态一起提交
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
                // Searching 请求由活动批次持有，这里只终止尚未认领的 Pending 请求
                if (fieldState.Status != NavigationPathStatus.Pending)
                {
                    continue;
                }

                // Pending 失败不改变 RequestVersion，调用方仍能对应原请求
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
            // 请求数量不影响 Scratch，形状一致时直接复用
            if (_scratchCellCount == cellCount &&
                _scratchClusterCount == clusterCount &&
                _scratchNodeCount == nodeCount &&
                _cellCosts.IsCreated)
            {
                return;
            }

            // 先释放旧拓扑对应的数组，再按新形状分配
            DisposeScratch();

            // Cell 成本、堆和 Generation 共享同一 Cell 索引空间
            _cellCosts = new NativeArray<float>(cellCount, Allocator.Persistent);
            _cellHeap = new NativeArray<int>(cellCount, Allocator.Persistent);
            _cellHeapPositions = new NativeArray<int>(cellCount, Allocator.Persistent);
            _cellGenerations = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            // Cluster Generation 只用于标记当前 Corridor 成员
            _clusterGenerations = new NativeArray<int>(
                clusterCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            // 抽象搜索数组按 Portal Node 数量分配
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
            // Grid 形状变化后旧缓存中的所有 Cell 和 Node 索引都失效
            ClearCache();
        }

        private void EnsureGenerationCapacity(int requiredGenerations)
        {
            // 正常路径只推进计数器，不清空与 Grid 等长的标记数组
            if (_nextGeneration > 0 && _nextGeneration <= int.MaxValue - requiredGenerations)
            {
                return;
            }

            // 即将回绕时清零标记，避免旧 Generation 再次变为有效值
            for (int index = 0; index < _cellGenerations.Length; index++)
            {
                _cellGenerations[index] = 0;
            }
            for (int index = 0; index < _clusterGenerations.Length; index++)
            {
                // Cluster 和 Cell 使用独立标记数组，必须同时重置
                _clusterGenerations[index] = 0;
            }
            for (int index = 0; index < _abstractGenerations.Length; index++)
            {
                // Portal Node 标记也参与同一全局 Generation 计数
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
            // 空切片允许负一偏移，非空切片必须完整落在列表内
            return count == 0
                ? offset >= -1
                : offset >= 0 && count > 0 && offset + count <= totalLength;
        }

        private static void SortEntities(NativeList<Entity> entities)
        {
            // 批次很小，插入排序避免额外容器并保持确定性
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
            // 活动批次容器只在 Job 完成或 World 销毁后释放
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
                    // Corridor/Field 切片允许保留在尾部，缓存元数据移除后不会再被读取
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
            // Scratch 数组没有独立 Job 句柄，释放前由调用方保证批次已完成
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
