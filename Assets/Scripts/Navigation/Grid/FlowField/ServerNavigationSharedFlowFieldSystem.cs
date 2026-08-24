using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 为正式 Cohort 归并请求、并行构建共享 Field，并按稳定顺序发布 Handle
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniMovementCohortPathRequestSystem))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    public partial struct ServerNavigationSharedFlowFieldSystem : ISystem
    {
        private const int MaximumSupportedConcurrentBuilds = 8;
        private const int MaximumWaitSamples = 8192;

        private EntityQuery _gridQuery;
        private EntityQuery _cohortQuery;
        private EntityQuery _recordQuery;
        private Entity _storeEntity;
        private Unity.Entities.Hash128 _storeGridHash;
        private Unity.Entities.Hash128 _activeGridHash;
        private uint _nextRecordVersion;
        private int _nextGeneration;

        private JobHandle _activeJobHandle;
        private bool _activeJobScheduled;
        private NativeArray<NavigationSharedFlowFieldBuildRequest> _activeRequests;
        private NativeArray<NavigationFlowFieldJobResult> _activeResults;
        private NativeArray<NavigationDynamicOverlayCell> _activeOverlay;
        private NativeArray<NavigationDynamicOverlayCluster> _activeOverlayClusters;
        private NativeStream _activeCorridorClusters;
        private NativeStream _activeCorridorPortals;
        private NativeStream _activeWaypointCells;
        private NativeStream _activeFlowCells;

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
        private int _workspaceCount;
        private int _cellStride;
        private int _clusterStride;
        private int _abstractStride;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            // Cohort Query 要求 Handle 和队列元数据，确保旧 Squad 不会误入共享链路
            _cohortQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniMovementCohort>(),
                ComponentType.ReadOnly<NavigationFlowFieldRequest>(),
                ComponentType.ReadWrite<NavigationFlowFieldState>(),
                ComponentType.ReadWrite<NavigationFlowFieldHandle>(),
                ComponentType.ReadWrite<NavigationFlowFieldQueueState>());
            // 共享记录必须同时拥有四类结果 Buffer，半成品不会参与缓存命中
            _recordQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<NavigationSharedFlowFieldRecord>(),
                ComponentType.ReadOnly<NavigationCorridorCluster>(),
                ComponentType.ReadOnly<NavigationCorridorPortal>(),
                ComponentType.ReadOnly<NavigationHierarchicalWaypoint>(),
                ComponentType.ReadOnly<NavigationFlowFieldCell>());

            // Store Entity 只保存调度配置、汇总状态和有界等待样本
            _storeEntity = state.EntityManager.CreateEntity(
                typeof(NavigationFlowFieldSchedulerSettings),
                typeof(NavigationFlowFieldSchedulerState));
            state.EntityManager.SetComponentData(
                _storeEntity,
                NavigationFlowFieldSchedulerSettings.CreateDefault());
            state.EntityManager.AddBuffer<NavigationFlowFieldQueueWaitSample>(_storeEntity);
            _nextRecordVersion = 1;
            _nextGeneration = 1;
        }

        public void OnUpdate(ref SystemState state)
        {
            NavigationFlowFieldSchedulerState schedulerState = state.EntityManager
                .GetComponentData<NavigationFlowFieldSchedulerState>(_storeEntity);
            schedulerState.Tick++;
            schedulerState.LastPublishedBuildCount = 0;

            if (!TryGetGrid(
                    ref state,
                    out Entity gridEntity,
                    out BlobAssetReference<NavigationGridBlob> grid))
            {
                // Grid 尚未就绪时保留队列现场，只发布本 Tick 的观察状态
                PublishSchedulerState(ref state, ref schedulerState);
                return;
            }

            DynamicBuffer<NavigationDynamicOverlayCell> overlay = state.EntityManager
                .HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(gridEntity, true)
                : default;
            DynamicBuffer<NavigationDynamicOverlayCluster> overlayClusters = state.EntityManager
                .HasBuffer<NavigationDynamicOverlayCluster>(gridEntity)
                ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCluster>(gridEntity, true)
                : default;
            // 主线程也读取独立快照，发布 Record 造成的结构变更不会使 Overlay 视图失效
            using NativeArray<NavigationDynamicOverlayCell> overlayRead = overlay.IsCreated
                ? overlay.ToNativeArray(Allocator.Temp)
                : default;
            using NativeArray<NavigationDynamicOverlayCluster> overlayClusterRead =
                overlayClusters.IsCreated
                    ? overlayClusters.ToNativeArray(Allocator.Temp)
                    : default;
            uint overlayVersion = state.EntityManager
                .HasComponent<NavigationDynamicOverlayState>(gridEntity)
                ? state.EntityManager.GetComponentData<NavigationDynamicOverlayState>(gridEntity)
                    .Version
                : 1u;

            // 先处理请求换代，保证同一帧完成的旧结果不会覆盖新版本
            ReconcileQueueVersions(ref state, ref schedulerState);
            if (_activeJobScheduled)
            {
                // 构建期间不触碰任务持有的快照和工作区，只维护可独立更新的统计
                if (!_activeJobHandle.IsCompleted)
                {
                    schedulerState.ActiveBuildCount = _activeRequests.Length;
                    schedulerState.QueueLength = CountWaitingRequests(ref state);
                    // 活动 Job 不会修改既有 Record，引用统计和缓存回收仍可继续执行
                    RefreshRecordUsageAndBudget(ref state, ref schedulerState);
                    PublishSchedulerState(ref state, ref schedulerState);
                    return;
                }

                // 所有结果都在主线程按槽位顺序发布，避免并行完成顺序改变 ECS 状态
                _activeJobHandle.Complete();
                ApplyActiveResults(
                    ref state,
                    ref grid.Value,
                    overlayRead,
                    overlayClusterRead,
                    overlayVersion,
                    ref schedulerState);
                // 发布完成后才释放 NativeStream，Reader 生命周期覆盖全部结果消费
                DisposeActiveBatch();
            }

            // 网格换代时先等正在读取旧 Blob 的任务结束，再释放它持有的工作区
            RefreshStoreForGrid(ref state, grid.Value.DataHash, ref schedulerState);

            // 发布结束后清理受局部 Overlay 影响的 Record，再收集本轮请求
            RepairAndInvalidateHandles(
                ref state,
                overlayClusterRead);

            NavigationFlowFieldSchedulerSettings settings = ResolveSettings(ref state);
            using var candidates = new NativeList<SchedulerCandidate>(Allocator.Temp);
            CollectPendingRequests(
                ref state,
                ref grid.Value,
                overlayRead,
                settings,
                ref schedulerState,
                candidates);
            // 高优先级优先，同优先级按等待时间和稳定键排序
            SortCandidates(candidates);

            // 每轮只提交预算与并发上限的交集，剩余候选保持 Pending
            int buildCount = math.min(
                candidates.Length,
                math.min(settings.MaximumBuildsPerTick, settings.MaximumConcurrentBuilds));
            if (buildCount > 0)
            {
                ScheduleBuilds(
                    ref state,
                    grid,
                    overlayRead,
                    overlayClusterRead,
                    overlayVersion,
                    candidates,
                    buildCount,
                    ref schedulerState);
            }

            schedulerState.QueueLength = CountWaitingRequests(ref state);
            schedulerState.ActiveBuildCount = _activeJobScheduled ? _activeRequests.Length : 0;
            RefreshRecordUsageAndBudget(ref state, ref schedulerState);
            PublishSchedulerState(ref state, ref schedulerState);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_activeJobScheduled)
            {
                // World 销毁不能让 Worker 继续读取随后将释放的 Blob 和工作区
                _activeJobHandle.Complete();
            }

            DisposeActiveBatch();
            DisposeWorkspaces();
        }

        private bool TryGetGrid(
            ref SystemState state,
            out Entity gridEntity,
            out BlobAssetReference<NavigationGridBlob> grid)
        {
            gridEntity = Entity.Null;
            grid = default;
            // 多张 Grid 无法为 Cell 索引确定唯一数据域，因此与缺失 Grid 一样暂停调度
            if (_gridQuery.CalculateEntityCount() != 1)
            {
                return false;
            }

            gridEntity = _gridQuery.GetSingletonEntity();
            grid = state.EntityManager.GetComponentData<NavigationGridReference>(gridEntity).Value;
            return grid.IsCreated;
        }

        private NavigationFlowFieldSchedulerSettings ResolveSettings(ref SystemState state)
        {
            NavigationFlowFieldSchedulerSettings settings = state.EntityManager
                .GetComponentData<NavigationFlowFieldSchedulerSettings>(_storeEntity);
            // 硬上限与预留工作区模型绑定，运行时配置不能绕过该内存边界
            settings.MaximumConcurrentBuilds = math.clamp(
                settings.MaximumConcurrentBuilds,
                1,
                MaximumSupportedConcurrentBuilds);
            settings.MaximumBuildsPerTick = math.clamp(
                settings.MaximumBuildsPerTick,
                1,
                settings.MaximumConcurrentBuilds);
            settings.RequestTimeoutTicks = math.max(1, settings.RequestTimeoutTicks);
            // 保留至少一个字节可避免负数配置让所有新 Record 立即进入异常分支
            settings.StoreByteBudget = math.max(1L, settings.StoreByteBudget);
            return settings;
        }

        // Grid Blob 换代后旧索引没有复用价值，需要让全部 Cohort 重新取路
        private void RefreshStoreForGrid(
            ref SystemState state,
            Unity.Entities.Hash128 gridHash,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            if (_storeGridHash.Equals(gridHash))
            {
                return;
            }

            // Grid 内容变化后所有 Cell、Cluster 与 Portal 索引都不再属于同一数据集
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            if (records.Length > 0)
            {
                state.EntityManager.DestroyEntity(records);
                schedulerState.CumulativeEvictedCount += records.Length;
            }
            // Handle 必须在旧 Blob 工作区释放前撤销，消费者下一 Tick 才能重新投影
            ClearAllHandles(ref state);
            _storeGridHash = gridHash;
            DisposeWorkspaces();
        }

        // 将缓存命中、正在构建和真正需要排队的请求分流
        private void CollectPendingRequests(
            ref SystemState state,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NavigationFlowFieldSchedulerSettings settings,
            ref NavigationFlowFieldSchedulerState schedulerState,
            NativeList<SchedulerCandidate> candidates)
        {
            // Entity 快照让本轮遍历不受后续 Record 创建和销毁影响
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            // 本 Tick 已完成局部失效检查，这里只建立一次哈希索引供全部 Cohort 查询
            using var recordIndex = new NativeParallelHashMap<NavigationFlowFieldKey, Entity>(
                math.max(1, records.Length),
                Allocator.Temp);
            for (int index = 0; index < records.Length; index++)
            {
                Entity recordEntity = records[index];
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(recordEntity);
                recordIndex.TryAdd(record.Key, recordEntity);
            }
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldQueueState>(cohortEntity);

                if (fieldState.Status != NavigationPathStatus.Pending)
                {
                    // Searching 由活动批次拥有，成功和失败状态等待上层提交新版本
                    continue;
                }

                // 超时从首次入队开始计算，低优先级请求不会无限滞留
                int waitedTicks = schedulerState.Tick - queueState.EnqueuedTick;
                if (waitedTicks >= settings.RequestTimeoutTicks)
                {
                    fieldState.Status = NavigationPathStatus.Failed;
                    fieldState.FailureReason = NavigationPathFailureReason.TimedOut;
                    state.EntityManager.SetComponentData(cohortEntity, fieldState);
                    CompleteQueue(
                        ref state,
                        cohortEntity,
                        waitedTicks,
                        NavigationPathStatus.Failed,
                        schedulerState.Tick);
                    schedulerState.CumulativeTimeoutCount++;
                    continue;
                }

                if (!TryCreateKey(
                        ref grid,
                        request.PathRequest,
                        overlay,
                        out NavigationFlowFieldKey key,
                        out NavigationPathFailureReason failureReason))
                {
                    // 投影失败属于请求本身的终态，不占用后续构建预算
                    fieldState.Status = NavigationPathStatus.Failed;
                    fieldState.FailureReason = failureReason;
                    state.EntityManager.SetComponentData(cohortEntity, fieldState);
                    CompleteQueue(
                        ref state,
                        cohortEntity,
                        waitedTicks,
                        NavigationPathStatus.Failed,
                        schedulerState.Tick);
                    continue;
                }

                // 有效 Record 直接共享，Cohort 本身不复制 Corridor 和 Flow Buffer
                Entity cachedRecord = FindRecord(recordIndex, key);
                if (cachedRecord != Entity.Null)
                {
                    AttachRecord(
                        ref state,
                        cohortEntity,
                        cachedRecord,
                        request.PathRequest.Version,
                        cacheHit: true,
                        schedulerState.Tick);
                    schedulerState.CumulativeSharedHitCount++;
                    continue;
                }

                // 相同 Key 已在构建时继续等待，完成后由统一发布阶段一次挂接
                if (ContainsActiveKey(key))
                {
                    continue;
                }

                SchedulerCandidate candidate = new SchedulerCandidate
                {
                    Key = key,
                    Cohort = cohortEntity,
                    Request = request,
                    Priority = request.Priority,
                    EnqueuedTick = queueState.EnqueuedTick,
                };
                // 同 Key 只留下优先级最高且等待最久的 Cohort 作为构建代表
                AddOrPromoteCandidate(candidates, candidate);
            }
        }

        // 请求版本和取消版本共同定义一次可观测的排队生命周期
        private void ReconcileQueueVersions(
            ref SystemState state,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldQueueState>(cohortEntity);
                if (queueState.RequestVersion == request.PathRequest.Version &&
                    queueState.CancellationVersion == request.CancellationVersion)
                {
                    // 版本未变时保留原始入队 Tick，不能因逐帧扫描重置等待时间
                    continue;
                }

                // 调度中的旧版本被替换时立即记账，迟到结果仍可作为新请求的共享缓存
                if (queueState.RequestVersion != 0 && queueState.CompletedTick < 0)
                {
                    schedulerState.CumulativeCancelledCount++;
                    AddWaitSample(
                        ref state,
                        math.max(0, schedulerState.Tick - queueState.EnqueuedTick),
                        NavigationPathStatus.Cancelled);
                }
                // 新生命周期从当前 Tick 入队，尚未开工和完成使用负值表示
                queueState = new NavigationFlowFieldQueueState
                {
                    RequestVersion = request.PathRequest.Version,
                    CancellationVersion = request.CancellationVersion,
                    EnqueuedTick = schedulerState.Tick,
                    StartedTick = -1,
                    CompletedTick = -1,
                };
                state.EntityManager.SetComponentData(cohortEntity, queueState);
                state.EntityManager.SetComponentData(
                    cohortEntity,
                    default(NavigationFlowFieldHandle));
            }
        }

        // 为本 Tick 选中的唯一 Key 准备隔离工作区并提交并行构建
        private void ScheduleBuilds(
            ref SystemState state,
            BlobAssetReference<NavigationGridBlob> grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            uint overlayVersion,
            NativeList<SchedulerCandidate> candidates,
            int buildCount,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            // 工作区容量按本批并发数扩展，稳定规模下跨批次复用底层内存
            EnsureWorkspaceCapacity(
                buildCount,
                grid.Value.Cells.Length,
                grid.Value.Clusters.Length,
                grid.Value.PortalNodes.Length);
            EnsureGenerationCapacity(
                buildCount * NavigationGridFlowFieldJob.CalculateGenerationStride(
                    grid.Value.PortalNodes.Length,
                    overlayVersion));

            // 活动批次会跨 Tick 存活，输入、结果和流都使用 Persistent 分配器
            _activeRequests = new NativeArray<NavigationSharedFlowFieldBuildRequest>(
                buildCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _activeResults = new NativeArray<NavigationFlowFieldJobResult>(
                buildCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            // 快照和输出流在 Schedule 前一次准备完成，Worker 不读取 ECS DynamicBuffer
            CopyOverlaySnapshot(overlay, overlayClusters);
            _activeCorridorClusters = new NativeStream(buildCount, Allocator.Persistent);
            _activeCorridorPortals = new NativeStream(buildCount, Allocator.Persistent);
            _activeWaypointCells = new NativeStream(buildCount, Allocator.Persistent);
            _activeFlowCells = new NativeStream(buildCount, Allocator.Persistent);
            _activeGridHash = grid.Value.DataHash;

            int generationStride = NavigationGridFlowFieldJob.CalculateGenerationStride(
                grid.Value.PortalNodes.Length,
                overlayVersion);
            // 每个槽位拥有独立版本号、Generation 区间和工作区切片
            for (int index = 0; index < buildCount; index++)
            {
                SchedulerCandidate candidate = candidates[index];
                uint recordVersion = NextRecordVersion();
                _activeRequests[index] = new NavigationSharedFlowFieldBuildRequest
                {
                    Key = candidate.Key,
                    JobRequest = new NavigationFlowFieldJobRequest
                    {
                        Entity = candidate.Cohort,
                        Request = candidate.Request,
                    },
                    RecordVersion = recordVersion,
                    EnqueuedTick = candidate.EnqueuedTick,
                    GenerationStart = _nextGeneration + index * generationStride,
                };
                // 同 Key 的所有等待者一起标为 Searching，防止下一 Tick 再次生成候选
                MarkMatchingRequestsSearching(
                    ref state,
                    candidate.Key,
                    ref grid.Value,
                    overlay,
                    schedulerState.Tick);
            }
            // 下一批跳过本批全部 Generation 区间，旧访问标记不会互相命中
            _nextGeneration += buildCount * generationStride;

            var job = new NavigationSharedFlowFieldBuildJob
            {
                Grid = grid,
                Requests = _activeRequests,
                DynamicOverlay = _activeOverlay,
                DynamicOverlayClusters = _activeOverlayClusters,
                DynamicOverlayVersion = overlayVersion,
                Results = _activeResults,
                CorridorClusters = _activeCorridorClusters.AsWriter(),
                CorridorPortals = _activeCorridorPortals.AsWriter(),
                HierarchicalWaypointCells = _activeWaypointCells.AsWriter(),
                FlowCells = _activeFlowCells.AsWriter(),
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
                CellStride = _cellStride,
                ClusterStride = _clusterStride,
                AbstractStride = _abstractStride,
            };
            // 内层批大小为一，允许调度器把每个唯一 Key 分发给不同 Worker
            _activeJobHandle = job.Schedule(buildCount, 1);
            _activeJobScheduled = true;
            schedulerState.CumulativeUniqueBuildCount += buildCount;
            schedulerState.ActiveBuildCount = buildCount;
        }

        // 按请求槽位读取 NativeStream，并将成功结果转成只读共享 Record
        private void ApplyActiveResults(
            ref SystemState state,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            uint overlayVersion,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            // 发布计数包含成功和失败槽位，便于对照本轮实际完成的构建量
            schedulerState.LastPublishedBuildCount = _activeResults.Length;
            bool gridStillMatches = _activeGridHash.Equals(grid.DataHash);
            var clusterReader = _activeCorridorClusters.AsReader();
            var portalReader = _activeCorridorPortals.AsReader();
            var waypointReader = _activeWaypointCells.AsReader();
            var fieldReader = _activeFlowCells.AsReader();
            if (!gridStillMatches)
            {
                // 新 Grid 的 Cell 索引可能完全不同，不能再用旧 Key 定位活动批次消费者
                ResetAllSearchingRequests(ref state);
            }
            for (int index = 0; index < _activeResults.Length; index++)
            {
                NavigationSharedFlowFieldBuildRequest buildRequest = _activeRequests[index];
                NavigationFlowFieldJobResult result = _activeResults[index];
                // Grid 换代属于可重试失效，求解失败则保留明确失败原因
                if (!gridStillMatches || result.Status != NavigationPathStatus.Succeeded)
                {
                    if (gridStillMatches)
                    {
                        FailMatchingRequests(
                            ref state,
                            buildRequest.Key,
                            ref grid,
                            overlay,
                            result.FailureReason,
                            schedulerState.Tick);
                    }
                    SkipStreams(index, ref clusterReader, ref portalReader, ref waypointReader, ref fieldReader);
                    continue;
                }

                // 只有成功槽位才创建 Record，失败结果不会留下空缓存项
                Entity recordEntity = CreateRecord(
                    ref state,
                    ref grid,
                    buildRequest,
                    result,
                    index,
                    ref clusterReader,
                    ref portalReader,
                    ref waypointReader,
                    ref fieldReader,
                    schedulerState.Tick);
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(recordEntity);
                // 发布前再次比对实际 Corridor，挡住计算期间发生的局部障碍变化
                uint currentSignature = CalculateRecordOverlaySignature(
                    state.EntityManager,
                    recordEntity,
                    overlayClusters);
                if (currentSignature != record.DynamicOverlaySignature)
                {
                    // Job 使用的快照已经过期时不发布 Handle，下一 Tick 会按新 Overlay 重建
                    state.EntityManager.DestroyEntity(recordEntity);
                    ResetMatchingRequests(
                        ref state,
                        buildRequest.Key,
                        ref grid,
                        overlay);
                    continue;
                }

                // 发布时重新匹配当前请求，构建期间换代的 Cohort 不会被旧目标覆盖
                AttachMatchingRequests(
                    ref state,
                    recordEntity,
                    buildRequest.Key,
                    ref grid,
                    overlay,
                    schedulerState.Tick,
                    ref schedulerState);
            }
        }

        // 将一个求解槽位的四类输出收拢到同一个共享 Record Entity
        private Entity CreateRecord(
            ref SystemState state,
            ref NavigationGridBlob grid,
            NavigationSharedFlowFieldBuildRequest buildRequest,
            NavigationFlowFieldJobResult result,
            int streamIndex,
            ref NativeStream.Reader clusterReader,
            ref NativeStream.Reader portalReader,
            ref NativeStream.Reader waypointReader,
            ref NativeStream.Reader fieldReader,
            int tick)
        {
            Entity recordEntity = state.EntityManager.CreateEntity(
                typeof(NavigationSharedFlowFieldRecord),
                typeof(NavigationCorridorCluster),
                typeof(NavigationCorridorPortal),
                typeof(NavigationHierarchicalWaypoint),
                typeof(NavigationFlowFieldCell));
            // 一次性创建完整 Archetype 后再取得 Buffer，后续写入不会被结构变更使句柄失效
            DynamicBuffer<NavigationCorridorCluster> clusters = state.EntityManager
                .GetBuffer<NavigationCorridorCluster>(recordEntity);
            DynamicBuffer<NavigationCorridorPortal> portals = state.EntityManager
                .GetBuffer<NavigationCorridorPortal>(recordEntity);
            DynamicBuffer<NavigationHierarchicalWaypoint> waypoints = state.EntityManager
                .GetBuffer<NavigationHierarchicalWaypoint>(recordEntity);
            DynamicBuffer<NavigationFlowFieldCell> field = state.EntityManager
                .GetBuffer<NavigationFlowFieldCell>(recordEntity);

            // 四个 NativeStream 段必须按相同槽位依次读完，后续槽位才能正确定位
            int clusterCount = clusterReader.BeginForEachIndex(streamIndex);
            for (int index = 0; index < clusterCount; index++)
            {
                clusters.Add(new NavigationCorridorCluster
                {
                    ClusterId = clusterReader.Read<int>(),
                });
            }
            clusterReader.EndForEachIndex();

            int portalCount = portalReader.BeginForEachIndex(streamIndex);
            for (int index = 0; index < portalCount; index++)
            {
                portals.Add(new NavigationCorridorPortal
                {
                    PortalIndex = portalReader.Read<int>(),
                });
            }
            portalReader.EndForEachIndex();

            int waypointCount = waypointReader.BeginForEachIndex(streamIndex);
            for (int index = 0; index < waypointCount; index++)
            {
                int cellIndex = waypointReader.Read<int>();
                // Worker 只返回 CellIndex，世界坐标在发布时由当前 Grid 统一还原
                waypoints.Add(new NavigationHierarchicalWaypoint
                {
                    CellIndex = cellIndex,
                    Position = NavigationGridQuery.GetCellWorldPosition(ref grid, cellIndex),
                });
            }
            waypointReader.EndForEachIndex();

            int fieldCount = fieldReader.BeginForEachIndex(streamIndex);
            for (int index = 0; index < fieldCount; index++)
            {
                field.Add(fieldReader.Read<NavigationFlowFieldCell>());
            }
            fieldReader.EndForEachIndex();

            // 缓存预算按有效负载估算，不把 Cohort Handle 重复计入
            int byteSize = UnsafeUtility.SizeOf<NavigationSharedFlowFieldRecord>() +
                           clusters.Length * UnsafeUtility.SizeOf<NavigationCorridorCluster>() +
                           portals.Length * UnsafeUtility.SizeOf<NavigationCorridorPortal>() +
                           waypoints.Length * UnsafeUtility.SizeOf<NavigationHierarchicalWaypoint>() +
                           field.Length * UnsafeUtility.SizeOf<NavigationFlowFieldCell>();
            state.EntityManager.SetComponentData(recordEntity, new NavigationSharedFlowFieldRecord
            {
                Key = buildRequest.Key,
                RecordVersion = buildRequest.RecordVersion,
                DynamicOverlaySignature = result.DynamicOverlaySignature,
                SourceOverlayVersion = result.DynamicOverlayVersion,
                LastUsedTick = tick,
                ByteSize = byteSize,
                AbstractExpandedNodeCount = result.AbstractExpandedNodeCount,
                IntegrationExpandedCellCount = result.IntegrationExpandedCellCount,
                TotalCost = result.TotalCost,
            });
            return recordEntity;
        }

        // 一个唯一构建完成后，把当下仍匹配该 Key 的全部请求挂到同一 Record
        private void AttachMatchingRequests(
            ref SystemState state,
            Entity recordEntity,
            NavigationFlowFieldKey key,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            int tick,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            // 稳定 Entity 顺序让构建所有者和共享命中统计可以重复验证
            SortEntities(cohorts);
            bool ownerAttached = false;
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status != NavigationPathStatus.Searching &&
                    fieldState.Status != NavigationPathStatus.Pending)
                {
                    // 已结束的旧生命周期不能因 Key 碰巧相同而再次取得 Handle
                    continue;
                }

                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                // 请求可能在 Worker 运行期间换代，必须用当前值重新计算 Key
                if (!TryCreateKey(
                        ref grid,
                        request.PathRequest,
                        overlay,
                        out NavigationFlowFieldKey currentKey,
                        out _) ||
                    !KeysEqual(key, currentKey))
                {
                    continue;
                }

                // 稳定顺序中的第一个消费者记为构建所有者，其余都计入共享命中
                bool cacheHit = ownerAttached;
                AttachRecord(
                    ref state,
                    cohortEntity,
                    recordEntity,
                    request.PathRequest.Version,
                    cacheHit,
                    tick);
                if (cacheHit)
                {
                    schedulerState.CumulativeSharedHitCount++;
                }
                ownerAttached = true;
            }
        }

        // Handle 只保存 Record 引用和版本，运行时数据始终留在共享存储中
        private void AttachRecord(
            ref SystemState state,
            Entity cohortEntity,
            Entity recordEntity,
            uint requestVersion,
            bool cacheHit,
            int tick)
        {
            NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                NavigationSharedFlowFieldRecord>(recordEntity);
            // Handle 同时冻结请求和 Record 版本，任一换代都会在维护阶段失效
            NavigationFlowFieldHandle handle = new NavigationFlowFieldHandle
            {
                Record = recordEntity,
                RecordVersion = record.RecordVersion,
                RequestVersion = requestVersion,
            };
            state.EntityManager.SetComponentData(cohortEntity, handle);

            DynamicBuffer<NavigationCorridorCluster> clusters = state.EntityManager.GetBuffer<
                NavigationCorridorCluster>(recordEntity, true);
            DynamicBuffer<NavigationCorridorPortal> portals = state.EntityManager.GetBuffer<
                NavigationCorridorPortal>(recordEntity, true);
            DynamicBuffer<NavigationHierarchicalWaypoint> waypoints = state.EntityManager.GetBuffer<
                NavigationHierarchicalWaypoint>(recordEntity, true);
            DynamicBuffer<NavigationFlowFieldCell> field = state.EntityManager.GetBuffer<
                NavigationFlowFieldCell>(recordEntity, true);
            // Cohort 只复制长度、成本和版本等小型状态，不复制任何结果 Buffer
            NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                NavigationFlowFieldState>(cohortEntity);
            fieldState.Status = NavigationPathStatus.Succeeded;
            fieldState.FailureReason = NavigationPathFailureReason.None;
            fieldState.RequestVersion = requestVersion;
            fieldState.CacheVersion = record.RecordVersion;
            fieldState.ProjectedStartCellIndex = record.Key.StartCellIndex;
            fieldState.ProjectedEndCellIndex = record.Key.EndCellIndex;
            fieldState.CorridorClusterCount = clusters.Length;
            fieldState.CorridorPortalCount = portals.Length;
            fieldState.HierarchicalWaypointCount = waypoints.Length;
            fieldState.FieldCellCount = field.Length;
            fieldState.AbstractExpandedNodeCount = record.AbstractExpandedNodeCount;
            fieldState.IntegrationExpandedCellCount = record.IntegrationExpandedCellCount;
            fieldState.TotalCost = record.TotalCost;
            fieldState.CacheHit = cacheHit ? (byte)1 : (byte)0;
            fieldState.DynamicOverlayVersion = record.SourceOverlayVersion;
            state.EntityManager.SetComponentData(cohortEntity, fieldState);

            NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                NavigationFlowFieldQueueState>(cohortEntity);
            // 等待分位数统计的是入队到开工，不混入求解耗时
            int waitTicks = math.max(0, queueState.StartedTick - queueState.EnqueuedTick);
            CompleteQueue(
                ref state,
                cohortEntity,
                waitTicks,
                NavigationPathStatus.Succeeded,
                tick);
            record.LastUsedTick = tick;
            state.EntityManager.SetComponentData(recordEntity, record);
        }

        // 同一个 Key 的请求一起进入 Searching，避免重复占用后续构建额度
        private void MarkMatchingRequestsSearching(
            ref SystemState state,
            NavigationFlowFieldKey key,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            int tick)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status != NavigationPathStatus.Pending)
                {
                    continue;
                }

                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                if (!TryCreateKey(ref grid, request.PathRequest, overlay, out var currentKey, out _) ||
                    !KeysEqual(key, currentKey))
                {
                    continue;
                }

                fieldState.Status = NavigationPathStatus.Searching;
                fieldState.FailureReason = NavigationPathFailureReason.None;
                state.EntityManager.SetComponentData(cohortEntity, fieldState);
                NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldQueueState>(cohortEntity);
                // 同一构建的所有共享等待者使用相同开工 Tick，等待统计保持可比
                queueState.StartedTick = tick;
                state.EntityManager.SetComponentData(cohortEntity, queueState);
            }
        }

        // 唯一构建失败时只结束仍在消费该 Key 的请求
        private void FailMatchingRequests(
            ref SystemState state,
            NavigationFlowFieldKey key,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NavigationPathFailureReason failureReason,
            int tick)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                if (!TryCreateKey(ref grid, request.PathRequest, overlay, out var currentKey, out _) ||
                    !KeysEqual(key, currentKey))
                {
                    continue;
                }

                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status != NavigationPathStatus.Searching)
                {
                    // 新版本可能已回到 Pending，旧失败不能结束它的排队生命周期
                    continue;
                }

                fieldState.Status = NavigationPathStatus.Failed;
                fieldState.FailureReason = failureReason;
                state.EntityManager.SetComponentData(cohortEntity, fieldState);
                NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldQueueState>(cohortEntity);
                CompleteQueue(
                    ref state,
                    cohortEntity,
                    math.max(0, queueState.StartedTick - queueState.EnqueuedTick),
                    NavigationPathStatus.Failed,
                    tick);
            }
        }

        // Grid 或快照过期时把消费者退回 Pending，下一 Tick 使用新数据重建
        private void ResetMatchingRequests(
            ref SystemState state,
            NavigationFlowFieldKey key,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                if (!TryCreateKey(ref grid, request.PathRequest, overlay, out var currentKey, out _) ||
                    !KeysEqual(key, currentKey))
                {
                    continue;
                }

                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status == NavigationPathStatus.Searching)
                {
                    // 保留原始 EnqueuedTick，重试仍属于同一次请求的连续等待
                    fieldState.Status = NavigationPathStatus.Pending;
                    state.EntityManager.SetComponentData(cohortEntity, fieldState);
                }
            }
        }

        // Grid 整体换代时当前所有 Searching 请求都属于唯一活动批次
        private void ResetAllSearchingRequests(ref SystemState state)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status != NavigationPathStatus.Searching)
                {
                    continue;
                }

                fieldState.Status = NavigationPathStatus.Pending;
                fieldState.FailureReason = NavigationPathFailureReason.None;
                // QueueState 不重建，Grid 换代后的重试继续累计同一请求等待时间
                state.EntityManager.SetComponentData(cohortEntity, fieldState);
            }
        }

        // 根据 Record 的实际 Corridor 做局部失效，并修复悬空 Handle
        private void RepairAndInvalidateHandles(
            ref SystemState state,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            // 先撤销签名失效的 Record，随后悬空 Handle 检查只处理删除或换代情况
            for (int index = 0; index < records.Length; index++)
            {
                Entity recordEntity = records[index];
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(recordEntity);
                if (CalculateRecordOverlaySignature(
                        state.EntityManager,
                        recordEntity,
                        overlayClusters) == record.DynamicOverlaySignature)
                {
                    // 签名相同表示该 Corridor 看到的 Cluster 版本没有变化
                    continue;
                }

                // 只有 Corridor 涉及的 Cluster 版本变化才撤销对应消费者
                InvalidateRecordConsumers(ref state, recordEntity);
                state.EntityManager.DestroyEntity(recordEntity);
            }

            // 共享记录清理完成后再审计 Handle，可统一覆盖局部失效和外部销毁
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldHandle handle = state.EntityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohortEntity);
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                    NavigationFlowFieldRequest>(cohortEntity);
                if (handle.Record != Entity.Null &&
                    state.EntityManager.Exists(handle.Record) &&
                    state.EntityManager.HasComponent<NavigationSharedFlowFieldRecord>(handle.Record) &&
                    state.EntityManager.GetComponentData<NavigationSharedFlowFieldRecord>(
                        handle.Record).RecordVersion == handle.RecordVersion &&
                    handle.RequestVersion == request.PathRequest.Version)
                {
                    continue;
                }

                // 共享记录或请求版本任一不符时都清空 Handle，禁止继续读取过期 Buffer
                if (handle.Record != Entity.Null)
                {
                    state.EntityManager.SetComponentData(
                        cohortEntity,
                        default(NavigationFlowFieldHandle));
                }
                if (fieldState.Status == NavigationPathStatus.Succeeded)
                {
                    // 只有曾成功消费 Record 的请求需要重排，失败请求保持原诊断结果
                    fieldState.Status = NavigationPathStatus.Pending;
                    fieldState.FailureReason = NavigationPathFailureReason.None;
                    state.EntityManager.SetComponentData(cohortEntity, fieldState);
                }
            }
        }

        // 删除共享记录前先让所有消费者回到可重规划状态
        private void InvalidateRecordConsumers(ref SystemState state, Entity recordEntity)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldHandle handle = state.EntityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohortEntity);
                if (handle.Record != recordEntity)
                {
                    continue;
                }

                // 先撤销引用再删除 Record，下一系统不会观察到指向已销毁 Entity 的 Handle
                state.EntityManager.SetComponentData(
                    cohortEntity,
                    default(NavigationFlowFieldHandle));
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                if (fieldState.Status == NavigationPathStatus.Succeeded)
                {
                    fieldState.Status = NavigationPathStatus.Pending;
                    fieldState.FailureReason = NavigationPathFailureReason.None;
                    state.EntityManager.SetComponentData(cohortEntity, fieldState);
                }
            }
        }

        // 缓存查找复用本 Tick 的 Record 哈希索引，避免 Cohort 与 Record 两两扫描
        private static Entity FindRecord(
            NativeParallelHashMap<NavigationFlowFieldKey, Entity> recordIndex,
            NavigationFlowFieldKey key)
        {
            return recordIndex.TryGetValue(key, out Entity recordEntity)
                ? recordEntity
                : Entity.Null;
        }

        // 每帧重算引用数，并在预算超限时淘汰最久未用的无引用 Record
        private void RefreshRecordUsageAndBudget(
            ref SystemState state,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            // 引用数从 Handle 重新推导，避免增减路径遗漏导致 Record 永久无法淘汰
            for (int index = 0; index < records.Length; index++)
            {
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(records[index]);
                record.ReferenceCount = 0;
                state.EntityManager.SetComponentData(records[index], record);
            }

            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                NavigationFlowFieldHandle handle = state.EntityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohorts[index]);
                if (handle.Record == Entity.Null ||
                    !state.EntityManager.Exists(handle.Record) ||
                    !state.EntityManager.HasComponent<NavigationSharedFlowFieldRecord>(handle.Record))
                {
                    continue;
                }

                // 每个有效 Handle 只贡献一次引用，Record 不根据成员数重复计数
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(handle.Record);
                record.ReferenceCount++;
                record.LastUsedTick = schedulerState.Tick;
                state.EntityManager.SetComponentData(handle.Record, record);
            }

            NavigationFlowFieldSchedulerSettings settings = ResolveSettings(ref state);
            long totalBytes = 0;
            // 先计算完整存量，再按确定顺序逐条淘汰直至满足预算
            for (int index = 0; index < records.Length; index++)
            {
                if (state.EntityManager.Exists(records[index]))
                {
                    totalBytes += state.EntityManager.GetComponentData<
                        NavigationSharedFlowFieldRecord>(records[index]).ByteSize;
                }
            }

            // 被 Handle 引用的 Record 不能为了满足预算而强制删除
            while (totalBytes > settings.StoreByteBudget)
            {
                Entity evictionTarget = Entity.Null;
                NavigationSharedFlowFieldRecord oldest = default;
                for (int index = 0; index < records.Length; index++)
                {
                    Entity recordEntity = records[index];
                    if (!state.EntityManager.Exists(recordEntity))
                    {
                        continue;
                    }
                    NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                        NavigationSharedFlowFieldRecord>(recordEntity);
                    // 同 Tick 使用记录版本打破平局，使淘汰顺序不依赖 Query 返回顺序
                    if (record.ReferenceCount != 0 ||
                        (evictionTarget != Entity.Null &&
                         (record.LastUsedTick > oldest.LastUsedTick ||
                          (record.LastUsedTick == oldest.LastUsedTick &&
                           record.RecordVersion > oldest.RecordVersion))))
                    {
                        continue;
                    }
                    evictionTarget = recordEntity;
                    oldest = record;
                }

                if (evictionTarget == Entity.Null)
                {
                    // 全部 Record 都被引用时允许暂时超预算，正确性优先于强制回收
                    break;
                }
                totalBytes -= oldest.ByteSize;
                state.EntityManager.DestroyEntity(evictionTarget);
                schedulerState.CumulativeEvictedCount++;
            }

            schedulerState.StoreByteCount = totalBytes;
            schedulerState.StoreRecordCount = _recordQuery.CalculateEntityCount();
        }

        // 结束一次排队生命周期并留下可计算分位数的等待样本
        private void CompleteQueue(
            ref SystemState state,
            Entity cohortEntity,
            int waitTicks,
            NavigationPathStatus outcome,
            int tick)
        {
            NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                NavigationFlowFieldQueueState>(cohortEntity);
            queueState.CompletedTick = tick;
            queueState.QueueWaitTicks = waitTicks;
            state.EntityManager.SetComponentData(cohortEntity, queueState);
            AddWaitSample(ref state, waitTicks, outcome);
        }

        // 样本采用定长滑动窗口，避免长时间运行持续增加 Store 内存
        private void AddWaitSample(
            ref SystemState state,
            int waitTicks,
            NavigationPathStatus outcome)
        {
            DynamicBuffer<NavigationFlowFieldQueueWaitSample> samples = state.EntityManager
                .GetBuffer<NavigationFlowFieldQueueWaitSample>(_storeEntity);
            if (samples.Length >= MaximumWaitSamples)
            {
                // 丢弃最早样本形成滑动窗口，近期压力对报告更有参考价值
                samples.RemoveAt(0);
            }
            samples.Add(new NavigationFlowFieldQueueWaitSample
            {
                WaitTicks = math.max(0, waitTicks),
                Outcome = outcome,
            });
        }

        private int CountWaitingRequests(ref SystemState state)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            int count = 0;
            for (int index = 0; index < cohorts.Length; index++)
            {
                NavigationPathStatus status = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohorts[index]).Status;
                if (status == NavigationPathStatus.Pending ||
                    status == NavigationPathStatus.Searching)
                {
                    // 队列长度同时覆盖尚未开工和正在占用 Worker 的请求
                    count++;
                }
            }
            return count;
        }

        // Grid 换代时清空共享引用，已成功的 Cohort 会在下一轮重新排队
        private void ClearAllHandles(ref SystemState state)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < cohorts.Length; index++)
            {
                state.EntityManager.SetComponentData(
                    cohorts[index],
                    default(NavigationFlowFieldHandle));
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohorts[index]);
                if (fieldState.Status == NavigationPathStatus.Succeeded)
                {
                    fieldState.Status = NavigationPathStatus.Pending;
                    state.EntityManager.SetComponentData(cohorts[index], fieldState);
                }
            }
        }

        // 并发数或 Grid 规模变化时重建连续工作区，单个 Job 只使用自己的切片
        private void EnsureWorkspaceCapacity(
            int workspaceCount,
            int cellCount,
            int clusterCount,
            int abstractCount)
        {
            if (_workspaceCount >= workspaceCount &&
                _cellStride == cellCount &&
                _clusterStride == clusterCount &&
                _abstractStride == abstractCount &&
                _cellCosts.IsCreated)
            {
                // 并发容量可以大于当前批次，避免负载回落时反复缩容
                return;
            }

            DisposeWorkspaces();
            _workspaceCount = workspaceCount;
            _cellStride = cellCount;
            _clusterStride = clusterCount;
            _abstractStride = abstractCount;
            int totalCells = workspaceCount * cellCount;
            int totalClusters = workspaceCount * clusterCount;
            int totalAbstract = workspaceCount * abstractCount;
            // 连续大数组比每个请求单独分配更容易复用，也减少 Native 分配次数
            _cellCosts = new NativeArray<float>(totalCells, Allocator.Persistent);
            _cellHeap = new NativeArray<int>(totalCells, Allocator.Persistent);
            _cellHeapPositions = new NativeArray<int>(totalCells, Allocator.Persistent);
            _cellGenerations = new NativeArray<int>(
                totalCells,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _clusterGenerations = new NativeArray<int>(
                totalClusters,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _abstractCosts = new NativeArray<float>(totalAbstract, Allocator.Persistent);
            _abstractEndCosts = new NativeArray<float>(totalAbstract, Allocator.Persistent);
            _abstractParents = new NativeArray<int>(totalAbstract, Allocator.Persistent);
            _abstractHeap = new NativeArray<int>(totalAbstract, Allocator.Persistent);
            _abstractHeapPositions = new NativeArray<int>(totalAbstract, Allocator.Persistent);
            _abstractGenerations = new NativeArray<int>(
                totalAbstract,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _nextGeneration = 1;
        }

        // Generation 接近溢出时统一清零，避免旧访问标记被新任务误认
        private void EnsureGenerationCapacity(int requiredGenerations)
        {
            if (_nextGeneration > 0 && _nextGeneration <= int.MaxValue - requiredGenerations)
            {
                return;
            }
            for (int index = 0; index < _cellGenerations.Length; index++)
            {
                _cellGenerations[index] = 0;
            }
            for (int index = 0; index < _clusterGenerations.Length; index++)
            {
                _clusterGenerations[index] = 0;
            }
            for (int index = 0; index < _abstractGenerations.Length; index++)
            {
                _abstractGenerations[index] = 0;
            }
            _nextGeneration = 1;
        }

        // Job 持有独立 Overlay 副本，主线程更新动态障碍时无需等待构建
        private void CopyOverlaySnapshot(
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            if (overlay.IsCreated)
            {
                // Cell 和 Cluster 两层都复制，求解成本与局部失效签名来自同一版本
                _activeOverlay = new NativeArray<NavigationDynamicOverlayCell>(
                    overlay.Length,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                NativeArray<NavigationDynamicOverlayCell>.Copy(
                    overlay,
                    _activeOverlay);
            }
            if (overlayClusters.IsCreated)
            {
                _activeOverlayClusters = new NativeArray<NavigationDynamicOverlayCluster>(
                    overlayClusters.Length,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                NativeArray<NavigationDynamicOverlayCluster>.Copy(
                    overlayClusters,
                    _activeOverlayClusters);
            }
        }

        private bool ContainsActiveKey(NavigationFlowFieldKey key)
        {
            if (!_activeJobScheduled || !_activeRequests.IsCreated)
            {
                return false;
            }
            // 活动批次受并发硬上限约束，线性检查最多比较八个 Key
            for (int index = 0; index < _activeRequests.Length; index++)
            {
                if (KeysEqual(_activeRequests[index].Key, key))
                {
                    return true;
                }
            }
            return false;
        }

        // Key 使用投影后的 Cell 和成本配置，屏蔽输入坐标的微小浮点差异
        private static bool TryCreateKey(
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            out NavigationFlowFieldKey key,
            out NavigationPathFailureReason failureReason)
        {
            key = default;
            failureReason = NavigationPathFailureReason.InvalidRequest;
            if (!NavigationGridQuery.IsRequestValid(request))
            {
                return false;
            }
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    request.StartPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    overlay,
                    out int startCellIndex))
            {
                failureReason = NavigationPathFailureReason.StartProjectionFailed;
                return false;
            }
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    request.EndPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    overlay,
                    out int endCellIndex))
            {
                failureReason = NavigationPathFailureReason.EndProjectionFailed;
                return false;
            }

            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            // 存储浮点位模式而非舍入值，哈希相等必然代表 Solver 输入完全一致
            key = new NavigationFlowFieldKey
            {
                StartCellIndex = startCellIndex,
                EndCellIndex = endCellIndex,
                RequiredClearanceBits = math.asint(requiredClearance),
                ClearancePenaltyWeightBits = math.asint(request.ClearancePenaltyWeight),
            };
            failureReason = NavigationPathFailureReason.None;
            return true;
        }

        // 签名只覆盖 Record 经过的 Cluster，远处 Overlay 变化不会清空全部缓存
        private static uint CalculateRecordOverlaySignature(
            EntityManager entityManager,
            Entity recordEntity,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            DynamicBuffer<NavigationCorridorCluster> clusters = entityManager.GetBuffer<
                NavigationCorridorCluster>(recordEntity, true);
            uint hash = 2166136261u;
            for (int index = 0; index < clusters.Length; index++)
            {
                // ClusterId 和版本共同进入 FNV 序列，顺序变化也会产生不同签名
                int clusterIndex = clusters[index].ClusterId;
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

        private static bool KeysEqual(
            NavigationFlowFieldKey left,
            NavigationFlowFieldKey right)
        {
            return left.StartCellIndex == right.StartCellIndex &&
                   left.EndCellIndex == right.EndCellIndex &&
                   left.RequiredClearanceBits == right.RequiredClearanceBits &&
                   left.ClearancePenaltyWeightBits == right.ClearancePenaltyWeightBits;
        }

        private static int CompareKeys(
            NavigationFlowFieldKey left,
            NavigationFlowFieldKey right)
        {
            int comparison = left.StartCellIndex.CompareTo(right.StartCellIndex);
            if (comparison != 0) return comparison;
            comparison = left.EndCellIndex.CompareTo(right.EndCellIndex);
            if (comparison != 0) return comparison;
            comparison = left.RequiredClearanceBits.CompareTo(right.RequiredClearanceBits);
            if (comparison != 0) return comparison;
            return left.ClearancePenaltyWeightBits.CompareTo(right.ClearancePenaltyWeightBits);
        }

        // 同 Key 只保留最优调度代表，其余 Cohort 等待共享同一结果
        private static void AddOrPromoteCandidate(
            NativeList<SchedulerCandidate> candidates,
            SchedulerCandidate candidate)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                if (!KeysEqual(candidates[index].Key, candidate.Key))
                {
                    continue;
                }
                if (CandidateComesBefore(candidate, candidates[index]))
                {
                    candidates[index] = candidate;
                }
                return;
            }
            candidates.Add(candidate);
        }

        // 请求量受每 Tick 构建上限约束，短数组插入排序便于保持确定顺序
        private static void SortCandidates(NativeList<SchedulerCandidate> candidates)
        {
            for (int index = 1; index < candidates.Length; index++)
            {
                SchedulerCandidate value = candidates[index];
                int insertionIndex = index - 1;
                while (insertionIndex >= 0 &&
                       CandidateComesBefore(value, candidates[insertionIndex]))
                {
                    candidates[insertionIndex + 1] = candidates[insertionIndex];
                    insertionIndex--;
                }
                candidates[insertionIndex + 1] = value;
            }
        }

        private static bool CandidateComesBefore(
            SchedulerCandidate left,
            SchedulerCandidate right)
        {
            // 优先级降序、入队时间升序，最终稳定键保证重复运行顺序一致
            if (left.Priority != right.Priority)
            {
                return left.Priority > right.Priority;
            }
            if (left.EnqueuedTick != right.EnqueuedTick)
            {
                return left.EnqueuedTick < right.EnqueuedTick;
            }
            int keyComparison = CompareKeys(left.Key, right.Key);
            if (keyComparison != 0)
            {
                return keyComparison < 0;
            }
            return IsEntityBefore(left.Cohort, right.Cohort);
        }

        private static void SortEntities(NativeArray<Entity> entities)
        {
            for (int index = 1; index < entities.Length; index++)
            {
                Entity value = entities[index];
                int insertionIndex = index - 1;
                while (insertionIndex >= 0 && IsEntityBefore(value, entities[insertionIndex]))
                {
                    entities[insertionIndex + 1] = entities[insertionIndex];
                    insertionIndex--;
                }
                entities[insertionIndex + 1] = value;
            }
        }

        private static bool IsEntityBefore(Entity left, Entity right)
        {
            return left.Index < right.Index ||
                   (left.Index == right.Index && left.Version < right.Version);
        }

        private void SkipStreams(
            int index,
            ref NativeStream.Reader clusters,
            ref NativeStream.Reader portals,
            ref NativeStream.Reader waypoints,
            ref NativeStream.Reader fields)
        {
            // 失败槽位也必须消费四个段，否则 Reader 无法前进到下一个请求
            SkipStream<int>(index, ref clusters);
            SkipStream<int>(index, ref portals);
            SkipStream<int>(index, ref waypoints);
            SkipStream<NavigationFlowFieldCell>(index, ref fields);
        }

        private static void SkipStream<T>(int index, ref NativeStream.Reader reader)
            where T : unmanaged
        {
            int count = reader.BeginForEachIndex(index);
            for (int valueIndex = 0; valueIndex < count; valueIndex++)
            {
                reader.Read<T>();
            }
            reader.EndForEachIndex();
        }

        // 版本号跳过零值，零始终表示尚未绑定任何 Record
        private uint NextRecordVersion()
        {
            uint value = _nextRecordVersion++;
            if (_nextRecordVersion == 0)
            {
                _nextRecordVersion = 1;
            }
            return value == 0 ? NextRecordVersion() : value;
        }

        private void PublishSchedulerState(
            ref SystemState state,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            state.EntityManager.SetComponentData(_storeEntity, schedulerState);
        }

        private void DisposeActiveBatch()
        {
            // 完成后的批次资源统一释放，任何提前分支都不能只销毁其中一部分
            if (_activeRequests.IsCreated) _activeRequests.Dispose();
            if (_activeResults.IsCreated) _activeResults.Dispose();
            if (_activeOverlay.IsCreated) _activeOverlay.Dispose();
            if (_activeOverlayClusters.IsCreated) _activeOverlayClusters.Dispose();
            if (_activeCorridorClusters.IsCreated) _activeCorridorClusters.Dispose();
            if (_activeCorridorPortals.IsCreated) _activeCorridorPortals.Dispose();
            if (_activeWaypointCells.IsCreated) _activeWaypointCells.Dispose();
            if (_activeFlowCells.IsCreated) _activeFlowCells.Dispose();
            _activeRequests = default;
            _activeResults = default;
            _activeOverlay = default;
            _activeOverlayClusters = default;
            _activeCorridorClusters = default;
            _activeCorridorPortals = default;
            _activeWaypointCells = default;
            _activeFlowCells = default;
            _activeJobHandle = default;
            _activeJobScheduled = false;
        }

        private void DisposeWorkspaces()
        {
            // 工作区由系统独占，Grid 换代或 World 销毁时成组释放
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
            _workspaceCount = 0;
            _cellStride = 0;
            _clusterStride = 0;
            _abstractStride = 0;
        }

        private struct SchedulerCandidate
        {
            public NavigationFlowFieldKey Key;
            public Entity Cohort;
            public NavigationFlowFieldRequest Request;
            public byte Priority;
            public int EnqueuedTick;
        }
    }
}
