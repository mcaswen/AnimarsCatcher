using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 仅在统一 Benchmark 场景缺少烘焙数据时提供确定性静态 Grid
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ServerNavigationGridBenchmarkGridSystem : ISystem
    {
        private const int Width = 96;
        private const int Height = 64;
        private const int ClusterSizeInCells = 8;
        private const float CellSize = 1f;
        private const float GroundHeight = 0.57f;
        private const float BaseAgentRadius = 0.35f;
        private const float BaseAgentHeight = 1.5f;

        private static readonly float3 BoundsMinimum = new(48f, -1f, 16f);
        private static readonly Unity.Entities.Hash128 GeometryHash =
            new("4b6e63686d61726b4772696447656f31");
        private static readonly Unity.Entities.Hash128 ParameterHash =
            new("4b6e63686d61726b4772696450617231");
        private static readonly Unity.Entities.Hash128 DataHash =
            new("4b6e63686d61726b4772696444617431");

        private EntityQuery _gridQuery;
        private BlobAssetReference<NavigationGridBlob> _ownedGrid;
        private Entity _ownedGridEntity;
        private bool _ownsGrid;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            // Query 用于优先复用场景中已经烘焙的 Grid
            _gridQuery = state.GetEntityQuery(ComponentType.ReadOnly<NavigationGridReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 任意现有 Grid 都优先于合成 Benchmark 数据
            int existingGridCount = _gridQuery.CalculateEntityCount();
            if (existingGridCount > 0)
            {
                // 正式烘焙 Grid 优先，Benchmark 数据源不得覆盖或创建第二份引用
                if (existingGridCount > 1)
                {
                    // 多 Grid 会破坏 Flow Field 缓存的索引归属
                    Debug.LogError("[NavigationBenchmark] More than one Navigation Grid exists");
                }

                state.Enabled = false;
                return;
            }

            // 缺少 Authoring 时只创建一次合成 Grid
            _ownedGrid = CreateBenchmarkGrid();
            // 引用实体和 Blob 都由当前 System 负责销毁
            _ownedGridEntity = state.EntityManager.CreateEntity(typeof(NavigationGridReference));
            state.EntityManager.SetComponentData(
                _ownedGridEntity,
                new NavigationGridReference { Value = _ownedGrid });
            _ownsGrid = true;
            state.Enabled = false;
            Debug.Log(
                $"[NavigationBenchmark] Created deterministic Grid {Width}x{Height}, " +
                $"hash={_ownedGrid.Value.DataHash}");
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!_ownsGrid)
            {
                // 复用正式 Grid 时不拥有其实体或 Blob 生命周期
                return;
            }

            if (state.EntityManager.Exists(_ownedGridEntity))
            {
                // 先移除引用实体再释放 Blob，避免 World 内留下指向已释放内存的组件
                state.EntityManager.DestroyEntity(_ownedGridEntity);
            }

            if (_ownedGrid.IsCreated)
            {
                _ownedGrid.Dispose();
            }

            _ownsGrid = false;
        }

        private static BlobAssetReference<NavigationGridBlob> CreateBenchmarkGrid()
        {
            // 合成数据只用于固定坐标下的路径工作量对比
            var cells = new NavigationGridCellData[Width * Height];
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = new NavigationGridCellData
                {
                    Height = GroundHeight,
                    SurfaceNormal = Vector3.up,
                    TerrainCost = 1f,
                    Walkable = true,
                };
            }

            // 开放地面仍按生产顺序派生全部底层拓扑
            NavigationGridAlgorithms.BuildConnectivity(cells, Width, Height, 0.5f);
            NavigationGridAlgorithms.CalculateClearance(cells, Width, Height, CellSize);
            NavigationGridAlgorithms.AssignClusters(cells, Width, Height, ClusterSizeInCells);
            NavigationGridAlgorithms.AssignRegions(cells, Width, Height);
            NavigationGridHierarchyBuildResult hierarchy = NavigationGridHierarchyBuilder.Build(
                cells,
                Width,
                Height,
                ClusterSizeInCells,
                CellSize);
            float3 boundsMaximum = BoundsMinimum + new float3(
                Width * CellSize,
                4f,
                Height * CellSize);
            // 固定 Hash 明确区分合成数据和正式场景烘焙结果
            return NavigationGridBlobBuilder.Create(
                cells,
                hierarchy,
                BoundsMinimum,
                boundsMaximum,
                CellSize,
                BaseAgentRadius,
                BaseAgentHeight,
                Width,
                Height,
                ClusterSizeInCells,
                GeometryHash,
                ParameterHash,
                DataHash,
                Allocator.Persistent);
        }
    }
}
