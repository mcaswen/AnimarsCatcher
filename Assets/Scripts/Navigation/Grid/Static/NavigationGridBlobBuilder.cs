using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将已经完成静态拓扑和分层构建的 Grid 装配为运行时只读 Blob
    /// </summary>
    public static class NavigationGridBlobBuilder
    {
        /// <summary>
        /// 复制 Cell、Cluster、Portal 和抽象边并创建指定生命周期的 Blob
        /// </summary>
        /// <param name="cells">完成邻接、Clearance、Region 和 Cluster 分配的 Cell</param>
        /// <param name="hierarchy">与 Cell 拓扑对应的分层构建结果</param>
        /// <param name="boundsMinimum">Grid 世界包围盒最小点</param>
        /// <param name="boundsMaximum">Grid 世界包围盒最大点</param>
        /// <param name="cellSize">Cell 在 XZ 平面的世界边长</param>
        /// <param name="baseAgentRadius">烘焙静态占用时包含的基础半径</param>
        /// <param name="baseAgentHeight">烘焙静态占用时包含的基础高度</param>
        /// <param name="width">Grid 在 X 轴的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴的 Cell 数量</param>
        /// <param name="clusterSizeInCells">规则 Cluster 的 Cell 边长</param>
        /// <param name="geometryHash">参与构建的静态几何 Hash</param>
        /// <param name="parameterHash">参与构建的配置参数 Hash</param>
        /// <param name="dataHash">完整输出数据 Hash</param>
        /// <param name="allocator">返回 Blob 的生命周期分配器</param>
        /// <returns>由调用者负责注册或释放的只读 Grid Blob</returns>
        public static BlobAssetReference<NavigationGridBlob> Create(
            NavigationGridCellData[] cells,
            NavigationGridHierarchyBuildResult hierarchy,
            float3 boundsMinimum,
            float3 boundsMaximum,
            float cellSize,
            float baseAgentRadius,
            float baseAgentHeight,
            int width,
            int height,
            int clusterSizeInCells,
            Unity.Entities.Hash128 geometryHash,
            Unity.Entities.Hash128 parameterHash,
            Unity.Entities.Hash128 dataHash,
            Allocator allocator)
        {
            if (cells == null || cells.Length != width * height)
            {
                throw new ArgumentException("Navigation Grid Blob input shape is invalid", nameof(cells));
            }

            if (hierarchy == null)
            {
                throw new ArgumentNullException(nameof(hierarchy));
            }

            // 临时 Builder 只负责组装，最终 Blob 创建后保持只读
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            // 根节点先写入与所有数组共享的 Grid 形状和版本
            root.BoundsMinimum = boundsMinimum;
            root.BoundsMaximum = boundsMaximum;
            root.CellSize = cellSize;
            root.BaseAgentRadius = baseAgentRadius;
            root.BaseAgentHeight = baseAgentHeight;
            root.Width = width;
            root.Height = height;
            root.ClusterSizeInCells = clusterSizeInCells;
            root.ClusterWidth = hierarchy.ClusterWidth;
            root.ClusterHeight = hierarchy.ClusterHeight;
            root.RegionCount = CountRegions(cells);
            root.DataVersion = NavigationGridBakeAsset.CurrentDataVersion;
            root.GeometryHash = geometryHash;
            root.ParameterHash = parameterHash;
            root.DataHash = dataHash;

            // Cell 按行主序复制到连续 BlobArray
            BlobBuilderArray<NavigationGridCell> blobCells =
                builder.Allocate(ref root.Cells, cells.Length);
            for (int index = 0; index < cells.Length; index++)
            {
                NavigationGridCellData source = cells[index];
                blobCells[index] = new NavigationGridCell
                {
                    Height = source.Height,
                    SurfaceNormal = source.SurfaceNormal,
                    SlopeDegrees = source.SlopeDegrees,
                    TerrainCost = source.TerrainCost,
                    Clearance = source.Clearance,
                    RegionId = source.RegionId,
                    ClusterId = source.ClusterId,
                    // 托管枚举和 bool 转为稳定字节表示
                    NeighborMask = (byte)source.NeighborMask,
                    Walkable = source.Walkable ? (byte)1 : (byte)0,
                };
            }

            // Cluster 与 hierarchy 数组保持相同索引
            BlobBuilderArray<NavigationGridCluster> blobClusters =
                builder.Allocate(ref root.Clusters, hierarchy.Clusters.Length);
            for (int index = 0; index < hierarchy.Clusters.Length; index++)
            {
                NavigationGridClusterData source = hierarchy.Clusters[index];
                blobClusters[index] = new NavigationGridCluster
                {
                    MinimumX = source.MinimumX,
                    MinimumZ = source.MinimumZ,
                    MaximumXExclusive = source.MaximumXExclusive,
                    MaximumZExclusive = source.MaximumZExclusive,
                    // Offset 和 Count 指向后面的全局 Portal Node 索引数组
                    PortalNodeOffset = source.PortalNodeOffset,
                    PortalNodeCount = source.PortalNodeCount,
                };
            }

            // Portal 保持分层构建器产生的稳定顺序
            BlobBuilderArray<NavigationGridPortal> blobPortals =
                builder.Allocate(ref root.Portals, hierarchy.Portals.Length);
            for (int index = 0; index < hierarchy.Portals.Length; index++)
            {
                NavigationGridPortalData source = hierarchy.Portals[index];
                blobPortals[index] = new NavigationGridPortal
                {
                    ClusterA = source.ClusterA,
                    ClusterB = source.ClusterB,
                    RegionId = source.RegionId,
                    FirstCellA = source.FirstCellA,
                    LastCellA = source.LastCellA,
                    FirstCellB = source.FirstCellB,
                    LastCellB = source.LastCellB,
                    // 代表 Cell 和 Clearance 直接沿用已量化的烘焙结果
                    RepresentativeCellA = source.RepresentativeCellA,
                    RepresentativeCellB = source.RepresentativeCellB,
                    MinimumClearance = source.MinimumClearance,
                    StaticCostAtoB = source.StaticCostAtoB,
                    StaticCostBtoA = source.StaticCostBtoA,
                };
            }

            // Portal Node 数组保留每个 Portal 两侧节点的连续布局
            BlobBuilderArray<NavigationGridPortalNode> blobNodes =
                builder.Allocate(ref root.PortalNodes, hierarchy.PortalNodes.Length);
            for (int index = 0; index < hierarchy.PortalNodes.Length; index++)
            {
                NavigationGridPortalNodeData source = hierarchy.PortalNodes[index];
                blobNodes[index] = new NavigationGridPortalNode
                {
                    PortalIndex = source.PortalIndex,
                    ClusterId = source.ClusterId,
                    CellIndex = source.CellIndex,
                    // Offset 和 Count 定位全局抽象边数组
                    EdgeOffset = source.EdgeOffset,
                    EdgeCount = source.EdgeCount,
                };
            }

            // 抽象边按 Node 出边切片顺序复制
            BlobBuilderArray<NavigationGridAbstractEdge> blobEdges =
                builder.Allocate(ref root.AbstractEdges, hierarchy.AbstractEdges.Length);
            for (int index = 0; index < hierarchy.AbstractEdges.Length; index++)
            {
                NavigationGridAbstractEdgeData source = hierarchy.AbstractEdges[index];
                blobEdges[index] = new NavigationGridAbstractEdge
                {
                    ToNodeIndex = source.ToNodeIndex,
                    StaticCost = source.StaticCost,
                    MinimumClearance = source.MinimumClearance,
                    // Blob 使用字节保存跨 Portal 标志
                    CrossesPortal = source.CrossesPortal ? (byte)1 : (byte)0,
                };
            }

            // Cluster 的 Offset 和 Count 会读取这份连续 Node 索引
            BlobBuilderArray<int> clusterNodeIndices = builder.Allocate(
                ref root.ClusterPortalNodeIndices,
                hierarchy.ClusterPortalNodeIndices.Length);
            for (int index = 0; index < hierarchy.ClusterPortalNodeIndices.Length; index++)
            {
                clusterNodeIndices[index] = hierarchy.ClusterPortalNodeIndices[index];
            }

            // 使用调用方指定的 allocator 创建最终 Blob 生命周期
            BlobAssetReference<NavigationGridBlob> result =
                builder.CreateBlobAssetReference<NavigationGridBlob>(allocator);
            // 临时 Builder 与已创建的 Blob 引用相互独立
            builder.Dispose();
            return result;
        }

        private static int CountRegions(NavigationGridCellData[] cells)
        {
            int maximumRegionId = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                maximumRegionId = math.max(maximumRegionId, cells[index].RegionId);
            }

            return maximumRegionId;
        }
    }
}
