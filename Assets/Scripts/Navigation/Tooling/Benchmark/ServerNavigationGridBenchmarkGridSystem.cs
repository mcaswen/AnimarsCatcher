using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 基准场景没有正式烘焙资产时，创建一张固定的开放导航网格作为测试数据
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ServerNavigationGridBenchmarkGridSystem : ISystem
    {
        private const int BaselineWidth = 96;
        private const int BaselineHeight = 64;
        private const int StageSixWidth = 128;
        private const int StageSixHeight = 128;
        private const int ClusterSizeInCells = 8;
        private const float CellSize = 1f;
        private const float GroundHeight = 0.57f;
        private const float BaseAgentRadius = 0.35f;
        private const float BaseAgentHeight = 1.5f;

        private static readonly float3 BaselineBoundsMinimum = new(48f, -1f, 16f);
        private static readonly Unity.Entities.Hash128 GeometryHash =
            new("4b6e63686d61726b4772696447656f31");
        private static readonly Unity.Entities.Hash128 ParameterHash =
            new("4b6e63686d61726b4772696450617231");
        private static readonly Unity.Entities.Hash128 DataHash =
            new("4b6e63686d61726b4772696444617431");
        private static readonly Unity.Entities.Hash128 StageSixGeometryHash =
            new("536978414772696447656f6d65747279");
        private static readonly Unity.Entities.Hash128 StageSixParameterHash =
            new("5369784147726964506172616d657465");
        private static readonly Unity.Entities.Hash128 StageSixDataHash =
            new("53697841477269644461746131323878");

        private EntityQuery _gridQuery;
        private BlobAssetReference<NavigationGridBlob> _ownedGrid;
        private Entity _ownedGridEntity;
        private bool _ownsGrid;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            state.RequireForUpdate<NavigationGridBenchmarkConfig>();
            // 优先查询并复用场景中已有的正式导航网格
            _gridQuery = state.GetEntityQuery(ComponentType.ReadOnly<NavigationGridReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 只要场景已有导航网格，就不创建合成数据
            int existingGridCount = _gridQuery.CalculateEntityCount();
            if (existingGridCount > 0)
            {
                // 正式烘焙网格始终优先，基准系统不会覆盖它或再创建第二张
                if (existingGridCount > 1)
                {
                    // 多张网格会让 Flow Field 缓存中的格子索引无法确定归属
                    Debug.LogError("[NavigationBenchmark] More than one Navigation Grid exists");
                }

                state.Enabled = false;
                return;
            }

            NavigationGridBenchmarkConfig config =
                SystemAPI.GetSingleton<NavigationGridBenchmarkConfig>();
            bool stageSixMovement =
                config.Workload == NavigationGridBenchmarkWorkload.FreeCohortMovement &&
                NavigationGridBenchmarkScaleProfile.IsStageSixAgentCount(config.AgentCount);
            int width = stageSixMovement ? StageSixWidth : BaselineWidth;
            int height = stageSixMovement ? StageSixHeight : BaselineHeight;
            // 万人起点和目标区域使用更大的固定开放网格，历史基线仍保持原地图与 Hash
            float3 boundsMinimum = stageSixMovement
                ? config.SpawnOrigin - new float3(width * CellSize * 0.5f, 1.57f,
                    height * CellSize * 0.5f)
                : BaselineBoundsMinimum;
            _ownedGrid = CreateBenchmarkGrid(
                width,
                height,
                boundsMinimum,
                stageSixMovement ? StageSixGeometryHash : GeometryHash,
                stageSixMovement ? StageSixParameterHash : ParameterHash,
                stageSixMovement ? StageSixDataHash : DataHash);
            // 引用 Entity 和 Blob 都由当前 System 负责销毁
            _ownedGridEntity = state.EntityManager.CreateEntity(typeof(NavigationGridReference));
            state.EntityManager.SetComponentData(
                _ownedGridEntity,
                new NavigationGridReference { Value = _ownedGrid });
            _ownsGrid = true;
            state.Enabled = false;
            Debug.Log(
                $"[NavigationBenchmark] Created deterministic Grid {width}x{height}, " +
                $"hash={_ownedGrid.Value.DataHash}");
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!_ownsGrid)
            {
                // 复用正式 Grid 时不拥有其 Entity 或 Blob 生命周期
                return;
            }

            // World 销毁系统的顺序不固定，释放合成网格前必须等待所有读取它的任务完成
            state.EntityManager.CompleteAllTrackedJobs();

            if (state.EntityManager.Exists(_ownedGridEntity))
            {
                // 先移除引用 Entity 再释放 Blob，避免 World 内留下指向已释放内存的组件
                state.EntityManager.DestroyEntity(_ownedGridEntity);
            }

            if (_ownedGrid.IsCreated)
            {
                _ownedGrid.Dispose();
            }

            _ownsGrid = false;
        }

        private static BlobAssetReference<NavigationGridBlob> CreateBenchmarkGrid(
            int width,
            int height,
            float3 boundsMinimum,
            Unity.Entities.Hash128 geometryHash,
            Unity.Entities.Hash128 parameterHash,
            Unity.Entities.Hash128 dataHash)
        {
            // 合成网格只用于在固定地图条件下比较寻路工作量
            var cells = new NavigationGridCellData[width * height];
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

            // 即使是全开放地面，也通过正式烘焙算法生成邻接、安全距离和分层数据
            NavigationGridBakingAlgorithms.BuildConnectivity(cells, width, height, 0.5f);
            NavigationEuclideanDistanceTransform.Calculate(cells, width, height, CellSize);
            NavigationGridBakingAlgorithms.AssignClusters(cells, width, height, ClusterSizeInCells);
            NavigationGridBakingAlgorithms.AssignRegions(cells, width, height);
            NavigationGridHierarchyBuildResult hierarchy = NavigationGridHierarchyBuilder.Build(
                cells,
                width,
                height,
                ClusterSizeInCells,
                CellSize);
            float3 boundsMaximum = boundsMinimum + new float3(
                width * CellSize,
                4f,
                height * CellSize);
            // 使用固定哈希明确区分合成网格和正式场景资产
            return NavigationGridBlobBuilder.Create(
                cells,
                hierarchy,
                boundsMinimum,
                boundsMaximum,
                CellSize,
                BaseAgentRadius,
                BaseAgentHeight,
                width,
                height,
                ClusterSizeInCells,
                geometryHash,
                parameterHash,
                dataHash,
                Allocator.Persistent);
        }
    }
}
