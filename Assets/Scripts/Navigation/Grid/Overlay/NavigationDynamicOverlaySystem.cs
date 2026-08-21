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

            if (deltas.IsEmpty)
            {
                overlayState.LastUpdatedCellCount = 0;
                overlayState.LastUpdatedClusterCount = 0;
                entityManager.SetComponentData(gridEntity, overlayState);
                return;
            }

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
                updatedClusterCount += NavigationDynamicOverlayAlgorithms.MarkAffectedClusters(
                    ref grid,
                    delta.CellIndex,
                    clusters,
                    nextVersion);
            }

            deltas.Clear();
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

            overlayState.Version = overlayState.Version == 0 ? 1u : overlayState.Version;
            overlayState.Initialized = 1;
            entityManager.SetComponentData(gridEntity, overlayState);
        }
    }
}
