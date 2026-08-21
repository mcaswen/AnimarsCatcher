using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将可检查的 Grid 烘焙资产转换为运行时只读 Blob
    /// </summary>
    public sealed class NavigationGridBaker : Baker<NavigationGridAuthoring>
    {
        public override void Bake(NavigationGridAuthoring authoring)
        {
            // 只读取 Authoring 明确关联的烘焙资产；没有资产或数据无效时不生成运行时网格
            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            if (bakeAsset == null)
            {
                Debug.LogError("NavigationGridAuthoring requires a bake asset", authoring);
                return;
            }

            // 资产是否过期由编辑器构建检查负责，Baker 这里只声明依赖并转换现有数据
            DependsOn(bakeAsset);
            if (!bakeAsset.IsUsable)
            {
                Debug.LogError("Navigation Grid bake asset is missing or uses an unsupported version", bakeAsset);
                return;
            }

            // Blob 根节点先复制导航网格尺寸、版本和内容哈希
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            Bounds bounds = bakeAsset.WorldBounds;

            root.BoundsMinimum = new float3(bounds.min.x, bounds.min.y, bounds.min.z);
            root.BoundsMaximum = new float3(bounds.max.x, bounds.max.y, bounds.max.z);
            root.CellSize = bakeAsset.CellSize;
            root.BaseAgentRadius = bakeAsset.BaseAgentRadius;
            root.BaseAgentHeight = bakeAsset.BaseAgentHeight;
            root.Width = bakeAsset.Width;
            root.Height = bakeAsset.Height;
            root.ClusterSizeInCells = bakeAsset.ClusterSizeInCells;
            root.ClusterWidth = bakeAsset.ClusterWidth;
            root.ClusterHeight = bakeAsset.ClusterHeight;
            root.RegionCount = bakeAsset.RegionCount;
            root.DataVersion = bakeAsset.DataVersion;
            root.GeometryHash = new Unity.Entities.Hash128(bakeAsset.GeometryHash);
            root.ParameterHash = new Unity.Entities.Hash128(bakeAsset.ParameterHash);
            root.DataHash = new Unity.Entities.Hash128(bakeAsset.DataHash);

            // 格子按资产中的行顺序复制，使运行时索引与编辑器预览一致
            BlobBuilderArray<NavigationGridCell> blobCells =
                builder.Allocate(ref root.Cells, bakeAsset.CellCount);

            for (int i = 0; i < bakeAsset.CellCount; i++)
            {
                NavigationGridCellData source = bakeAsset.GetCell(i);
                blobCells[i] = new NavigationGridCell
                {
                    Height = source.Height,
                    SurfaceNormal = new float3(
                        source.SurfaceNormal.x,
                        source.SurfaceNormal.y,
                        source.SurfaceNormal.z),
                    SlopeDegrees = source.SlopeDegrees,
                    TerrainCost = source.TerrainCost,
                    Clearance = source.Clearance,
                    RegionId = source.RegionId,
                    ClusterId = source.ClusterId,
                    NeighborMask = (byte)source.NeighborMask,
                    Walkable = source.Walkable ? (byte)1 : (byte)0,
                };
            }

            // 寻路分块沿用资产中的编号顺序
            BlobBuilderArray<NavigationGridCluster> blobClusters =
                builder.Allocate(ref root.Clusters, bakeAsset.ClusterCount);
            for (int i = 0; i < bakeAsset.ClusterCount; i++)
            {
                NavigationGridClusterData source = bakeAsset.GetCluster(i);
                blobClusters[i] = new NavigationGridCluster
                {
                    MinimumX = source.MinimumX,
                    MinimumZ = source.MinimumZ,
                    MaximumXExclusive = source.MaximumXExclusive,
                    MaximumZExclusive = source.MaximumZExclusive,
                    // 每个分块直接沿用烘焙结果中入口节点切片的起点和数量
                    PortalNodeOffset = source.PortalNodeOffset,
                    PortalNodeCount = source.PortalNodeCount,
                };
            }

            // 分块入口的格子范围和双向成本直接复制自烘焙资产
            BlobBuilderArray<NavigationGridPortal> blobPortals =
                builder.Allocate(ref root.Portals, bakeAsset.PortalCount);
            for (int i = 0; i < bakeAsset.PortalCount; i++)
            {
                NavigationGridPortalData source = bakeAsset.GetPortal(i);
                blobPortals[i] = new NavigationGridPortal
                {
                    ClusterA = source.ClusterA,
                    ClusterB = source.ClusterB,
                    RegionId = source.RegionId,
                    FirstCellA = source.FirstCellA,
                    LastCellA = source.LastCellA,
                    FirstCellB = source.FirstCellB,
                    LastCellB = source.LastCellB,
                    RepresentativeCellA = source.RepresentativeCellA,
                    RepresentativeCellB = source.RepresentativeCellB,
                    // 运行时根据烘焙出的最窄空间判断角色体型能否通过入口
                    MinimumClearance = source.MinimumClearance,
                    StaticCostAtoB = source.StaticCostAtoB,
                    StaticCostBtoA = source.StaticCostBtoA,
                };
            }

            // 每个入口两侧的节点继续保持连续排列
            BlobBuilderArray<NavigationGridPortalNode> blobPortalNodes =
                builder.Allocate(ref root.PortalNodes, bakeAsset.PortalNodeCount);
            for (int i = 0; i < bakeAsset.PortalNodeCount; i++)
            {
                NavigationGridPortalNodeData source = bakeAsset.GetPortalNode(i);
                blobPortalNodes[i] = new NavigationGridPortalNode
                {
                    PortalIndex = source.PortalIndex,
                    ClusterId = source.ClusterId,
                    CellIndex = source.CellIndex,
                    // 起点和数量指向下面的抽象连接数组
                    EdgeOffset = source.EdgeOffset,
                    EdgeCount = source.EdgeCount,
                };
            }

            // 抽象连接按各节点的切片顺序写入 Blob
            BlobBuilderArray<NavigationGridAbstractEdge> blobAbstractEdges =
                builder.Allocate(ref root.AbstractEdges, bakeAsset.AbstractEdgeCount);
            for (int i = 0; i < bakeAsset.AbstractEdgeCount; i++)
            {
                NavigationGridAbstractEdgeData source = bakeAsset.GetAbstractEdge(i);
                blobAbstractEdges[i] = new NavigationGridAbstractEdge
                {
                    ToNodeIndex = source.ToNodeIndex,
                    StaticCost = source.StaticCost,
                    MinimumClearance = source.MinimumClearance,
                    // CrossesPortal 标记该连接是否真正进入相邻分块
                    CrossesPortal = source.CrossesPortal ? (byte)1 : (byte)0,
                };
            }

            // 每个分块通过起点和数量引用这份连续入口节点索引
            BlobBuilderArray<int> blobClusterPortalNodeIndices = builder.Allocate(
                ref root.ClusterPortalNodeIndices,
                bakeAsset.ClusterPortalNodeIndexCount);
            for (int i = 0; i < bakeAsset.ClusterPortalNodeIndexCount; i++)
            {
                blobClusterPortalNodeIndices[i] = bakeAsset.GetClusterPortalNodeIndex(i);
            }

            BlobAssetReference<NavigationGridBlob> blobReference =
                builder.CreateBlobAssetReference<NavigationGridBlob>(Allocator.Persistent);
            builder.Dispose();

            // 将完成的 Blob 交给 Baker 注册；Unity 会按内容去重并释放重复的临时实例
            AddBlobAsset(ref blobReference, out _);

            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new NavigationGridReference { Value = blobReference });
        }
    }
}
