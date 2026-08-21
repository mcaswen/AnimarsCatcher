using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 更新并查询动态障碍在导航网格上产生的阻挡、成本和空间影响
    /// </summary>
    public static class NavigationDynamicOverlayAlgorithms
    {
        /// <summary>
        /// 检查动态障碍缓冲区的大小是否与当前导航网格一致
        /// </summary>
        /// <param name="grid">当前只读 Grid Blob</param>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="clusters">逐 Cluster 的 Overlay Buffer</param>
        /// <returns>格子和分块缓冲区都与导航网格大小一致时返回 true</returns>
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
        /// 将一个动态障碍的新增或移除影响合并到指定格子
        /// </summary>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">需要更新的 Cell 索引</param>
        /// <param name="blockCountDelta">阻挡数量的变化，添加为正、移除为负</param>
        /// <param name="extraCostDelta">移动成本的变化</param>
        /// <param name="clearanceReductionDelta">可用空间缩减值的变化</param>
        /// <param name="version">当前更新批次的版本</param>
        /// <returns>该格子的实际导航状态发生变化时返回 true</returns>
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
        /// 标记变化格子及其周围一圈所涉及的寻路分块
        /// </summary>
        /// <param name="grid">当前只读 Grid Blob</param>
        /// <param name="cellIndex">发生有效变化的 Cell 索引</param>
        /// <param name="clusters">逐 Cluster 的 Overlay Buffer</param>
        /// <param name="version">本批次稳定版本</param>
        /// <returns>本批次新标记的分块数量</returns>
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
        /// 生成下一个非零的动态障碍版本号
        /// </summary>
        /// <param name="current">当前版本</param>
        /// <returns>可用于本次更新的版本号</returns>
        public static uint NextVersion(uint current)
        {
            uint next = current + 1u;
            return next == 0u ? 1u : next;
        }

        /// <summary>
        /// 检查指定格子是否被一个或多个动态障碍挡住
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
        /// 计算扣除动态障碍影响后，格子实际剩余的可用空间
        /// </summary>
        /// <param name="staticCell">Blob 中的静态 Cell</param>
        /// <param name="cells">逐 Cell 的 Overlay Buffer</param>
        /// <param name="cellIndex">待查询的 Cell 索引</param>
        /// <returns>不会小于零的实际可用空间</returns>
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
        /// 读取格子的动态附加移动成本，并保证结果不小于零
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
