using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将格子地图和分层寻路结果打包成可在运行时共享的只读 Blob
    /// </summary>
    public static class NavigationGridBlobBuilder
    {
        /// <summary>
        /// 复制格子、分块、入口和抽象连接，创建指定生命周期的 Blob
        /// </summary>
        /// <param name="cells">已完成邻接、可用空间、连通区域和分块编号的格子</param>
        /// <param name="hierarchy">根据这些格子生成的分层寻路结果</param>
        /// <param name="boundsMinimum">Grid 世界包围盒最小点</param>
        /// <param name="boundsMaximum">Grid 世界包围盒最大点</param>
        /// <param name="cellSize">Cell 在 XZ 平面的世界边长</param>
        /// <param name="baseAgentRadius">烘焙静态占用时包含的基础半径</param>
        /// <param name="baseAgentHeight">烘焙静态占用时包含的基础高度</param>
        /// <param name="width">Grid 在 X 轴的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴的 Cell 数量</param>
        /// <param name="clusterSizeInCells">规则 Cluster 的 Cell 边长</param>
        /// <param name="geometryHash">参与构建的静态场景几何哈希</param>
        /// <param name="parameterHash">影响构建结果的配置哈希</param>
        /// <param name="dataHash">完整输出内容的哈希</param>
        /// <param name="allocator">返回 Blob 的生命周期分配器</param>
        /// <returns>由调用方负责注册或释放的只读导航网格 Blob</returns>
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

            // BlobBuilder 只在构建期间可写，创建完成后的 Blob 保持只读
            var builder = new BlobBuilder(Allocator.Temp);
            ref NavigationGridBlob root = ref builder.ConstructRoot<NavigationGridBlob>();
            // 根节点先写入整张导航网格共用的尺寸、角色基准和版本信息
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

            // 格子按行连续复制到 BlobArray
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
                    // 枚举和 bool 转为 byte，适合紧凑地存入 Blob
                    NeighborMask = (byte)source.NeighborMask,
                    Walkable = source.Walkable ? (byte)1 : (byte)0,
                };
            }

            // 寻路分块沿用分层构建结果中的索引
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
                    // 起点和数量指向后面的全局入口节点索引数组
                    PortalNodeOffset = source.PortalNodeOffset,
                    PortalNodeCount = source.PortalNodeCount,
                };
            }

            // 分块入口沿用构建器的扫描顺序
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
                    // 代表格子和最窄可用空间直接使用烘焙结果
                    RepresentativeCellA = source.RepresentativeCellA,
                    RepresentativeCellB = source.RepresentativeCellB,
                    MinimumClearance = source.MinimumClearance,
                    StaticCostAtoB = source.StaticCostAtoB,
                    StaticCostBtoA = source.StaticCostBtoA,
                };
            }

            // 入口节点保持每个入口两侧连续排列
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
                    // 起点和数量用于读取全局抽象连接数组
                    EdgeOffset = source.EdgeOffset,
                    EdgeCount = source.EdgeCount,
                };
            }

            // 抽象连接按节点的出边顺序复制
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
                    // 是否穿过分块入口使用 byte 保存
                    CrossesPortal = source.CrossesPortal ? (byte)1 : (byte)0,
                };
            }

            // 各分块通过自己的起点和数量读取这份连续节点索引
            BlobBuilderArray<int> clusterNodeIndices = builder.Allocate(
                ref root.ClusterPortalNodeIndices,
                hierarchy.ClusterPortalNodeIndices.Length);
            for (int index = 0; index < hierarchy.ClusterPortalNodeIndices.Length; index++)
            {
                clusterNodeIndices[index] = hierarchy.ClusterPortalNodeIndices[index];
            }

            // 使用调用方指定的 Allocator 创建最终 Blob
            BlobAssetReference<NavigationGridBlob> result =
                builder.CreateBlobAssetReference<NavigationGridBlob>(allocator);
            // Blob 创建后即可释放临时 Builder，不会影响最终数据
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
