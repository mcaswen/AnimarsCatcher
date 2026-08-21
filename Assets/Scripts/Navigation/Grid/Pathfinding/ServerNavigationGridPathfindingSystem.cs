using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在 Server 或 Local World 中异步处理确定性 Grid 路径请求
    /// </summary>
    [WorldSystemFilter(
        WorldSystemFilterFlags.ServerSimulation |
        WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AniGridMovementSystemGroup))]
    public partial struct ServerNavigationGridPathfindingSystem : ISystem
    {
        // 限制每批请求数，避免主线程收集和写回结果时出现过长卡顿
        private const int MaximumRequestsPerBatch = 32;

        // 查询只负责获取 ECS 输入；正在运行的任务及其 NativeContainer 由本系统独占管理
        private EntityQuery _gridQuery;
        private EntityQuery _requestQuery;

        // 同一时间只运行一个批次，避免多个任务同时写入共享临时数组
        private JobHandle _activeJobHandle;
        private bool _activeJobScheduled;

        // 批次调度时会复制导航网格引用和请求，之后的组件修改不会影响正在运行的任务
        private BlobAssetReference<NavigationGridBlob> _activeGrid;
        private NativeArray<NavigationPathJobRequest> _activeRequests;
        private NativeArray<NavigationPathJobResult> _activeResults;
        private NativeList<int> _activePathCells;
        private NativeArray<NavigationDynamicOverlayCell> _activeOverlay;

        // 临时数组按导航网格格子数分配，并在不同批次之间复用
        private NativeArray<float> _gCosts;
        private NativeArray<int> _parents;
        private NativeArray<int> _heap;
        private NativeArray<int> _heapPositions;
        private NativeArray<int> _nodeGenerations;

        // 每条请求使用新的 Generation，便于区分临时数组中残留的旧数据
        private int _scratchCellCount;
        private int _nextGeneration;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
            _requestQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationPathRequest>(),
                ComponentType.ReadWrite<NavigationPathState>(),
                ComponentType.ReadWrite<NavigationPathWaypoint>());
            _nextGeneration = 1;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_activeJobScheduled)
            {
                // 后台任务尚未结束时直接等待下一帧，不调用 Complete 阻塞主线程
                if (!_activeJobHandle.IsCompleted)
                {
                    return;
                }

                // 确认任务结束后再调用 Complete，用于同步内存并传播任务异常
                _activeJobHandle.Complete();
                ApplyActiveResults(ref state);
                SetPathJobActivity(ref state, false);
                DisposeActiveBatch();
                // 每批完成后留一帧给动态障碍层写入，避免连续寻路让障碍更新一直等不到机会
                return;
            }

            if (_requestQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int gridCount = _gridQuery.CalculateEntityCount();
            if (gridCount == 0)
            {
                // SubScene 尚未加载导航网格时保留 Pending，网格出现后请求可以继续执行
                return;
            }

            if (gridCount != 1)
            {
                // 同一 World 出现多张导航网格时没有可靠选择规则，因此明确失败并报告配置问题
                FailPendingRequests(
                    ref state,
                    NavigationPathFailureReason.InvalidGrid);
                return;
            }

            Entity gridEntity = _gridQuery.GetSingletonEntity();
            NavigationGridReference gridReference =
                state.EntityManager.GetComponentData<NavigationGridReference>(gridEntity);
            if (!gridReference.Value.IsCreated)
            {
                FailPendingRequests(
                    ref state,
                    NavigationPathFailureReason.InvalidGrid);
                return;
            }

            if (state.EntityManager.HasBuffer<NavigationDynamicOverlayDelta>(gridEntity) &&
                !state.EntityManager.GetBuffer<NavigationDynamicOverlayDelta>(
                    gridEntity,
                    isReadOnly: true).IsEmpty)
            {
                // 有动态障碍变化等待写入时不启动新任务，让障碍层先完成更新
                return;
            }

            DynamicBuffer<NavigationDynamicOverlayCell> overlay =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(
                        gridEntity,
                        isReadOnly: true)
                    : default;
            SchedulePendingRequests(ref state, gridReference.Value, overlay);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_activeJobScheduled)
            {
                _activeJobHandle.Complete();
                SetPathJobActivity(ref state, false);
            }

            DisposeActiveBatch();
            DisposeScratch();
        }

        // 复制并排序本批请求；这些 Persistent 容器会保留到后台任务完成并写回结果
        private void SchedulePendingRequests(
            ref SystemState state,
            BlobAssetReference<NavigationGridBlob> grid,
            DynamicBuffer<NavigationDynamicOverlayCell> overlay)
        {
            using NativeArray<Entity> requestEntities =
                _requestQuery.ToEntityArray(Allocator.Temp);
            using var pendingEntities = new NativeList<Entity>(
                requestEntities.Length,
                Allocator.Temp);

            // 已结束或取消的请求保持原状态，直到调用方同时写入新请求和 Pending 状态
            for (int entityIndex = 0; entityIndex < requestEntities.Length; entityIndex++)
            {
                Entity entity = requestEntities[entityIndex];
                NavigationPathState pathState =
                    state.EntityManager.GetComponentData<NavigationPathState>(entity);
                if (pathState.Status == NavigationPathStatus.Pending)
                {
                    pendingEntities.Add(entity);
                }
            }

            if (pendingEntities.IsEmpty)
            {
                return;
            }

            // 固定排序让请求超过单批上限时，每帧都能按明确顺序选择任务
            SortEntities(pendingEntities);
            int batchCount = math.min(MaximumRequestsPerBatch, pendingEntities.Length);
            EnsureScratchCapacity(grid.Value.Cells.Length);
            EnsureGenerationCapacity(batchCount);

            // 整个批次都使用调度时取得的导航网格，写回时不会混用下一帧的新引用
            _activeGrid = grid;
            _activeOverlay = overlay.IsCreated ? overlay.AsNativeArray() : default;
            _activeRequests = new NativeArray<NavigationPathJobRequest>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _activeResults = new NativeArray<NavigationPathJobResult>(
                batchCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _activePathCells = new NativeList<int>(
                math.max(16, batchCount * 8),
                Allocator.Persistent);

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                Entity entity = pendingEntities[batchIndex];
                NavigationPathRequest request =
                    state.EntityManager.GetComponentData<NavigationPathRequest>(entity);
                _activeRequests[batchIndex] = new NavigationPathJobRequest
                {
                    Entity = entity,
                    Request = request,
                };

                // Searching 表示该版本已交给后台任务；调用方若要放弃，应明确改为 Cancelled
                NavigationPathState pathState =
                    state.EntityManager.GetComponentData<NavigationPathState>(entity);
                pathState.Status = NavigationPathStatus.Searching;
                pathState.FailureReason = NavigationPathFailureReason.None;
                pathState.RequestVersion = request.Version;
                pathState.ProjectedStartCellIndex = -1;
                pathState.ProjectedEndCellIndex = -1;
                pathState.WaypointCount = 0;
                pathState.ExpandedNodeCount = 0;
                pathState.TotalCost = 0f;
                state.EntityManager.SetComponentData(entity, pathState);
                state.EntityManager.GetBuffer<NavigationPathWaypoint>(entity).Clear();
            }

            var pathfindingJob = new NavigationGridPathfindingJob
            {
                Grid = grid,
                Requests = _activeRequests,
                Results = _activeResults,
                PathCells = _activePathCells,
                GCosts = _gCosts,
                Parents = _parents,
                Heap = _heap,
                HeapPositions = _heapPositions,
                NodeGenerations = _nodeGenerations,
                DynamicOverlay = _activeOverlay,
                GenerationStart = _nextGeneration,
            };
            _nextGeneration += batchCount;
            // 私有句柄用于跨帧检查任务；System Dependency 则保证 ECS 结构变化前等待读取完成
            _activeJobHandle = pathfindingJob.Schedule(state.Dependency);
            state.Dependency = _activeJobHandle;
            _activeJobScheduled = true;
            SetPathJobActivity(ref state, true);
        }

        // 写回前复核 Entity 和版本，再把路径 Cell 转为世界坐标
        private void ApplyActiveResults(ref SystemState state)
        {
            for (int resultIndex = 0; resultIndex < _activeResults.Length; resultIndex++)
            {
                NavigationPathJobResult result = _activeResults[resultIndex];
                Entity entity = result.Entity;
                if (!state.EntityManager.Exists(entity) ||
                    !state.EntityManager.HasComponent<NavigationPathRequest>(entity) ||
                    !state.EntityManager.HasComponent<NavigationPathState>(entity) ||
                    !state.EntityManager.HasBuffer<NavigationPathWaypoint>(entity))
                {
                    continue;
                }

                NavigationPathRequest currentRequest =
                    state.EntityManager.GetComponentData<NavigationPathRequest>(entity);
                NavigationPathState pathState =
                    state.EntityManager.GetComponentData<NavigationPathState>(entity);
                // 同时检查状态、请求版本和结果版本，可覆盖取消、重新排队及替换输入等情况
                if (pathState.Status != NavigationPathStatus.Searching ||
                    pathState.RequestVersion != result.RequestVersion ||
                    currentRequest.Version != result.RequestVersion)
                {
                    continue;
                }

                DynamicBuffer<NavigationPathWaypoint> waypoints =
                    state.EntityManager.GetBuffer<NavigationPathWaypoint>(entity);
                waypoints.Clear();
                // 后台结果只记录共享数组中的切片，写回前先检查范围，避免损坏路径缓冲区
                bool validPathRange =
                    result.PathOffset >= 0 &&
                    result.PathLength >= 0 &&
                    result.PathOffset + result.PathLength <= _activePathCells.Length;
                if (result.Status == NavigationPathStatus.Succeeded && validPathRange)
                {
                    // 所有路径点都用本批次的导航网格换算地面高度，避免各调用方自行计算出不同结果
                    ref NavigationGridBlob grid = ref _activeGrid.Value;
                    for (int pathIndex = 0; pathIndex < result.PathLength; pathIndex++)
                    {
                        int cellIndex = _activePathCells[result.PathOffset + pathIndex];
                        waypoints.Add(new NavigationPathWaypoint
                        {
                            CellIndex = cellIndex,
                            Position = NavigationGridQuery.GetCellWorldPosition(
                                ref grid,
                                cellIndex),
                        });
                    }
                }

                pathState.Status = validPathRange
                    ? result.Status
                    : NavigationPathStatus.Failed;
                pathState.FailureReason = validPathRange
                    ? result.FailureReason
                    : NavigationPathFailureReason.InvalidGrid;
                pathState.ProjectedStartCellIndex = result.ProjectedStartCellIndex;
                pathState.ProjectedEndCellIndex = result.ProjectedEndCellIndex;
                pathState.WaypointCount = waypoints.Length;
                pathState.ExpandedNodeCount = result.ExpandedNodeCount;
                pathState.TotalCost = result.TotalCost;
                state.EntityManager.SetComponentData(entity, pathState);
            }
        }

        // 导航网格缺失或数量异常时，只结束尚未调度的 Pending 请求
        // Searching 请求属于已经运行的批次，不能被当前帧的网格状态覆盖
        private void FailPendingRequests(
            ref SystemState state,
            NavigationPathFailureReason failureReason)
        {
            using NativeArray<Entity> requestEntities =
                _requestQuery.ToEntityArray(Allocator.Temp);
            for (int entityIndex = 0; entityIndex < requestEntities.Length; entityIndex++)
            {
                Entity entity = requestEntities[entityIndex];
                NavigationPathState pathState =
                    state.EntityManager.GetComponentData<NavigationPathState>(entity);
                if (pathState.Status != NavigationPathStatus.Pending)
                {
                    continue;
                }

                pathState.Status = NavigationPathStatus.Failed;
                pathState.FailureReason = failureReason;
                pathState.ProjectedStartCellIndex = -1;
                pathState.ProjectedEndCellIndex = -1;
                pathState.WaypointCount = 0;
                pathState.ExpandedNodeCount = 0;
                pathState.TotalCost = 0f;
                state.EntityManager.SetComponentData(entity, pathState);
                state.EntityManager.GetBuffer<NavigationPathWaypoint>(entity).Clear();
            }
        }

        // 临时数组大小与当前导航网格格子数一致；网格尺寸变化时整组重建
        private void EnsureScratchCapacity(int cellCount)
        {
            if (_scratchCellCount == cellCount && _gCosts.IsCreated)
            {
                return;
            }

            DisposeScratch();
            // 临时数组重建后，之前的所有 Generation 标记都不再有效
            _gCosts = new NativeArray<float>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _parents = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _heap = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _heapPositions = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _nodeGenerations = new NativeArray<int>(
                cellCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _scratchCellCount = cellCount;
            _nextGeneration = 1;
        }

        // Generation 为每条请求提供非零标记；即将溢出时清空数组，防止新旧请求使用相同编号
        private void EnsureGenerationCapacity(int batchCount)
        {
            if (_nextGeneration > 0 && _nextGeneration <= int.MaxValue - batchCount)
            {
                return;
            }

            // 计数即将溢出时，先清空当前没有任务使用的标记，再从 1 开始编号
            for (int cellIndex = 0; cellIndex < _nodeGenerations.Length; cellIndex++)
            {
                _nodeGenerations[cellIndex] = 0;
            }

            _nextGeneration = 1;
        }

        // 单批数量很小，原地插入排序可以避免额外容器和比较器分配
        // 排序同时使用 Entity 的 Index 和 Version，避免索引复用后顺序含糊
        private static void SortEntities(NativeList<Entity> entities)
        {
            // EntityQuery 不保证返回顺序，因此明确按 Index 和 Version 排序
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

        // 先比较 Index；Index 相同，再用 Version 决定顺序
        private static bool IsEntityAfter(Entity left, Entity right)
        {
            return left.Index > right.Index ||
                   (left.Index == right.Index && left.Version > right.Version);
        }

        // 活动批次的容器只能在任务完成后释放
        // 释放后将字段恢复默认值，下一批调度可以安全判断当前是否持有资源
        private void DisposeActiveBatch()
        {
            // 调用前必须确认任务已经完成，否则 Burst 任务可能仍在使用这些容器
            if (_activeRequests.IsCreated) _activeRequests.Dispose();
            if (_activeResults.IsCreated) _activeResults.Dispose();
            if (_activePathCells.IsCreated) _activePathCells.Dispose();
            _activeRequests = default;
            _activeResults = default;
            _activePathCells = default;
            _activeOverlay = default;
            _activeGrid = default;
            _activeJobHandle = default;
            _activeJobScheduled = false;
        }

        private void SetPathJobActivity(ref SystemState state, bool active)
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
            activity.PathJobActive = active ? (byte)1 : (byte)0;
            state.EntityManager.SetComponentData(gridEntity, activity);
        }

        // 临时数组属于当前系统和 World；销毁系统时释放，并清空容量标记
        private void DisposeScratch()
        {
            // 临时数组虽然不属于结果容器，但释放前同样要确认没有任务正在使用
            if (_gCosts.IsCreated) _gCosts.Dispose();
            if (_parents.IsCreated) _parents.Dispose();
            if (_heap.IsCreated) _heap.Dispose();
            if (_heapPositions.IsCreated) _heapPositions.Dispose();
            if (_nodeGenerations.IsCreated) _nodeGenerations.Dispose();
            _gCosts = default;
            _parents = default;
            _heap = default;
            _heapPositions = default;
            _nodeGenerations = default;
            _scratchCellCount = 0;
        }
    }
}
