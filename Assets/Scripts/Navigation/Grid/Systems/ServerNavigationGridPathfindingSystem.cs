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
        // 单批限制控制主线程收集和写回成本，后续阶段可改为配置或预算
        private const int MaximumRequestsPerBatch = 32;

        // 查询只描述 ECS 输入输出，活动 Job 的 NativeContainer 由本 System 独占
        private EntityQuery _gridQuery;
        private EntityQuery _requestQuery;

        // 同一时刻只允许一个批次运行，防止多批次并发改写共享 Scratch 数组
        private JobHandle _activeJobHandle;
        private bool _activeJobScheduled;

        // 活动批次冻结调度时的 Grid 和请求，后续 Component 修改不会改变 Job 输入
        private BlobAssetReference<NavigationGridBlob> _activeGrid;
        private NativeArray<NavigationPathJobRequest> _activeRequests;
        private NativeArray<NavigationPathJobResult> _activeResults;
        private NativeList<int> _activePathCells;
        private NativeArray<NavigationDynamicOverlayCell> _activeOverlay;

        // Scratch 容量按 Grid Cell 数增长，不按请求数量重复分配
        private NativeArray<float> _gCosts;
        private NativeArray<int> _parents;
        private NativeArray<int> _heap;
        private NativeArray<int> _heapPositions;
        private NativeArray<int> _nodeGenerations;

        // Generation 递增后每个请求都能区分数组中的旧数据
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
                // 未完成时直接让出本帧，避免 Complete 把路径搜索变成主线程同步点
                if (!_activeJobHandle.IsCompleted)
                {
                    return;
                }

                // IsCompleted 为真后 Complete 只负责建立内存可见性和传播 Job 异常
                _activeJobHandle.Complete();
                ApplyActiveResults(ref state);
                SetPathJobActivity(ref state, false);
                DisposeActiveBatch();
                // 留出一个无读者 Tick 让 Overlay 优先发布，避免持续请求使差量长期饥饿
                return;
            }

            if (_requestQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int gridCount = _gridQuery.CalculateEntityCount();
            if (gridCount == 0)
            {
                // SubScene 尚未加载时保留 Pending 使请求能在 Grid 出现后继续执行
                return;
            }

            if (gridCount != 1)
            {
                // 多个 Grid 缺少明确选择规则，立即失败比隐式选择更可诊断
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
                !state.EntityManager.GetBuffer<NavigationDynamicOverlayDelta>(gridEntity).IsEmpty)
            {
                // 差量等待发布时不启动新读者，Overlay 将在所有活动批次退出后优先更新
                return;
            }

            DynamicBuffer<NavigationDynamicOverlayCell> overlay =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(gridEntity)
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

        // 冻结并稳定排序请求，Persistent 容器保留到 Job 结果写回结束
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

            // 终态和 Cancelled 都保留到调用方同时提交新的 Request 与 Pending State
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

            // 稳定排序使每帧预算不足时也能稳定选择相同批次成员
            SortEntities(pendingEntities);
            int batchCount = math.min(MaximumRequestsPerBatch, pendingEntities.Length);
            EnsureScratchCapacity(grid.Value.Cells.Length);
            EnsureGenerationCapacity(batchCount);

            // 活动 Grid 引用保持到结果写回结束，避免使用下一帧新查询得到的引用
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

                // Searching 表示该版本已进入活动批次，调用方取消时应改为 Cancelled
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
            // 不把 Handle 立即 Complete 搜索与后续主线程帧并行执行
            _activeJobHandle = pathfindingJob.Schedule();
            _activeJobScheduled = true;
            SetPathJobActivity(ref state, true);
        }

        // 写回前复核实体和版本，再把路径 Cell 转为世界坐标
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
                // 状态与两个版本共同覆盖取消、重新排队和输入替换窗口
                if (pathState.Status != NavigationPathStatus.Searching ||
                    pathState.RequestVersion != result.RequestVersion ||
                    currentRequest.Version != result.RequestVersion)
                {
                    continue;
                }

                DynamicBuffer<NavigationPathWaypoint> waypoints =
                    state.EntityManager.GetBuffer<NavigationPathWaypoint>(entity);
                waypoints.Clear();
                // Job 结果只保存连续数组切片，写回前必须验证边界防止损坏 Buffer
                bool validPathRange =
                    result.PathOffset >= 0 &&
                    result.PathLength >= 0 &&
                    result.PathOffset + result.PathLength <= _activePathCells.Length;
                if (result.Status == NavigationPathStatus.Succeeded && validPathRange)
                {
                    // 世界坐标统一由同一活动 Grid 生成，不接受调用方自行重建高度
                    ref NavigationGridBlob grid = ref _activeGrid.Value;
                    for (int pathIndex = 0; pathIndex < result.PathLength; pathIndex++)
                    {
                        int cellIndex = _activePathCells[result.PathOffset + pathIndex];
                        waypoints.Add(new NavigationPathWaypoint
                        {
                            CellIndex = cellIndex,
                            Position = NavigationGridPathAlgorithms.GetCellWorldPosition(
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

        // Grid 缺失或数量异常时只终结仍处于 Pending 的请求
        // Searching 请求属于已有活动批次，不能由当前帧状态覆盖
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

        // Scratch 容量严格跟随活动 Grid 的 Cell 数
        // 尺寸变化时整体重建以保持单一所有权和清晰生命周期
        private void EnsureScratchCapacity(int cellCount)
        {
            if (_scratchCellCount == cellCount && _gCosts.IsCreated)
            {
                return;
            }

            DisposeScratch();
            // Scratch 改变尺寸后所有旧 Generation 都失效
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

        // generation 为每个请求保留唯一正整数区间
        // 溢出前统一清零避免旧槽位与新请求获得相同标识
        private void EnsureGenerationCapacity(int batchCount)
        {
            if (_nextGeneration > 0 && _nextGeneration <= int.MaxValue - batchCount)
            {
                return;
            }

            // 即将溢出时清零未被 Job 使用的标记，并从一重新编号
            for (int cellIndex = 0; cellIndex < _nodeGenerations.Length; cellIndex++)
            {
                _nodeGenerations[cellIndex] = 0;
            }

            _nextGeneration = 1;
        }

        // 批次上限很小，使用原地插入排序避免额外容器和比较器分配
        // 排序键包含 Version 防止 Entity Index 复用时出现不稳定次序
        private static void SortEntities(NativeList<Entity> entities)
        {
            // EntityQuery 顺序不是公共契约，显式按 Index 和 Version 排序保证批次选择稳定
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

        // Index 是主键 Version 是同 Index 下的稳定次键
        private static bool IsEntityAfter(Entity left, Entity right)
        {
            return left.Index > right.Index ||
                   (left.Index == right.Index && left.Version > right.Version);
        }

        // 活动批次容器只能在 Handle 完成后释放
        // 字段恢复默认值使重复销毁和下一批调度都能安全判断所有权
        private void DisposeActiveBatch()
        {
            // 只能在 Handle 完成后调用，活动容器可能仍被 Burst Job 持有
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

        // Scratch 生命周期属于当前 System 和 World
        // 释放后清空容量标记防止 World 重建访问旧 NativeContainer
        private void DisposeScratch()
        {
            // Scratch 不属于活动结果，但同样必须保证当前没有 Job 正在使用
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
