using System;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 根据已经采样的格子生成邻接、可用空间、连通区域和寻路分块
    /// </summary>
    public static class NavigationGridBakingAlgorithms
    {
        /// <summary>
        /// 为每个格子建立八方向连接，并禁止从障碍物尖角斜穿
        /// </summary>
        /// <param name="cells">已经完成地面与静态障碍采样的格子数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <param name="maximumStepHeight">允许建立连接的最大高度差</param>
        public static void BuildConnectivity(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float maximumStepHeight)
        {
            // NeighborMask 是运行时采用的静态连接结果
            // 连通区域、可用空间和寻路都会读取它，不再重复查询场景几何
            ValidateShape(cells, width, height);
            maximumStepHeight = Mathf.Max(0f, maximumStepHeight);

            // 先清空旧连接，让重复烘焙只由当前采样和参数决定
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
        /// 计算每个格子中心到最近静态障碍、断崖或地图边界的安全距离
        /// </summary>
        /// <param name="cells">已经标记可行走状态并生成邻接的 Cell 数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <param name="cellSize">单个 Cell 的世界边长</param>
        public static int AssignRegions(NavigationGridCellData[] cells, int width, int height)
        {
            // 连通区域只描述烘焙地图是否相通，不考虑运行时角色体型和动态障碍
            // 固定起始格子和邻居检查顺序，使相同地图得到相同区域编号
            ValidateShape(cells, width, height);
            int[] queue = new int[cells.Length];
            int regionCount = 0;

            // 0 同时表示不可行走或尚未访问，因此有效区域从 1 开始编号
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

                // 按行选择起点，并按固定方向扩展邻居，重复烘焙会得到相同区域编号
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
        /// 按固定大小切分地图，并为每个格子写入所属分块编号
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
            // 分块只按地图坐标划分，与格子是否可行走无关
            // 这样通行状态改变后，分层入口仍可在同一套分块编号上重建
            ValidateShape(cells, width, height);
            clusterSizeInCells = Math.Max(1, clusterSizeInCells);
            int clusterWidth = (width + clusterSizeInCells - 1) / clusterSizeInCells;

            // 每个格子根据坐标直接得到分块编号，后续在这些分块之间构建入口和宏观路线
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
        /// 判断一个格子的可用空间是否足以容纳指定体型的角色
        /// </summary>
        /// <param name="cell">待检查的 Cell</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="bakedAgentRadius">基础可行走图已经包含的 Agent 半径</param>
        /// <param name="margin">额外安全边距</param>
        /// <returns>格子可行走且安全距离足够时返回 true</returns>
        public static bool CanAgentOccupy(
            NavigationGridCellData cell,
            float agentRadius,
            float bakedAgentRadius,
            float margin = 0f)
        {
            // 烘焙时已经为基础角色半径预留空间，这里只检查超出的体型和额外边距
            float requiredClearance =
                Mathf.Max(0f, agentRadius - bakedAgentRadius) +
                Mathf.Max(0f, margin);
            return cell.Walkable && cell.Clearance >= requiredClearance;
        }

        // 所有方向都通过同一入口检查地图边界、高度差和斜向穿角
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
            // 只有目标在地图内、两格可行走、高度差允许且不会斜穿墙角时才建立连接
            // NeighborMask 只在这里生成，修改规则会同时改变连通区域和最终寻路
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

            // 斜向连接要求两侧正交方向也都能通过，避免跨过墙角或高度断层
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
            // 高度差取绝对值，让两个方向采用相同判断；连通区域和反向搜索都依赖这种对称连接
            return
                cells[sourceIndex].Walkable &&
                cells[targetIndex].Walkable &&
                Mathf.Abs(cells[sourceIndex].Height - cells[targetIndex].Height) <= maximumStepHeight;
        }

        private static bool IsInside(int x, int z, int width, int height)
        {
            // 先统一检查坐标范围，再换算为按行排列的一维格子索引
            return x >= 0 && x < width && z >= 0 && z < height;
        }

        private static void SetRegion(
            NavigationGridCellData[] cells,
            int index,
            int regionId)
        {
            // 格子加入洪泛队列时立即写入区域编号，避免被多个邻居重复加入
            NavigationGridCellData cell = cells[index];
            cell.RegionId = regionId;
            cells[index] = cell;
        }

        private static void ValidateShape(
            NavigationGridCellData[] cells,
            int width,
            int height)
        {
            // 所有二维算法都要求格子总数与宽高一致；入口处统一检查可以及早暴露数据尺寸错误
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
