using System;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供不依赖 Scene 和 World 的确定性 Grid 烘焙算法
    /// </summary>
    public static class NavigationGridBakingAlgorithms
    {
        /// <summary>
        /// 按固定方向顺序生成八邻接并禁止对角穿角
        /// </summary>
        /// <param name="cells">已经完成地面和占用采样的 Cell 数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <param name="maximumStepHeight">允许建立连接的最大高度差</param>
        public static void BuildConnectivity(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float maximumStepHeight)
        {
            // NeighborMask 是静态拓扑的权威表示
            // Region Clearance 和路径搜索只读取该结果而不重复判断场景几何
            ValidateShape(cells, width, height);
            maximumStepHeight = Mathf.Max(0f, maximumStepHeight);

            // 先清空旧邻接结果，使重复烘焙和参数变化后的重算不依赖调用前状态
            for (int i = 0; i < cells.Length; i++)
            {
                NavigationGridCellData cell = cells[i];
                cell.NeighborMask = NavigationNeighborMask.None;
                cells[i] = cell;
            }

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + z * width;
                    if (!cells[index].Walkable)
                    {
                        continue;
                    }

                    NavigationNeighborMask mask = NavigationNeighborMask.None;
                    for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                    {
                        NavigationGridDirections.GetDirection(
                            directionIndex,
                            out int deltaX,
                            out int deltaZ);
                        if (CanConnect(
                                cells,
                                width,
                                height,
                                x,
                                z,
                                deltaX,
                                deltaZ,
                                maximumStepHeight))
                        {
                            mask |= (NavigationNeighborMask)(1 << directionIndex);
                        }
                    }

                    NavigationGridCellData cell = cells[index];
                    cell.NeighborMask = mask;
                    cells[index] = cell;
                }
            }
        }

        /// <summary>
        /// 计算 Cell 中心到最近静态阻挡或 Grid 边界的 Clearance
        /// </summary>
        /// <param name="cells">已经标记可行走状态并生成邻接的 Cell 数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <param name="cellSize">单个 Cell 的世界边长</param>
        public static int AssignRegions(NavigationGridCellData[] cells, int width, int height)
        {
            // Region 只表达静态拓扑连通性，不包含运行时体型或动态障碍
            // 标识稳定性依赖种子顺序和邻居顺序都保持固定
            ValidateShape(cells, width, height);
            int[] queue = new int[cells.Length];
            int regionCount = 0;

            // RegionId 的零值同时作为未访问标记，因此有效区域从一开始编号
            for (int i = 0; i < cells.Length; i++)
            {
                NavigationGridCellData cell = cells[i];
                cell.RegionId = 0;
                cells[i] = cell;
            }

            for (int seedIndex = 0; seedIndex < cells.Length; seedIndex++)
            {
                if (!cells[seedIndex].Walkable || cells[seedIndex].RegionId != 0)
                {
                    continue;
                }

                // 种子按行主序选择，邻居按固定方向展开，保证相同输入得到稳定的区域编号
                regionCount++;
                int queueStart = 0;
                int queueEnd = 0;
                queue[queueEnd++] = seedIndex;
                SetRegion(cells, seedIndex, regionCount);

                while (queueStart < queueEnd)
                {
                    int currentIndex = queue[queueStart++];
                    int currentX = currentIndex % width;
                    int currentZ = currentIndex / width;
                    NavigationNeighborMask mask = cells[currentIndex].NeighborMask;

                    for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                    {
                        NavigationNeighborMask directionMask =
                            (NavigationNeighborMask)(1 << directionIndex);
                        if ((mask & directionMask) == 0)
                        {
                            continue;
                        }

                        NavigationGridDirections.GetDirection(
                            directionIndex,
                            out int deltaX,
                            out int deltaZ);
                        int neighborX = currentX + deltaX;
                        int neighborZ = currentZ + deltaZ;
                        int neighborIndex = neighborX + neighborZ * width;
                        if (cells[neighborIndex].RegionId != 0)
                        {
                            continue;
                        }

                        SetRegion(cells, neighborIndex, regionCount);
                        queue[queueEnd++] = neighborIndex;
                    }
                }
            }

            return regionCount;
        }

        /// <summary>
        /// 按固定分块尺寸为每个 Cell 分配稳定 Cluster 标识
        /// </summary>
        /// <param name="cells">目标 Cell 数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <param name="clusterSizeInCells">每个 Cluster 的 Cell 边长</param>
        public static void AssignClusters(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int clusterSizeInCells)
        {
            // Cluster 是与可行走状态无关的规则空间分块
            // 分层寻路可以在不重新编号的情况下构建门户数据
            ValidateShape(cells, width, height);
            clusterSizeInCells = Math.Max(1, clusterSizeInCells);
            int clusterWidth = (width + clusterSizeInCells - 1) / clusterSizeInCells;

            // Cluster 只表达稳定的空间分块，与可行走状态无关，后续可在其上构建分层寻路数据
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + z * width;
                    NavigationGridCellData cell = cells[index];
                    cell.ClusterId =
                        x / clusterSizeInCells +
                        z / clusterSizeInCells * clusterWidth;
                    cells[index] = cell;
                }
            }
        }

        /// <summary>
        /// 判断指定 Cell 是否满足半径和边距要求
        /// </summary>
        /// <param name="cell">待检查的 Cell</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="bakedAgentRadius">基础可行走图已经包含的 Agent 半径</param>
        /// <param name="margin">额外安全边距</param>
        /// <returns>基础可行走且 Clearance 足够时返回 true</returns>
        public static bool CanAgentOccupy(
            NavigationGridCellData cell,
            float agentRadius,
            float bakedAgentRadius,
            float margin = 0f)
        {
            // 基础采样已经为 bakedAgentRadius 收缩过可行走面，此处只补足额外半径以避免重复扣减
            float requiredClearance =
                Mathf.Max(0f, agentRadius - bakedAgentRadius) +
                Mathf.Max(0f, margin);
            return cell.Walkable && cell.Clearance >= requiredClearance;
        }

        // 邻接构建复用统一方向编码，并集中验证边界、高度和穿角约束
        private static bool CanConnect(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int sourceX,
            int sourceZ,
            int deltaX,
            int deltaZ,
            float maximumStepHeight)
        {
            // 邻接成立必须同时满足边界、可行走、高度和穿角约束
            // 此方法是 NeighborMask 的唯一生成入口，修改条件会同步改变连通域和寻路结果
            int targetX = sourceX + deltaX;
            int targetZ = sourceZ + deltaZ;
            if (!IsInside(targetX, targetZ, width, height))
            {
                return false;
            }

            int sourceIndex = sourceX + sourceZ * width;
            int targetIndex = targetX + targetZ * width;
            if (!CanConnectHeight(cells, sourceIndex, targetIndex, maximumStepHeight))
            {
                return false;
            }

            if (deltaX == 0 || deltaZ == 0)
            {
                return true;
            }

            int sideXIndex = targetX + sourceZ * width;
            int sideZIndex = sourceX + targetZ * width;

            // 对角边同时验证四条正交边，避免障碍角点和高度断层被斜向跨越
            return
                CanConnectHeight(cells, sourceIndex, sideXIndex, maximumStepHeight) &&
                CanConnectHeight(cells, sourceIndex, sideZIndex, maximumStepHeight) &&
                CanConnectHeight(cells, sideXIndex, targetIndex, maximumStepHeight) &&
                CanConnectHeight(cells, sideZIndex, targetIndex, maximumStepHeight);
        }

        private static bool CanConnectHeight(
            NavigationGridCellData[] cells,
            int sourceIndex,
            int targetIndex,
            float maximumStepHeight)
        {
            // 高度差使用绝对值使双向连接保持对称
            // 对称邻接是连通域标记和 A 星反向可达性的共同前提
            return
                cells[sourceIndex].Walkable &&
                cells[targetIndex].Walkable &&
                Mathf.Abs(cells[sourceIndex].Height - cells[targetIndex].Height) <= maximumStepHeight;
        }

        private static bool IsInside(int x, int z, int width, int height)
        {
            // 所有 Cell 索引转换都在行主序计算前经过同一边界判定
            return x >= 0 && x < width && z >= 0 && z < height;
        }

        private static void SetRegion(
            NavigationGridCellData[] cells,
            int index,
            int regionId)
        {
            // RegionId 在加入洪泛队列时立即写入
            // 该约束防止同一 Cell 被多个邻居重复排队
            NavigationGridCellData cell = cells[index];
            cell.RegionId = regionId;
            cells[index] = cell;
        }

        private static void ValidateShape(
            NavigationGridCellData[] cells,
            int width,
            int height)
        {
            // 所有二维算法都依赖 Cells 与 Width Height 完全匹配
            // 在入口集中失败可避免越界错误被误判为采样或寻路问题
            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }

            if (width <= 0 || height <= 0 || cells.Length != width * height)
            {
                throw new ArgumentException("Grid dimensions do not match the cell array");
            }
        }
    }
}
