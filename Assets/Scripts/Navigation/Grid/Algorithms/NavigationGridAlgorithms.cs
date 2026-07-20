using System;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 提供不依赖 Scene 和 World 的确定性 Grid 烘焙算法
    /// </summary>
    public static class NavigationGridAlgorithms
    {
        private const double InfiniteDistance = double.PositiveInfinity;

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

            // 先清空旧邻接结果 使重复烘焙和参数变化后的重算不依赖调用前状态
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
                        GetDirection(directionIndex, out int deltaX, out int deltaZ);
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
        public static void CalculateClearance(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float cellSize)
        {
            // Clearance 使用欧氏距离场而不是曼哈顿层数
            // 结果表达 Cell 中心附近可用的保守半径并供多体型复用
            ValidateShape(cells, width, height);
            if (cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            }

            // 外围补一圈阻挡 将越出 Grid 的空间统一纳入距离场而不在变换阶段增加边界分支
            int paddedWidth = width + 2;
            int paddedHeight = height + 2;
            double[] source = new double[paddedWidth * paddedHeight];
            double[] firstPass = new double[source.Length];
            double[] distanceSquared = new double[source.Length];

            for (int z = 0; z < paddedHeight; z++)
            {
                for (int x = 0; x < paddedWidth; x++)
                {
                    bool boundary = x == 0 || z == 0 || x == paddedWidth - 1 || z == paddedHeight - 1;
                    bool blocked = boundary || IsClearanceSource(
                        cells,
                        x - 1,
                        z - 1,
                        width,
                        height);
                    // 0 表示距离源 +∞ 表示非源点 变换结果是到所有距离源的最小平方距离
                    source[x + z * paddedWidth] = blocked ? 0d : InfiniteDistance;
                }
            }

            // 一维工作区按最长轴复用 避免为每一行和每一列重复分配数组
            int maximumLength = Math.Max(paddedWidth, paddedHeight);
            double[] lineSource = new double[maximumLength];
            double[] lineResult = new double[maximumLength];
            int[] envelopeIndices = new int[maximumLength];
            double[] envelopeLimits = new double[maximumLength + 1];

            // 平方欧氏距离可分离为 Z 和 X 两次一维变换 总复杂度保持 O(width * height)
            for (int x = 0; x < paddedWidth; x++)
            {
                for (int z = 0; z < paddedHeight; z++)
                {
                    lineSource[z] = source[x + z * paddedWidth];
                }

                DistanceTransformOneDimension(
                    lineSource,
                    paddedHeight,
                    lineResult,
                    envelopeIndices,
                    envelopeLimits);

                for (int z = 0; z < paddedHeight; z++)
                {
                    firstPass[x + z * paddedWidth] = lineResult[z];
                }
            }

            for (int z = 0; z < paddedHeight; z++)
            {
                for (int x = 0; x < paddedWidth; x++)
                {
                    lineSource[x] = firstPass[x + z * paddedWidth];
                }

                DistanceTransformOneDimension(
                    lineSource,
                    paddedWidth,
                    lineResult,
                    envelopeIndices,
                    envelopeLimits);

                for (int x = 0; x < paddedWidth; x++)
                {
                    distanceSquared[x + z * paddedWidth] = lineResult[x];
                }
            }

            float obstacleHalfDiagonal = cellSize * 0.70710678f;
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = x + z * width;
                    NavigationGridCellData cell = cells[index];
                    if (!cell.Walkable)
                    {
                        cell.Clearance = 0f;
                    }
                    else
                    {
                        double centerDistance = Math.Sqrt(
                            distanceSquared[(x + 1) + (z + 1) * paddedWidth]);
                        // 从中心距离减去阻挡 Cell 半对角线 保证任何方向都不会高估可用空间
                        cell.Clearance = Mathf.Max(
                            0f,
                            (float)centerDistance * cellSize - obstacleHalfDiagonal);
                    }

                    cells[index] = cell;
                }
            }
        }

        /// <summary>
        /// 按行主序和固定邻接顺序分配静态连通区域标识
        /// </summary>
        /// <param name="cells">已经生成邻接的 Cell 数组</param>
        /// <param name="width">Grid 在 X 轴上的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴上的 Cell 数量</param>
        /// <returns>静态连通区域数量</returns>
        public static int AssignRegions(NavigationGridCellData[] cells, int width, int height)
        {
            // Region 只表达静态拓扑连通性 不包含运行时体型或动态障碍
            // 标识稳定性依赖种子顺序和邻居顺序都保持固定
            ValidateShape(cells, width, height);
            int[] queue = new int[cells.Length];
            int regionCount = 0;

            // RegionId 的零值同时作为未访问标记 因此有效区域从一开始编号
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

                // 种子按行主序选择 邻居按固定方向展开 保证相同输入得到稳定的区域编号
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

                        GetDirection(directionIndex, out int deltaX, out int deltaZ);
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

            // Cluster 只表达稳定的空间分块 与可行走状态无关 后续可在其上构建分层寻路数据
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
            // 基础采样已经为 bakedAgentRadius 收缩过可行走面 此处只补足额外半径以避免重复扣减
            float requiredClearance =
                Mathf.Max(0f, agentRadius - bakedAgentRadius) +
                Mathf.Max(0f, margin);
            return cell.Walkable && cell.Clearance >= requiredClearance;
        }

        /// <summary>
        /// 将固定方向索引转换为 Grid 坐标偏移
        /// </summary>
        /// <param name="directionIndex">零到七的固定方向索引</param>
        /// <param name="deltaX">输出 X 轴偏移</param>
        /// <param name="deltaZ">输出 Z 轴偏移</param>
        public static void GetDirection(int directionIndex, out int deltaX, out int deltaZ)
        {
            // 方向编号从北开始顺时针排列并与 NeighborMask 位位置一致
            // 修改映射会破坏已烘焙资产和运行时搜索之间的协议
            switch (directionIndex)
            {
                case 0:
                    deltaX = 0;
                    deltaZ = 1;
                    return;
                case 1:
                    deltaX = 1;
                    deltaZ = 1;
                    return;
                case 2:
                    deltaX = 1;
                    deltaZ = 0;
                    return;
                case 3:
                    deltaX = 1;
                    deltaZ = -1;
                    return;
                case 4:
                    deltaX = 0;
                    deltaZ = -1;
                    return;
                case 5:
                    deltaX = -1;
                    deltaZ = -1;
                    return;
                case 6:
                    deltaX = -1;
                    deltaZ = 0;
                    return;
                case 7:
                    deltaX = -1;
                    deltaZ = 1;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(directionIndex));
            }
        }

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
            // 邻接成立必须同时满足边界 可行走 高度和穿角约束
            // 此方法是 NeighborMask 的唯一生成入口 修改条件会同步改变连通域和寻路结果
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

            // 对角边同时验证四条正交边 避免障碍角点和高度断层被斜向跨越
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

        private static bool IsClearanceSource(
            NavigationGridCellData[] cells,
            int x,
            int z,
            int width,
            int height)
        {
            // 不可行走 Cell 和外围补边界都是距离场的零距离源
            // 可行走断层也作为源点避免 Clearance 跨越实际上不可通过的边缘
            int index = x + z * width;
            NavigationGridCellData cell = cells[index];
            if (!cell.Walkable)
            {
                return true;
            }

            // 两侧都可站立但不能跨越时把断层 Cell 当作距离场边界
            return
                IsDisconnectedWalkableNeighbor(
                    cells,
                    x,
                    z,
                    0,
                    1,
                    width,
                    height,
                    NavigationNeighborMask.North) ||
                IsDisconnectedWalkableNeighbor(
                    cells,
                    x,
                    z,
                    1,
                    0,
                    width,
                    height,
                    NavigationNeighborMask.East) ||
                IsDisconnectedWalkableNeighbor(
                    cells,
                    x,
                    z,
                    0,
                    -1,
                    width,
                    height,
                    NavigationNeighborMask.South) ||
                IsDisconnectedWalkableNeighbor(
                    cells,
                    x,
                    z,
                    -1,
                    0,
                    width,
                    height,
                    NavigationNeighborMask.West);
        }

        private static bool IsDisconnectedWalkableNeighbor(
            NavigationGridCellData[] cells,
            int x,
            int z,
            int deltaX,
            int deltaZ,
            int width,
            int height,
            NavigationNeighborMask directionMask)
        {
            // 只检查仍可站立但没有对应连接的相邻 Cell
            // 普通阻挡已经由源点标记覆盖 此处专门捕获台阶和断崖边界
            int neighborX = x + deltaX;
            int neighborZ = z + deltaZ;
            if (!IsInside(neighborX, neighborZ, width, height))
            {
                return false;
            }

            int index = x + z * width;
            int neighborIndex = neighborX + neighborZ * width;
            return
                cells[neighborIndex].Walkable &&
                (cells[index].NeighborMask & directionMask) == 0;
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

        private static void DistanceTransformOneDimension(
            double[] source,
            int length,
            double[] result,
            int[] envelopeIndices,
            double[] envelopeLimits)
        {
            // 使用一维平方距离变换构造抛物线下包络
            // 每个有限源点最多入栈和出栈一次 因此单轴计算保持线性复杂度
            // source 的有限值表示距离源 无穷值表示等待其他源点传播
            int envelopeCount = -1;

            // 每个有限源点 q 都定义一条 source[q] + (x - q)^2 抛物线
            // envelopeIndices 和 envelopeLimits 保存这些抛物线的下包络及其开始生效的位置
            for (int position = 0; position < length; position++)
            {
                if (double.IsPositiveInfinity(source[position]))
                {
                    continue;
                }

                double intersection = double.NegativeInfinity;
                while (envelopeCount >= 0)
                {
                    int previousPosition = envelopeIndices[envelopeCount];
                    intersection =
                        (source[position] + position * position -
                         source[previousPosition] - previousPosition * previousPosition) /
                        (2d * (position - previousPosition));

                    if (intersection > envelopeLimits[envelopeCount])
                    {
                        break;
                    }

                    // 新抛物线在旧抛物线生效前已经更优时 旧抛物线不可能贡献最小距离
                    envelopeCount--;
                }

                envelopeCount++;
                envelopeIndices[envelopeCount] = position;
                envelopeLimits[envelopeCount] =
                    envelopeCount == 0 ? double.NegativeInfinity : intersection;
                envelopeLimits[envelopeCount + 1] = double.PositiveInfinity;
            }

            if (envelopeCount < 0)
            {
                for (int position = 0; position < length; position++)
                {
                    result[position] = InfiniteDistance;
                }

                return;
            }

            // 查询位置单调递增时 最优抛物线也只会向后切换 因而评估阶段是线性复杂度
            int activeEnvelope = 0;
            for (int position = 0; position < length; position++)
            {
                while (envelopeLimits[activeEnvelope + 1] < position)
                {
                    activeEnvelope++;
                }

                int sourcePosition = envelopeIndices[activeEnvelope];
                double delta = position - sourcePosition;
                result[position] = delta * delta + source[sourcePosition];
            }
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
