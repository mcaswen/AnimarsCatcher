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
            // Baker 只消费 Authoring 显式引用的持久化资产
            NavigationGridBakeAsset bakeAsset = authoring.BakeAsset;
            if (bakeAsset == null)
            {
                Debug.LogError("NavigationGridAuthoring requires a bake asset", authoring);
                return;
            }

            // 场景新鲜度由编辑器门禁检查，Runtime 只声明资产依赖
            DependsOn(bakeAsset);
            if (!bakeAsset.IsUsable)
            {
                Debug.LogError("Navigation Grid bake asset is missing or uses an unsupported version", bakeAsset);
                return;
            }

            // Blob 根字段镜像 Bake Asset 的运行时只读契约
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

            // Cell 按行主序复制，运行时索引与编辑器检查保持一致
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

            // Cluster 保持 Bake Asset 中的稳定顺序
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
                    // Baker 直接沿用分层构建器生成的切片边界
                    PortalNodeOffset = source.PortalNodeOffset,
                    PortalNodeCount = source.PortalNodeCount,
                };
            }

            // Portal 区间和双向成本直接来自可检查资产
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
                    // 运行时体型过滤直接读取烘焙后的 MinimumClearance
                    MinimumClearance = source.MinimumClearance,
                    StaticCostAtoB = source.StaticCostAtoB,
                    StaticCostBtoA = source.StaticCostBtoA,
                };
            }

            // Portal Node 保持每个 Portal 两侧节点的连续布局
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
                    // Offset 和 Count 定位下面的抽象边数组
                    EdgeOffset = source.EdgeOffset,
                    EdgeCount = source.EdgeCount,
                };
            }

            // 抽象边按节点切片顺序写入 Blob
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
                    // 该标志决定运行时 Corridor 何时跨入下一个 Cluster
                    CrossesPortal = source.CrossesPortal ? (byte)1 : (byte)0,
                };
            }

            // Cluster Blob 通过 Offset 和 Count 引用这份连续 Node 索引
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

            // 交给 Baker 按完整 Blob 内容去重，同时正确释放重复构建的临时实例
            AddBlobAsset(ref blobReference, out _);

            Entity entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new NavigationGridReference { Value = blobReference });
        }
    }
}
