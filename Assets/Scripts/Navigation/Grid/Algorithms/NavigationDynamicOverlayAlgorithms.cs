using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供动态 Overlay 的无状态差量更新和运行时查询
    /// </summary>
    public static class NavigationDynamicOverlayAlgorithms
    {
        /// <summary>
        /// 验证 Overlay Buffer 与当前 Grid Blob 的拓扑尺寸是否一致
        /// </summary>
        /// <param name="grid">当前只读 Grid Blob</param>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="clusters">逐 Cluster 的 Overlay Buffer</param>
        /// <returns>两个 Buffer 均与 Grid 拓扑一致时返回 true</returns>
        public static bool IsShapeValid(
            ref NavigationGridBlob grid,
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters)
        {
            return grid.Width > 0 &&
                   grid.Height > 0 &&
                   grid.Cells.Length == grid.Width * grid.Height &&
                   cells.Length == grid.Cells.Length &&
                   clusters.Length == grid.Clusters.Length;
        }

        /// <summary>
        /// 把一个障碍来源的差量合并到指定 Cell
        /// </summary>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">需要更新的 Cell 索引</param>
        /// <param name="blockCountDelta">阻挡引用计数差量</param>
        /// <param name="extraCostDelta">额外移动成本差量</param>
        /// <param name="clearanceReductionDelta">Clearance 减少量差量</param>
        /// <param name="version">本批次稳定版本</param>
        /// <returns>Cell 的有效状态发生变化时返回 true</returns>
        public static bool ApplyDelta(
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            int cellIndex,
            int blockCountDelta,
            float extraCostDelta,
            float clearanceReductionDelta,
            uint version)
        {
            if (cellIndex < 0 || cellIndex >= cells.Length ||
                !math.isfinite(extraCostDelta) ||
                !math.isfinite(clearanceReductionDelta))
            {
                return false;
            }

            NavigationDynamicOverlayCell cell = cells[cellIndex];
            int blockCount = math.max(0, cell.BlockCount + blockCountDelta);
            float extraCost = math.max(0f, cell.ExtraCost + extraCostDelta);
            float clearanceReduction = math.max(
                0f,
                cell.ClearanceReduction + clearanceReductionDelta);

            bool changed = cell.BlockCount != blockCount ||
                           math.abs(cell.ExtraCost - extraCost) > 0.00001f ||
                           math.abs(cell.ClearanceReduction - clearanceReduction) >
                           0.00001f;
            if (!changed)
            {
                return false;
            }

            cell.BlockCount = blockCount;
            cell.ExtraCost = extraCost;
            cell.ClearanceReduction = clearanceReduction;
            cell.Version = version;
            cells[cellIndex] = cell;
            return true;
        }

        /// <summary>
        /// 标记变更 Cell 外围一圈涉及的 Cluster
        /// </summary>
        /// <param name="grid">当前只读 Grid Blob</param>
        /// <param name="cellIndex">发生有效变化的 Cell 索引</param>
        /// <param name="clusters">逐 Cluster 的 Overlay Buffer</param>
        /// <param name="version">本批次稳定版本</param>
        /// <returns>本批次首次标记的 Cluster 数量</returns>
        public static int MarkAffectedClusters(
            ref NavigationGridBlob grid,
            int cellIndex,
            DynamicBuffer<NavigationDynamicOverlayCluster> clusters,
            uint version)
        {
            if (cellIndex < 0 || cellIndex >= grid.Cells.Length)
            {
                return 0;
            }

            int centerX = cellIndex % grid.Width;
            int centerZ = cellIndex / grid.Width;
            int changedClusterCount = 0;
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int x = centerX + offsetX;
                    int z = centerZ + offsetZ;
                    if (x < 0 || x >= grid.Width || z < 0 || z >= grid.Height)
                    {
                        continue;
                    }

                    int neighborIndex = x + z * grid.Width;
                    int clusterId = grid.Cells[neighborIndex].ClusterId;
                    if (clusterId < 0 || clusterId >= clusters.Length)
                    {
                        continue;
                    }

                    NavigationDynamicOverlayCluster cluster = clusters[clusterId];
                    if (cluster.Version != version)
                    {
                        changedClusterCount++;
                    }
                    cluster.Version = version;
                    cluster.AffectedCellCount++;
                    clusters[clusterId] = cluster;
                }
            }

            return changedClusterCount;
        }

        /// <summary>
        /// 生成避开零值的下一个 Overlay 版本
        /// </summary>
        /// <param name="current">当前版本</param>
        /// <returns>可用于发布的非零版本</returns>
        public static uint NextVersion(uint current)
        {
            uint next = current + 1u;
            return next == 0u ? 1u : next;
        }

        /// <summary>
        /// 查询指定 Cell 是否存在至少一个阻挡引用
        /// </summary>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">待查询的 Cell 索引</param>
        /// <returns>索引有效且阻挡引用大于零时返回 true</returns>
        public static bool IsBlocked(
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            int cellIndex)
        {
            return cellIndex >= 0 &&
                   cellIndex < cells.Length &&
                   cells[cellIndex].BlockCount > 0;
        }

        /// <summary>
        /// 合并静态 Clearance 和动态减少量
        /// </summary>
        /// <param name="staticCell">Blob 中的静态 Cell</param>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">待查询的 Cell 索引</param>
        /// <returns>限制为非负值的有效 Clearance</returns>
        public static float GetEffectiveClearance(
            ref NavigationGridCell staticCell,
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            int cellIndex)
        {
            float reduction = 0f;
            if (cellIndex >= 0 && cellIndex < cells.Length)
            {
                reduction = math.max(0f, cells[cellIndex].ClearanceReduction);
            }

            return math.max(0f, staticCell.Clearance - reduction);
        }

        /// <summary>
        /// 读取限制为非负值的动态移动成本
        /// </summary>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">待查询的 Cell 索引</param>
        /// <returns>索引无效时返回零</returns>
        public static float GetExtraCost(
            DynamicBuffer<NavigationDynamicOverlayCell> cells,
            int cellIndex)
        {
            return cellIndex >= 0 && cellIndex < cells.Length
                ? math.max(0f, cells[cellIndex].ExtraCost)
                : 0f;
        }
    }
}
