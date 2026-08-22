using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在服务端应用动态障碍的新增和移除，只更新真正受影响的格子与寻路分块
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderFirst = true)]
    public partial struct NavigationDynamicOverlaySystem : ISystem
    {
        private EntityQuery _gridQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            // 查询不强制 Buffer 已存在，System 会为旧场景补齐运行时存储
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_gridQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity gridEntity = _gridQuery.GetSingletonEntity();
            EntityManager entityManager = state.EntityManager;
            EnsureOverlayStorage(entityManager, gridEntity);

            NavigationGridJobActivity activity = entityManager.GetComponentData<
                NavigationGridJobActivity>(gridEntity);
            // 寻路任务仍在读取缓冲区时延迟到下一帧，避免读写同一块内存
            if (activity.PathJobActive != 0 || activity.FlowFieldJobActive != 0)
            {
                return;
            }

            NavigationGridReference gridReference = entityManager.GetComponentData<
                NavigationGridReference>(gridEntity);
            if (!gridReference.Value.IsCreated)
            {
                return;
            }

            ref NavigationGridBlob grid = ref gridReference.Value.Value;

            DynamicBuffer<NavigationDynamicOverlayCell> cells = entityManager.GetBuffer<
                NavigationDynamicOverlayCell>(gridEntity);
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters = entityManager.GetBuffer<
                NavigationDynamicOverlayCluster>(gridEntity);
            DynamicBuffer<NavigationDynamicOverlayDelta> deltas = entityManager.GetBuffer<
                NavigationDynamicOverlayDelta>(gridEntity);
            NavigationDynamicOverlayState overlayState = entityManager.GetComponentData<
                NavigationDynamicOverlayState>(gridEntity);

            // Grid 资源替换或旧存储长度不匹配时重建全零 Overlay
            if (!NavigationDynamicOverlayAlgorithms.IsShapeValid(
                    ref grid,
                    cells,
                    clusters))
            {
                InitializeOverlay(
                    entityManager,
                    gridEntity,
                    ref grid,
                    cells,
                    clusters,
                    ref overlayState);
            }

            // 没有 Delta 时仍清零本 Tick 的更新计数，保留版本不变
            if (deltas.IsEmpty)
            {
                overlayState.LastUpdatedCellCount = 0;
                overlayState.LastUpdatedClusterCount = 0;
                entityManager.SetComponentData(gridEntity, overlayState);
                return;
            }

            // 一批 Delta 共享同一个新版本，读取方可把它们视作一次发布
            uint nextVersion = NavigationDynamicOverlayAlgorithms.NextVersion(
                overlayState.Version);
            int updatedCellCount = 0;
            int updatedClusterCount = 0;
            for (int index = 0; index < deltas.Length; index++)
            {
                NavigationDynamicOverlayDelta delta = deltas[index];
                if (!NavigationDynamicOverlayAlgorithms.ApplyDelta(
                        cells,
                        delta.CellIndex,
                        delta.BlockCountDelta,
                        delta.ExtraCostDelta,
                        delta.ClearanceReductionDelta,
                        nextVersion))
                {
                    continue;
                }

                updatedCellCount++;
                // Cell 变化会使自身及边界相邻 Cluster 的寻路缓存失效
                updatedClusterCount += NavigationDynamicOverlayAlgorithms.MarkAffectedClusters(
                    ref grid,
                    delta.CellIndex,
                    clusters,
                    nextVersion);
            }

            deltas.Clear();
            // 只有至少一个 Cell 真正变化时才发布新版本
            overlayState.Version = updatedCellCount > 0
                ? nextVersion
                : overlayState.Version;
            overlayState.LastUpdatedCellCount = updatedCellCount;
            overlayState.LastUpdatedClusterCount = updatedClusterCount;
            entityManager.SetComponentData(gridEntity, overlayState);
        }

        private static void EnsureOverlayStorage(
            EntityManager entityManager,
            Entity gridEntity)
        {
            if (!entityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity))
            {
                entityManager.AddBuffer<NavigationDynamicOverlayCell>(gridEntity);
            }

            if (!entityManager.HasBuffer<NavigationDynamicOverlayCluster>(gridEntity))
            {
                entityManager.AddBuffer<NavigationDynamicOverlayCluster>(gridEntity);
            }

            // Delta Buffer 是其他系统向 Overlay 提交变化的唯一写入口
            if (!entityManager.HasBuffer<NavigationDynamicOverlayDelta>(gridEntity))
            {
                entityManager.AddBuffer<NavigationDynamicOverlayDelta>(gridEntity);
            }

            if (!entityManager.HasComponent<NavigationDynamicOverlayState>(gridEntity))
            {
                entityManager.AddComponentData(
                    gridEntity,
                    new NavigationDynamicOverlayState { Version = 1 });
            }

            // Job 活动状态协调 Overlay 写入与寻路读取的内存安全边界
            if (!entityManager.HasComponent<NavigationGridJobActivity>(gridEntity))
            {
                entityManager.AddComponentData(
                    gridEntity,
                    default(NavigationGridJobActivity));
            }
        }

        private static void InitializeOverlay(
            EntityManager entityManager,
            Entity gridEntity,
            ref NavigationGridBlob grid,
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters,
            ref NavigationDynamicOverlayState overlayState)
        {
            cells.Clear();
            cells.ResizeUninitialized(grid.Cells.Length);
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = default;
            }

            clusters.Clear();
            clusters.ResizeUninitialized(grid.Clusters.Length);
            for (int index = 0; index < clusters.Length; index++)
            {
                clusters[index] = default;
            }

            // 重建存储不强制递增已有版本，但必须消除未初始化零值
            overlayState.Version = overlayState.Version == 0 ? 1u : overlayState.Version;
            overlayState.Initialized = 1;
            entityManager.SetComponentData(gridEntity, overlayState);
        }
    }
}
