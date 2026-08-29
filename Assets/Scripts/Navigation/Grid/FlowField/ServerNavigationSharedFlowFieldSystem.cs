using System.Diagnostics;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
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
        private const int PreallocatedRecordCount = 32;
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
        private long _activeJobStartTimestamp;
        private bool _activeJobScheduled;
        private int _activeBuildCount;
        private NativeArray<NavigationSharedFlowFieldBuildRequest> _activeRequests;
        private NativeArray<NavigationFlowFieldJobResult> _activeResults;
        private NativeArray<NavigationDynamicOverlayCell> _activeOverlay;
        private NativeArray<NavigationDynamicOverlayCluster> _activeOverlayClusters;
        private NativeList<Entity> _recordPool;
        private long _recordPoolByteCount;
        private long _recordSlotCapacityBytes;
        private bool _recordPoolInitialized;

        private NavigationSharedFlowFieldWorkspace _buildWorkspace0;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace1;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace2;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace3;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace4;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace5;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace6;
        private NavigationSharedFlowFieldWorkspace _buildWorkspace7;

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
            // 共享记录必须同时拥有结果和覆盖块 Buffer，半成品不会参与缓存命中
            _recordQuery = state.GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<NavigationSharedFlowFieldRecord>(),
                    ComponentType.ReadOnly<NavigationCorridorCluster>(),
                    ComponentType.ReadOnly<NavigationCorridorPortal>(),
                    ComponentType.ReadOnly<NavigationHierarchicalWaypoint>(),
                    ComponentType.ReadOnly<NavigationFlowFieldCell>(),
                    ComponentType.ReadOnly<NavigationFlowFieldCoverageTile>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<NavigationSharedFlowFieldRecordPoolSlot>(),
                },
            });

            // Store Entity 只保存调度配置、汇总状态和有界等待样本
            _storeEntity = state.EntityManager.CreateEntity(
                typeof(NavigationFlowFieldSchedulerSettings),
                typeof(NavigationFlowFieldSchedulerState));
            state.EntityManager.SetComponentData(
                _storeEntity,
                NavigationFlowFieldSchedulerSettings.CreateDefault());
            state.EntityManager.AddBuffer<NavigationFlowFieldQueueWaitSample>(_storeEntity);
            _recordPool = new NativeList<Entity>(PreallocatedRecordCount, Allocator.Persistent);
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
                // 构建固定在提交后的下一 Tick 完成，墙钟快慢不能改变结果发布 Tick
                _activeJobHandle.Complete();
                double buildMilliseconds =
                    (Stopwatch.GetTimestamp() - _activeJobStartTimestamp) * 1000.0 /
                    Stopwatch.Frequency;
                schedulerState.LastBuildBatchMilliseconds = buildMilliseconds;
                schedulerState.MaximumBuildBatchMilliseconds = math.max(
                    schedulerState.MaximumBuildBatchMilliseconds,
                    buildMilliseconds);
                ApplyActiveResults(
                    ref state,
                    ref grid.Value,
                    overlayRead,
                    overlayClusterRead,
                    overlayVersion,
                    ref schedulerState);
                // 发布完成后只结束活动批次，工作区内容会在下一次构建前统一清空
                DisposeActiveBatch();
            }

            // 网格换代时先等正在读取旧 Blob 的任务结束，再释放它持有的工作区
            RefreshStoreForGrid(ref state, grid.Value.DataHash, ref schedulerState);
            EnsureWarmStorage(
                ref state,
                ref grid.Value,
                overlayRead.Length,
                overlayClusterRead.Length);

            // 发布结束后清理受局部 Overlay 影响的 Record，再收集本轮请求
            RepairAndInvalidateHandles(
                ref state,
                overlayClusterRead,
                ref schedulerState);

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
            int buildBudget = math.min(
                settings.MaximumBuildsPerTick,
                settings.MaximumConcurrentBuilds);
            if (schedulerState.LastPublishedBuildCount > 0 &&
                schedulerState.LastBuildBatchMilliseconds >
                settings.MaximumWorkerMillisecondsPerTick)
            {
                // 只约束刚完成批次后的连续积压，空闲期不能沿用历史耗时压低新请求吞吐
                buildBudget = 1;
                schedulerState.CumulativeBudgetThrottleCount++;
            }
            int buildCount = math.min(
                candidates.Length,
                buildBudget);
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
            schedulerState.ActiveBuildCount = _activeJobScheduled ? _activeBuildCount : 0;
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
            DisposeActiveBatchStorage();
            DisposeWorkspaces();
            DisposeRecordPool(ref state);
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
                math.min(
                    MaximumSupportedConcurrentBuilds,
                    math.max(1, JobsUtility.JobWorkerCount / 2)));
            settings.MaximumBuildsPerTick = math.clamp(
                settings.MaximumBuildsPerTick,
                1,
                settings.MaximumConcurrentBuilds);
            settings.RequestTimeoutTicks = math.max(1, settings.RequestTimeoutTicks);
            settings.MaximumWorkerMillisecondsPerTick = math.max(
                0.1f,
                settings.MaximumWorkerMillisecondsPerTick);
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
            ClearRecordPool(ref state);
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
                if (record.RefreshPending != 0)
                {
                    // 等待刷新的目标场仍可供原 Handle 读取，但不能接收新的缓存命中
                    continue;
                }
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
                        request,
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
                    if (TryResolveCoveredStartCell(
                            state.EntityManager,
                            cachedRecord,
                            ref grid,
                            request.PathRequest,
                            overlay,
                            out int startCellIndex))
                    {
                        AttachRecord(
                            ref state,
                            cohortEntity,
                            cachedRecord,
                            request.PathRequest.Version,
                            startCellIndex,
                            cacheHit: true,
                            schedulerState.Tick);
                        schedulerState.CumulativeSharedHitCount++;
                        schedulerState.CumulativeCoverageTileReuseCount++;
                    }
                    else
                    {
                        // 目标场只覆盖与目标动态连通的 Cell，场外起点得到明确的无路径结果
                        FailRequest(
                            ref state,
                            cohortEntity,
                            NavigationPathFailureReason.NoPath,
                            schedulerState.Tick);
                    }
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
            // 工作区按并发硬上限一次建立，后续批次只清空长度并复用底层内存
            EnsureWorkspaceCapacity(
                MaximumSupportedConcurrentBuilds,
                grid.Value.Cells.Length,
                grid.Value.Clusters.Length,
                grid.Value.PortalNodes.Length);
            EnsureGenerationCapacity(
                buildCount * NavigationGridFlowFieldJob.CalculateGenerationStride(
                    grid.Value.PortalNodes.Length,
                    overlayVersion));

            // 活动数组和 Overlay 快照只在尺寸变化时重建，稳定负载不产生批次级原生分配
            EnsureActiveBatchStorage(overlay.Length, overlayClusters.Length);
            CopyOverlaySnapshot(overlay, overlayClusters);
            _activeBuildCount = buildCount;
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
                if (candidate.Request.CoverageMode ==
                    NavigationFlowFieldCoverageMode.GoalRegion)
                {
                    schedulerState.CumulativeTargetRecordBuildCount++;
                }
                else
                {
                    schedulerState.CumulativeCorridorResolveCount++;
                }
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
                Workspace0 = _buildWorkspace0,
                Workspace1 = _buildWorkspace1,
                Workspace2 = _buildWorkspace2,
                Workspace3 = _buildWorkspace3,
                Workspace4 = _buildWorkspace4,
                Workspace5 = _buildWorkspace5,
                Workspace6 = _buildWorkspace6,
                Workspace7 = _buildWorkspace7,
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
            _activeJobStartTimestamp = Stopwatch.GetTimestamp();
            _activeJobScheduled = true;
            schedulerState.CumulativeUniqueBuildCount += buildCount;
            schedulerState.ActiveBuildCount = buildCount;
        }

        // 按请求槽位读取复用工作区，并将成功结果转成只读共享 Record
        private void ApplyActiveResults(
            ref SystemState state,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            uint overlayVersion,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            // 发布计数包含成功和失败槽位，便于对照本轮实际完成的构建量
            schedulerState.LastPublishedBuildCount = _activeBuildCount;
            bool gridStillMatches = _activeGridHash.Equals(grid.DataHash);
            if (!gridStillMatches)
            {
                // 新 Grid 的 Cell 索引可能完全不同，不能再用旧 Key 定位活动批次消费者
                ResetAllSearchingRequests(ref state);
            }
            for (int index = 0; index < _activeBuildCount; index++)
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
                    continue;
                }

                NavigationSharedFlowFieldWorkspace workspace = GetWorkspace(index);
                uint currentSignature = CalculateWorkspaceOverlaySignature(
                    ref workspace.CorridorClusters,
                    overlayClusters);
                if (currentSignature != result.DynamicOverlaySignature)
                {
                    // Job 快照过期时保留原目标 Record，等待请求下一 Tick 使用新版本重试
                    continue;
                }

                Entity refreshRecord = buildRequest.Key.CoverageMode ==
                                       NavigationFlowFieldCoverageMode.GoalRegion
                    ? FindPendingRefreshRecord(ref state, buildRequest.Key)
                    : Entity.Null;
                // Integration 成本会跨 Cluster 传播，当前刷新必须按实际完整求解范围计数
                int builtTileCount = workspace.CorridorClusters.Length;
                // 目标场刷新沿用原 Record 和版本，活动 Handle 不会因局部障碍变化悬空
                Entity recordEntity = CreateOrRefreshRecord(
                    ref state,
                    ref grid,
                    buildRequest,
                    result,
                    index,
                    schedulerState.Tick,
                    refreshRecord);
                schedulerState.CumulativeCoverageTileBuildCount += builtTileCount;

                // 每份结果只直接挂接自己的构建所有者，其余等待者随后通过一次缓存扫描归并
                TryAttachBuildOwner(
                    ref state,
                    recordEntity,
                    buildRequest,
                    ref grid,
                    overlay,
                    schedulerState.Tick);
            }
            // 成功结果已进入 Store，剩余 Searching 请求回到 Pending 后会在本 Tick 统一命中缓存
            ResetAllSearchingRequests(ref state);
        }

        // 将一个求解槽位的结果写入新 Record，或原位刷新已有目标 Record
        private Entity CreateOrRefreshRecord(
            ref SystemState state,
            ref NavigationGridBlob grid,
            NavigationSharedFlowFieldBuildRequest buildRequest,
            NavigationFlowFieldJobResult result,
            int workspaceIndex,
            int tick,
            Entity refreshRecord)
        {
            NavigationSharedFlowFieldWorkspace workspace = GetWorkspace(workspaceIndex);
            bool isRefresh = refreshRecord != Entity.Null;
            Entity recordEntity;
            if (isRefresh)
            {
                recordEntity = refreshRecord;
            }
            else if (_recordPool.IsCreated && !_recordPool.IsEmpty)
            {
                int lastIndex = _recordPool.Length - 1;
                recordEntity = _recordPool[lastIndex];
                _recordPool.RemoveAt(lastIndex);
                _recordPoolByteCount = _recordPool.Length * _recordSlotCapacityBytes;
                state.EntityManager.RemoveComponent<NavigationSharedFlowFieldRecordPoolSlot>(
                    recordEntity);
            }
            else
            {
                recordEntity = state.EntityManager.CreateEntity(
                    typeof(NavigationSharedFlowFieldRecord),
                    typeof(NavigationCorridorCluster),
                    typeof(NavigationCorridorPortal),
                    typeof(NavigationHierarchicalWaypoint),
                    typeof(NavigationFlowFieldCell),
                    typeof(NavigationFlowFieldCoverageTile));
            }
            // 一次性创建完整 Archetype 后再取得 Buffer，后续写入不会被结构变更使句柄失效
            DynamicBuffer<NavigationCorridorCluster> clusters = state.EntityManager
                .GetBuffer<NavigationCorridorCluster>(recordEntity);
            DynamicBuffer<NavigationCorridorPortal> portals = state.EntityManager
                .GetBuffer<NavigationCorridorPortal>(recordEntity);
            DynamicBuffer<NavigationHierarchicalWaypoint> waypoints = state.EntityManager
                .GetBuffer<NavigationHierarchicalWaypoint>(recordEntity);
            DynamicBuffer<NavigationFlowFieldCell> field = state.EntityManager
                .GetBuffer<NavigationFlowFieldCell>(recordEntity);
            DynamicBuffer<NavigationFlowFieldCoverageTile> coverageTiles = state.EntityManager
                .GetBuffer<NavigationFlowFieldCoverageTile>(recordEntity);
            clusters.Clear();
            portals.Clear();
            waypoints.Clear();
            field.Clear();
            coverageTiles.Clear();

            for (int index = 0; index < workspace.CorridorClusters.Length; index++)
            {
                int clusterId = workspace.CorridorClusters[index];
                clusters.Add(new NavigationCorridorCluster
                {
                    ClusterId = clusterId,
                });
                coverageTiles.Add(new NavigationFlowFieldCoverageTile
                {
                    ClusterId = clusterId,
                    DynamicOverlayVersion = GetOverlayClusterVersion(
                        _activeOverlayClusters,
                        clusterId),
                });
            }

            for (int index = 0; index < workspace.CorridorPortals.Length; index++)
            {
                portals.Add(new NavigationCorridorPortal
                {
                    PortalIndex = workspace.CorridorPortals[index],
                });
            }

            for (int index = 0; index < workspace.WaypointCells.Length; index++)
            {
                int cellIndex = workspace.WaypointCells[index];
                // Worker 只返回 CellIndex，世界坐标在发布时由当前 Grid 统一还原
                waypoints.Add(new NavigationHierarchicalWaypoint
                {
                    CellIndex = cellIndex,
                    Position = NavigationGridQuery.GetCellWorldPosition(ref grid, cellIndex),
                });
            }

            // Flow Cell 类型与目标 Buffer 一致，批量复制可避免万格目标场逐项扩容和安全检查
            field.AddRange(workspace.FlowCells.AsArray());

            // 缓存预算按有效负载估算，不把 Cohort Handle 重复计入
            int byteSize = UnsafeUtility.SizeOf<NavigationSharedFlowFieldRecord>() +
                           clusters.Length * UnsafeUtility.SizeOf<NavigationCorridorCluster>() +
                           portals.Length * UnsafeUtility.SizeOf<NavigationCorridorPortal>() +
                           waypoints.Length * UnsafeUtility.SizeOf<NavigationHierarchicalWaypoint>() +
                           field.Length * UnsafeUtility.SizeOf<NavigationFlowFieldCell>() +
                           coverageTiles.Length *
                           UnsafeUtility.SizeOf<NavigationFlowFieldCoverageTile>();
            NavigationSharedFlowFieldRecord previous = isRefresh
                ? state.EntityManager.GetComponentData<NavigationSharedFlowFieldRecord>(recordEntity)
                : default;
            state.EntityManager.SetComponentData(recordEntity, new NavigationSharedFlowFieldRecord
            {
                Key = buildRequest.Key,
                RecordVersion = isRefresh
                    ? previous.RecordVersion
                    : buildRequest.RecordVersion,
                DynamicOverlaySignature = result.DynamicOverlaySignature,
                SourceOverlayVersion = result.DynamicOverlayVersion,
                RefreshPending = 0,
                PendingCoverageTileCount = 0,
                ReferenceCount = previous.ReferenceCount,
                LastUsedTick = tick,
                ByteSize = byteSize,
                CoverageTileCount = clusters.Length,
                AbstractExpandedNodeCount = result.AbstractExpandedNodeCount,
                IntegrationExpandedCellCount = result.IntegrationExpandedCellCount,
                TotalCost = result.TotalCost,
            });
            return recordEntity;
        }

        private NavigationSharedFlowFieldWorkspace GetWorkspace(int index)
        {
            return index switch
            {
                0 => _buildWorkspace0,
                1 => _buildWorkspace1,
                2 => _buildWorkspace2,
                3 => _buildWorkspace3,
                4 => _buildWorkspace4,
                5 => _buildWorkspace5,
                6 => _buildWorkspace6,
                _ => _buildWorkspace7,
            };
        }

        private void TryAttachBuildOwner(
            ref SystemState state,
            Entity recordEntity,
            NavigationSharedFlowFieldBuildRequest buildRequest,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            int tick)
        {
            Entity cohortEntity = buildRequest.JobRequest.Entity;
            if (!state.EntityManager.Exists(cohortEntity) ||
                !_cohortQuery.Matches(cohortEntity))
            {
                return;
            }

            NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                NavigationFlowFieldState>(cohortEntity);
            NavigationFlowFieldRequest request = state.EntityManager.GetComponentData<
                NavigationFlowFieldRequest>(cohortEntity);
            // Worker 运行期间换代的所有者不消费旧结果，后续当前版本仍可正常命中 Store
            if ((fieldState.Status != NavigationPathStatus.Searching &&
                 fieldState.Status != NavigationPathStatus.Pending) ||
                !TryCreateKey(ref grid, request, overlay, out var currentKey, out _) ||
                !KeysEqual(buildRequest.Key, currentKey) ||
                !TryResolveCoveredStartCell(
                    state.EntityManager,
                    recordEntity,
                    ref grid,
                    request.PathRequest,
                    overlay,
                    out int startCellIndex))
            {
                return;
            }

            AttachRecord(
                ref state,
                cohortEntity,
                recordEntity,
                request.PathRequest.Version,
                startCellIndex,
                cacheHit: false,
                tick);
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
                        request,
                        overlay,
                        out NavigationFlowFieldKey currentKey,
                        out _) ||
                    !KeysEqual(key, currentKey))
                {
                    continue;
                }

                if (!TryResolveCoveredStartCell(
                        state.EntityManager,
                        recordEntity,
                        ref grid,
                        request.PathRequest,
                        overlay,
                        out int startCellIndex))
                {
                    FailRequest(
                        ref state,
                        cohortEntity,
                        NavigationPathFailureReason.NoPath,
                        tick);
                    continue;
                }

                // 稳定顺序中的第一个消费者记为构建所有者，其余都计入共享命中
                bool cacheHit = ownerAttached;
                AttachRecord(
                    ref state,
                    cohortEntity,
                    recordEntity,
                    request.PathRequest.Version,
                    startCellIndex,
                    cacheHit,
                    tick);
                if (cacheHit)
                {
                    schedulerState.CumulativeSharedHitCount++;
                    schedulerState.CumulativeCoverageTileReuseCount++;
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
            int projectedStartCellIndex,
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
            fieldState.ProjectedStartCellIndex = projectedStartCellIndex;
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

        private static bool TryResolveCoveredStartCell(
            EntityManager entityManager,
            Entity recordEntity,
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            out int startCellIndex)
        {
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    request.StartPosition,
                    request.AgentRadius,
                    request.ClearanceMargin,
                    request.MaximumProjectionRadiusInCells,
                    overlay,
                    out startCellIndex))
            {
                return false;
            }

            DynamicBuffer<NavigationFlowFieldCell> field = entityManager.GetBuffer<
                NavigationFlowFieldCell>(recordEntity, true);
            int minimum = 0;
            int maximum = field.Length - 1;
            // Field 按 Cell 索引排序，发布 251 个导航分组时不扫描整份目标场
            while (minimum <= maximum)
            {
                int index = minimum + ((maximum - minimum) >> 1);
                int currentCellIndex = field[index].CellIndex;
                if (currentCellIndex == startCellIndex)
                {
                    return true;
                }

                if (currentCellIndex < startCellIndex)
                {
                    minimum = index + 1;
                }
                else
                {
                    maximum = index - 1;
                }
            }

            return false;
        }

        private void FailRequest(
            ref SystemState state,
            Entity cohortEntity,
            NavigationPathFailureReason failureReason,
            int tick)
        {
            NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                NavigationFlowFieldState>(cohortEntity);
            fieldState.Status = NavigationPathStatus.Failed;
            fieldState.FailureReason = failureReason;
            state.EntityManager.SetComponentData(cohortEntity, fieldState);

            NavigationFlowFieldQueueState queueState = state.EntityManager.GetComponentData<
                NavigationFlowFieldQueueState>(cohortEntity);
            int waitTicks = queueState.StartedTick >= 0
                ? queueState.StartedTick - queueState.EnqueuedTick
                : tick - queueState.EnqueuedTick;
            CompleteQueue(
                ref state,
                cohortEntity,
                math.max(0, waitTicks),
                NavigationPathStatus.Failed,
                tick);
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
                if (!TryCreateKey(ref grid, request, overlay, out var currentKey, out _) ||
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
                if (!TryCreateKey(ref grid, request, overlay, out var currentKey, out _) ||
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
                if (!TryCreateKey(ref grid, request, overlay, out var currentKey, out _) ||
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
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            ref NavigationFlowFieldSchedulerState schedulerState)
        {
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            // 先处理局部失效，随后悬空 Handle 检查只处理删除或换代情况
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

                if (record.Key.CoverageMode == NavigationFlowFieldCoverageMode.GoalRegion)
                {
                    int changedTileCount = CountChangedCoverageTiles(
                        state.EntityManager,
                        recordEntity,
                        overlayClusters);
                    bool alreadyPending = record.RefreshPending != 0;
                    record.RefreshPending = 1;
                    record.PendingCoverageTileCount = math.max(1, changedTileCount);
                    state.EntityManager.SetComponentData(recordEntity, record);
                    if (!alreadyPending)
                    {
                        schedulerState.CumulativeCoverageTileInvalidationCount +=
                            record.PendingCoverageTileCount;
                        // 只让一个稳定消费者认领刷新，其余 Cohort 继续持有同一个 Record
                        ResetFirstRecordConsumer(ref state, recordEntity);
                    }
                    continue;
                }

                // 旧 Corridor Record 仍按整条路线换代，避免改变阶段三的严格回归语义
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
                if (state.EntityManager.HasComponent<AniMovementCohortPathState>(cohortEntity) &&
                    state.EntityManager.GetComponentData<AniMovementCohortPathState>(cohortEntity)
                        .RouteMode == AniMovementCohortRouteMode.Direct)
                {
                    // 直达路线按设计不拥有共享 Record，空 Handle 不是需要修复的悬空引用
                    if (handle.Record != Entity.Null)
                    {
                        state.EntityManager.SetComponentData(
                            cohortEntity,
                            default(NavigationFlowFieldHandle));
                    }
                    continue;
                }
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

        // 目标场刷新只需要一个消费者重新排队，其他 Handle 在原位发布后自然读取新数据
        private void ResetFirstRecordConsumer(ref SystemState state, Entity recordEntity)
        {
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            SortEntities(cohorts);
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index];
                NavigationFlowFieldHandle handle = state.EntityManager.GetComponentData<
                    NavigationFlowFieldHandle>(cohortEntity);
                if (handle.Record != recordEntity)
                {
                    continue;
                }

                state.EntityManager.SetComponentData(
                    cohortEntity,
                    default(NavigationFlowFieldHandle));
                NavigationFlowFieldState fieldState = state.EntityManager.GetComponentData<
                    NavigationFlowFieldState>(cohortEntity);
                fieldState.Status = NavigationPathStatus.Pending;
                fieldState.FailureReason = NavigationPathFailureReason.None;
                state.EntityManager.SetComponentData(cohortEntity, fieldState);
                return;
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

        // 同一目标最多保留一个待刷新 Record，版本更小者先建立，选择结果不依赖 Query 顺序
        private Entity FindPendingRefreshRecord(
            ref SystemState state,
            NavigationFlowFieldKey key)
        {
            using NativeArray<Entity> records = _recordQuery.ToEntityArray(Allocator.Temp);
            Entity selected = Entity.Null;
            uint selectedVersion = uint.MaxValue;
            for (int index = 0; index < records.Length; index++)
            {
                NavigationSharedFlowFieldRecord record = state.EntityManager.GetComponentData<
                    NavigationSharedFlowFieldRecord>(records[index]);
                if (record.RefreshPending == 0 ||
                    !KeysEqual(record.Key, key) ||
                    record.RecordVersion >= selectedVersion)
                {
                    continue;
                }

                selected = records[index];
                selectedVersion = record.RecordVersion;
            }
            return selected;
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
            _buildWorkspace0 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace1 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace2 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace3 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace4 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace5 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace6 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
            _buildWorkspace7 = NavigationSharedFlowFieldWorkspace.Create(
                cellCount,
                clusterCount,
                abstractCount);
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

        private void EnsureActiveBatchStorage(int overlayCellCount, int overlayClusterCount)
        {
            if (!_activeRequests.IsCreated)
            {
                _activeRequests = new NativeArray<NavigationSharedFlowFieldBuildRequest>(
                    MaximumSupportedConcurrentBuilds,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                _activeResults = new NativeArray<NavigationFlowFieldJobResult>(
                    MaximumSupportedConcurrentBuilds,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            if (_activeOverlay.IsCreated && _activeOverlay.Length != overlayCellCount)
            {
                _activeOverlay.Dispose();
                _activeOverlay = default;
            }
            if (!_activeOverlay.IsCreated && overlayCellCount > 0)
            {
                _activeOverlay = new NativeArray<NavigationDynamicOverlayCell>(
                    overlayCellCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            if (_activeOverlayClusters.IsCreated &&
                _activeOverlayClusters.Length != overlayClusterCount)
            {
                _activeOverlayClusters.Dispose();
                _activeOverlayClusters = default;
            }
            if (!_activeOverlayClusters.IsCreated && overlayClusterCount > 0)
            {
                _activeOverlayClusters = new NativeArray<NavigationDynamicOverlayCluster>(
                    overlayClusterCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }
        }

        // Grid 就绪后一次性准备构建工作区和 Record 槽，正式采样只复用已有内存
        private void EnsureWarmStorage(
            ref SystemState state,
            ref NavigationGridBlob grid,
            int overlayCellCount,
            int overlayClusterCount)
        {
            EnsureWorkspaceCapacity(
                MaximumSupportedConcurrentBuilds,
                grid.Cells.Length,
                grid.Clusters.Length,
                grid.PortalNodes.Length);
            EnsureActiveBatchStorage(overlayCellCount, overlayClusterCount);
            EnsureRecordPool(
                ref state,
                grid.Cells.Length,
                grid.Clusters.Length,
                grid.PortalNodes.Length);
        }

        private void EnsureRecordPool(
            ref SystemState state,
            int cellCount,
            int clusterCount,
            int portalCount)
        {
            if (_recordPoolInitialized)
            {
                return;
            }

            _recordSlotCapacityBytes = 0;
            for (int index = 0; index < PreallocatedRecordCount; index++)
            {
                Entity recordEntity = state.EntityManager.CreateEntity(
                    typeof(NavigationSharedFlowFieldRecord),
                    typeof(NavigationCorridorCluster),
                    typeof(NavigationCorridorPortal),
                    typeof(NavigationHierarchicalWaypoint),
                    typeof(NavigationFlowFieldCell),
                    typeof(NavigationFlowFieldCoverageTile),
                    typeof(NavigationSharedFlowFieldRecordPoolSlot));

                DynamicBuffer<NavigationCorridorCluster> clusters = state.EntityManager
                    .GetBuffer<NavigationCorridorCluster>(recordEntity);
                DynamicBuffer<NavigationCorridorPortal> portals = state.EntityManager
                    .GetBuffer<NavigationCorridorPortal>(recordEntity);
                DynamicBuffer<NavigationHierarchicalWaypoint> waypoints = state.EntityManager
                    .GetBuffer<NavigationHierarchicalWaypoint>(recordEntity);
                DynamicBuffer<NavigationFlowFieldCell> field = state.EntityManager
                    .GetBuffer<NavigationFlowFieldCell>(recordEntity);
                DynamicBuffer<NavigationFlowFieldCoverageTile> coverageTiles = state.EntityManager
                    .GetBuffer<NavigationFlowFieldCoverageTile>(recordEntity);

                // 容量按最坏目标场准备，发布时 Clear 和 AddRange 不再触发 Native 扩容
                clusters.EnsureCapacity(clusterCount);
                portals.EnsureCapacity(portalCount);
                waypoints.EnsureCapacity(portalCount + 2);
                field.EnsureCapacity(cellCount);
                coverageTiles.EnsureCapacity(clusterCount);
                _recordPool.Add(recordEntity);

                if (_recordSlotCapacityBytes == 0)
                {
                    _recordSlotCapacityBytes =
                        UnsafeUtility.SizeOf<NavigationSharedFlowFieldRecord>() +
                        (long)clusters.Capacity *
                        UnsafeUtility.SizeOf<NavigationCorridorCluster>() +
                        (long)portals.Capacity *
                        UnsafeUtility.SizeOf<NavigationCorridorPortal>() +
                        (long)waypoints.Capacity *
                        UnsafeUtility.SizeOf<NavigationHierarchicalWaypoint>() +
                        (long)field.Capacity * UnsafeUtility.SizeOf<NavigationFlowFieldCell>() +
                        (long)coverageTiles.Capacity *
                        UnsafeUtility.SizeOf<NavigationFlowFieldCoverageTile>();
                }
            }

            _recordPoolByteCount = _recordPool.Length * _recordSlotCapacityBytes;
            _recordPoolInitialized = true;
        }

        private void ClearRecordPool(ref SystemState state)
        {
            if (_recordPool.IsCreated)
            {
                for (int index = 0; index < _recordPool.Length; index++)
                {
                    Entity recordEntity = _recordPool[index];
                    if (state.EntityManager.Exists(recordEntity))
                    {
                        state.EntityManager.DestroyEntity(recordEntity);
                    }
                }
                _recordPool.Clear();
            }

            _recordPoolByteCount = 0;
            _recordSlotCapacityBytes = 0;
            _recordPoolInitialized = false;
        }

        private void DisposeRecordPool(ref SystemState state)
        {
            ClearRecordPool(ref state);
            if (_recordPool.IsCreated)
            {
                _recordPool.Dispose();
            }
            _recordPool = default;
        }

        // Job 持有独立 Overlay 副本，主线程更新动态障碍时无需等待构建
        private void CopyOverlaySnapshot(
            NativeArray<NavigationDynamicOverlayCell> overlay,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            if (overlay.IsCreated)
            {
                // Cell 和 Cluster 两层都复制，求解成本与局部失效签名来自同一版本
                NativeArray<NavigationDynamicOverlayCell>.Copy(
                    overlay,
                    _activeOverlay);
            }
            if (overlayClusters.IsCreated)
            {
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
            for (int index = 0; index < _activeBuildCount; index++)
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
            NavigationFlowFieldRequest flowRequest,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            out NavigationFlowFieldKey key,
            out NavigationPathFailureReason failureReason)
        {
            key = default;
            failureReason = NavigationPathFailureReason.InvalidRequest;
            NavigationPathRequest request = flowRequest.PathRequest;
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
            if (grid.Cells[startCellIndex].RegionId <= 0 ||
                grid.Cells[startCellIndex].RegionId != grid.Cells[endCellIndex].RegionId)
            {
                failureReason = NavigationPathFailureReason.RegionMismatch;
                return false;
            }

            float requiredClearance = NavigationGridCost.CalculateRequiredClearance(
                ref grid,
                request.AgentRadius,
                request.ClearanceMargin);
            // 存储浮点位模式而非舍入值，哈希相等必然代表 Solver 输入完全一致
            key = new NavigationFlowFieldKey
            {
                StartCellIndex = flowRequest.CoverageMode ==
                                 NavigationFlowFieldCoverageMode.GoalRegion
                    ? -1
                    : startCellIndex,
                EndCellIndex = endCellIndex,
                RequiredClearanceBits = math.asint(requiredClearance),
                ClearancePenaltyWeightBits = math.asint(request.ClearancePenaltyWeight),
                CoverageMode = flowRequest.CoverageMode,
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

        // Worker 结果发布前直接按工作区检查快照，避免先改写活动 Record 再发现版本过期
        private static uint CalculateWorkspaceOverlaySignature(
            ref NativeList<int> clusters,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            uint hash = 2166136261u;
            for (int index = 0; index < clusters.Length; index++)
            {
                int clusterIndex = clusters[index];
                hash ^= (uint)clusterIndex;
                hash *= 16777619u;
                hash ^= GetOverlayClusterVersion(overlayClusters, clusterIndex);
                hash *= 16777619u;
            }
            return hash == 0u ? 1u : hash;
        }

        // 覆盖块保存独立版本，因此一次 Overlay 更新可以精确统计实际相交的 Cluster
        private static int CountChangedCoverageTiles(
            EntityManager entityManager,
            Entity recordEntity,
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters)
        {
            DynamicBuffer<NavigationFlowFieldCoverageTile> tiles = entityManager.GetBuffer<
                NavigationFlowFieldCoverageTile>(recordEntity, true);
            int changedCount = 0;
            for (int index = 0; index < tiles.Length; index++)
            {
                NavigationFlowFieldCoverageTile tile = tiles[index];
                if (tile.DynamicOverlayVersion != GetOverlayClusterVersion(
                        overlayClusters,
                        tile.ClusterId))
                {
                    changedCount++;
                }
            }
            return changedCount;
        }

        private static uint GetOverlayClusterVersion(
            NativeArray<NavigationDynamicOverlayCluster> overlayClusters,
            int clusterIndex)
        {
            return overlayClusters.IsCreated &&
                   clusterIndex >= 0 &&
                   clusterIndex < overlayClusters.Length
                ? overlayClusters[clusterIndex].Version
                : 0u;
        }

        private static bool KeysEqual(
            NavigationFlowFieldKey left,
            NavigationFlowFieldKey right)
        {
            return left.StartCellIndex == right.StartCellIndex &&
                   left.EndCellIndex == right.EndCellIndex &&
                   left.RequiredClearanceBits == right.RequiredClearanceBits &&
                   left.ClearancePenaltyWeightBits == right.ClearancePenaltyWeightBits &&
                   left.CoverageMode == right.CoverageMode;
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
            comparison = left.ClearancePenaltyWeightBits.CompareTo(
                right.ClearancePenaltyWeightBits);
            if (comparison != 0) return comparison;
            return left.CoverageMode.CompareTo(right.CoverageMode);
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
            // 活动 Job 拥有工作区写权限，此时沿用上一份稳定容量快照
            if (!_activeJobScheduled)
            {
                schedulerState.WorkspaceByteCount = CalculateWorkspaceByteCount();
            }
            state.EntityManager.SetComponentData(_storeEntity, schedulerState);
        }

        private long CalculateWorkspaceByteCount()
        {
            long bytes = 0;
            bytes += (long)_cellCosts.Length * UnsafeUtility.SizeOf<float>();
            bytes += (long)_cellHeap.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_cellHeapPositions.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_cellGenerations.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_clusterGenerations.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_abstractCosts.Length * UnsafeUtility.SizeOf<float>();
            bytes += (long)_abstractEndCosts.Length * UnsafeUtility.SizeOf<float>();
            bytes += (long)_abstractParents.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_abstractHeap.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_abstractHeapPositions.Length * UnsafeUtility.SizeOf<int>();
            bytes += (long)_abstractGenerations.Length * UnsafeUtility.SizeOf<int>();
            bytes += _buildWorkspace0.CalculateCapacityBytes();
            bytes += _buildWorkspace1.CalculateCapacityBytes();
            bytes += _buildWorkspace2.CalculateCapacityBytes();
            bytes += _buildWorkspace3.CalculateCapacityBytes();
            bytes += _buildWorkspace4.CalculateCapacityBytes();
            bytes += _buildWorkspace5.CalculateCapacityBytes();
            bytes += _buildWorkspace6.CalculateCapacityBytes();
            bytes += _buildWorkspace7.CalculateCapacityBytes();
            bytes += (long)_activeRequests.Length *
                     UnsafeUtility.SizeOf<NavigationSharedFlowFieldBuildRequest>();
            bytes += (long)_activeResults.Length * UnsafeUtility.SizeOf<NavigationFlowFieldJobResult>();
            bytes += (long)_activeOverlay.Length *
                     UnsafeUtility.SizeOf<NavigationDynamicOverlayCell>();
            bytes += (long)_activeOverlayClusters.Length *
                     UnsafeUtility.SizeOf<NavigationDynamicOverlayCluster>();
            bytes += _recordPoolByteCount;
            return bytes;
        }

        private void DisposeActiveBatch()
        {
            // 完成后只结束活动所有权，容器留给下一批继续复用
            _activeBuildCount = 0;
            _activeJobHandle = default;
            _activeJobScheduled = false;
        }

        private void DisposeActiveBatchStorage()
        {
            if (_activeRequests.IsCreated) _activeRequests.Dispose();
            if (_activeResults.IsCreated) _activeResults.Dispose();
            if (_activeOverlay.IsCreated) _activeOverlay.Dispose();
            if (_activeOverlayClusters.IsCreated) _activeOverlayClusters.Dispose();
            _activeRequests = default;
            _activeResults = default;
            _activeOverlay = default;
            _activeOverlayClusters = default;
        }

        private void DisposeWorkspaces()
        {
            // 工作区由系统独占，Grid 换代或 World 销毁时成组释放
            _buildWorkspace0.Dispose();
            _buildWorkspace1.Dispose();
            _buildWorkspace2.Dispose();
            _buildWorkspace3.Dispose();
            _buildWorkspace4.Dispose();
            _buildWorkspace5.Dispose();
            _buildWorkspace6.Dispose();
            _buildWorkspace7.Dispose();
            _buildWorkspace0 = default;
            _buildWorkspace1 = default;
            _buildWorkspace2 = default;
            _buildWorkspace3 = default;
            _buildWorkspace4 = default;
            _buildWorkspace5 = default;
            _buildWorkspace6 = default;
            _buildWorkspace7 = default;
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
