using System;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 计算每个可行走格子到最近障碍边界的直线距离，供不同体型角色判断能否通过
    /// </summary>
    public static class NavigationEuclideanDistanceTransform
    {
        private const double InfiniteDistance = double.PositiveInfinity;

        // 地图外围视为一圈障碍，让边界格子也能用同一公式计算距离
        // 先沿一个轴、再沿另一个轴做平方距离变换，得到二维欧氏距离
        // 断崖以及看似可站立但不能相互跨越的邻格，也视为障碍边界
        // 从中心距离中扣除半个格子对角线，避免高估斜向可用空间
        // 临时数组只在烘焙时存在，不会写入运行时 Blob

        public static void Calculate(
            NavigationGridCellData[] cells,
            int width,
            int height,
            float cellSize)
        {
            // 使用直线距离而不是横竖步数，结果更接近角色周围真实可用半径
            // 得到的安全距离略偏保守，可供不同半径的角色共同使用
            ValidateShape(cells, width, height);
            if (cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            }

            // 在工作数组外围补一圈障碍，距离变换时无需为地图边界单独分支
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
                    // 障碍边界记为 0，其他位置记为无穷；变换后得到最近障碍的平方距离
                    source[x + z * paddedWidth] = blocked ? 0d : InfiniteDistance;
                }
            }

            // 一维临时数组按较长轴分配，并在所有行列之间复用
            int maximumLength = Math.Max(paddedWidth, paddedHeight);
            double[] lineSource = new double[maximumLength];
            double[] lineResult = new double[maximumLength];
            int[] envelopeIndices = new int[maximumLength];
            double[] envelopeLimits = new double[maximumLength + 1];

            // 平方欧氏距离可以拆成 Z、X 两次一维计算，总开销为 O(width * height)
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
                        // 扣除障碍格子的半对角线，确保任何方向都不会高估可用空间
                        cell.Clearance = Mathf.Max(
                            0f,
                            (float)centerDistance * cellSize - obstacleHalfDiagonal);
                    }

                    cells[index] = cell;
                }
            }
        }

        private static bool IsClearanceSource(
            NavigationGridCellData[] cells,
            int x,
            int z,
            int width,
            int height)
        {
            // 不可行走格子和地图外边界都是距离为零的障碍源
            // 可行走地面之间的断层也作为障碍源，安全距离不会跨过实际无法通过的边缘
            int index = x + z * width;
            NavigationGridCellData cell = cells[index];
            if (!cell.Walkable)
            {
                return true;
            }

            // 两个格子都能站立但不能互通时，将这一侧视为距离场边界
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
            // 普通障碍已经标记，这里只查找可站立却没有邻接连接的台阶和断崖
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

        private static bool IsInside(int x, int z, int width, int height)
        {
            return x >= 0 && x < width && z >= 0 && z < height;
        }

        private static void DistanceTransformOneDimension(
            double[] source,
            int length,
            double[] result,
            int[] envelopeIndices,
            double[] envelopeLimits)
        {
            // 一维平方距离变换通过构造抛物线下包络求最近距离
            // 每个有效距离源最多进出工作栈一次，因此单轴计算为线性时间
            // 有限值代表已有距离源，无穷值代表需要由其他距离源覆盖的位置
            int envelopeCount = -1;

            // 每个距离源 q 对应一条 source[q] + (x - q)^2 抛物线
            // envelopeIndices 记录组成下包络的抛物线，envelopeLimits 记录各自开始成为最优解的位置
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

                    // 新抛物线在旧抛物线的有效范围开始前就更低时，旧抛物线可以直接移除
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

            // 查询位置从小到大移动时，最优抛物线也只会向后切换，因此评估仍为线性时间
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
            // 格子总数必须与宽高一致；入口处先检查，避免尺寸错误演变为越界访问
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
