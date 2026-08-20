using System;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 计算供所有体型复用的保守欧氏 Clearance 场
    /// </summary>
    public static class NavigationEuclideanDistanceTransform
    {
        private const double InfiniteDistance = double.PositiveInfinity;

        // 外围补一圈阻挡，使越界空间自然进入同一个距离场
        // 两次一维平方距离变换组合为确定性的二维欧氏距离
        // 断崖和不可连接的可行走邻居同样作为零距离边界
        // 中心距离减去阻挡 Cell 半对角线，避免高估任意方向可用空间
        // 所有临时数组仅存在于烘焙调用期间，不进入运行时 Blob

        public static void Calculate(
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

            // 外围补一圈阻挡，将越出 Grid 的空间统一纳入距离场而不在变换阶段增加边界分支
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
                    // 0 表示距离源，+∞ 表示非源点；变换结果是到所有距离源的最小平方距离
                    source[x + z * paddedWidth] = blocked ? 0d : InfiniteDistance;
                }
            }

            // 一维工作区按最长轴复用，避免为每一行和每一列重复分配数组
            int maximumLength = Math.Max(paddedWidth, paddedHeight);
            double[] lineSource = new double[maximumLength];
            double[] lineResult = new double[maximumLength];
            int[] envelopeIndices = new int[maximumLength];
            double[] envelopeLimits = new double[maximumLength + 1];

            // 平方欧氏距离可分离为 Z 和 X 两次一维变换，总复杂度保持 O(width * height)
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
                        // 从中心距离减去阻挡 Cell 半对角线，保证任何方向都不会高估可用空间
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
            // 普通阻挡已经由源点标记覆盖，此处专门捕获台阶和断崖边界
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
            // 使用一维平方距离变换构造抛物线下包络
            // 每个有限源点最多入栈和出栈一次，因此单轴计算保持线性复杂度
            // source 的有限值表示距离源，无穷值表示等待其他源点传播
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

                    // 新抛物线在旧抛物线生效前已经更优时，旧抛物线不可能贡献最小距离
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

            // 查询位置单调递增时，最优抛物线也只会向后切换，因而评估阶段是线性复杂度
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
