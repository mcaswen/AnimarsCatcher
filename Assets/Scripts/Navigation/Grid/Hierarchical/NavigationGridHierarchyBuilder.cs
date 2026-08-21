using System;
using System.Collections.Generic;
using AnimarsCatcher.Core;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 从静态格子地图生成的分层寻路数据
    /// </summary>
    public sealed class NavigationGridHierarchyBuildResult
    {
        // X 方向的寻路分块数
        public int ClusterWidth;

        // Z 方向的寻路分块数
        public int ClusterHeight;

        // 按编号排列的全部寻路分块
        public NavigationGridClusterData[] Clusters = Array.Empty<NavigationGridClusterData>();

        // 按边界扫描顺序排列的分块入口
        public NavigationGridPortalData[] Portals = Array.Empty<NavigationGridPortalData>();

        // 每个入口两侧对应的抽象节点
        public NavigationGridPortalNodeData[] PortalNodes = Array.Empty<NavigationGridPortalNodeData>();

        // 各入口节点引用的有向连接
        public NavigationGridAbstractEdgeData[] AbstractEdges = Array.Empty<NavigationGridAbstractEdgeData>();

        // 每个分块连接到的入口节点索引
        public int[] ClusterPortalNodeIndices = Array.Empty<int>();
    }

    /// <summary>
    /// 将格子地图切成分块，找出相邻分块的入口，并预计算分层寻路图
    /// </summary>
    public static class NavigationGridHierarchyBuilder
    {
        private const float MinimumTerrainCost = 0.01f;
        private const float SquareRootTwo = 1.41421356237f;
        private const float CostEpsilon = 0.00001f;
        private const float ClearanceQuantizationScale = 10_000f;

        /// <summary>
        /// 构建分块入口、入口两侧节点，以及分块内部的预计算连接
        /// </summary>
        /// <param name="cells">已完成邻接、可用空间、连通区域和分块编号的格子</param>
        /// <param name="width">Grid 在 X 轴的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴的 Cell 数量</param>
        /// <param name="clusterSizeInCells">规则 Cluster 的 Cell 边长</param>
        /// <param name="cellSize">Cell 在 XZ 平面的世界边长</param>
        /// <returns>可写入烘焙资产和运行时 Blob 的分层数据</returns>
        public static NavigationGridHierarchyBuildResult Build(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int clusterSizeInCells,
            float cellSize)
        {
            // 格子总数必须与导航网格宽高一致
            if (cells == null || width <= 0 || height <= 0 || cells.Length != width * height)
            {
                throw new ArgumentException("Navigation Grid hierarchy input shape is invalid");
            }

            // 分块边长和格子尺寸都必须大于零
            if (clusterSizeInCells <= 0 || cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(clusterSizeInCells));
            }

            // 分块按行编号，地图边缘的分块可以小于配置尺寸
            int clusterWidth = (width + clusterSizeInCells - 1) / clusterSizeInCells;
            int clusterHeight = (height + clusterSizeInCells - 1) / clusterSizeInCells;
            NavigationGridClusterData[] clusters = CreateClusters(
                width,
                height,
                clusterSizeInCells,
                clusterWidth,
                clusterHeight);

            var portals = new List<NavigationGridPortalData>();
            // 先扫描竖直边界，再扫描水平边界，让重复烘焙得到相同入口顺序和资产哈希
            for (int boundaryX = clusterSizeInCells;
                 boundaryX < width;
                 boundaryX += clusterSizeInCells)
            {
                // boundaryX 两侧的格子列分别属于左右相邻分块
                ScanVerticalBoundary(cells, width, height, boundaryX, cellSize, portals);
            }

            // 水平边界统一排在竖直边界之后
            for (int boundaryZ = clusterSizeInCells;
                 boundaryZ < height;
                 boundaryZ += clusterSizeInCells)
            {
                // boundaryZ 两侧的格子行分别属于前后相邻分块
                ScanHorizontalBoundary(cells, width, height, boundaryZ, cellSize, portals);
            }

            // 每个分块入口生成两个连续节点，分别属于边界两侧的分块
            var nodes = new NavigationGridPortalNodeData[portals.Count * 2];
            var nodesByCluster = new List<int>[clusters.Length];
            for (int clusterIndex = 0; clusterIndex < clusters.Length; clusterIndex++)
            {
                nodesByCluster[clusterIndex] = new List<int>();
            }

            // 建立分块到入口节点的索引，运行时可以快速找到分块出口
            for (int portalIndex = 0; portalIndex < portals.Count; portalIndex++)
            {
                NavigationGridPortalData portal = portals[portalIndex];
                int nodeA = portalIndex * 2;
                int nodeB = nodeA + 1;
                nodes[nodeA] = new NavigationGridPortalNodeData
                {
                    PortalIndex = portalIndex,
                    ClusterId = portal.ClusterA,
                    CellIndex = portal.RepresentativeCellA,
                };
                nodes[nodeB] = new NavigationGridPortalNodeData
                {
                    PortalIndex = portalIndex,
                    ClusterId = portal.ClusterB,
                    CellIndex = portal.RepresentativeCellB,
                };
                nodesByCluster[portal.ClusterA].Add(nodeA);
                nodesByCluster[portal.ClusterB].Add(nodeB);
            }

            // 先为每个入口节点建立独立出边列表，最后再合并为连续数组
            var outgoingEdges = new List<NavigationGridAbstractEdgeData>[nodes.Length];
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                outgoingEdges[nodeIndex] = new List<NavigationGridAbstractEdgeData>();
            }

            // 先添加穿过入口的连接，再计算同一分块内入口之间的连接
            AddPortalCrossingEdges(portals, outgoingEdges);
            AddClusterConnectionEdges(
                cells,
                width,
                cellSize,
                nodes,
                nodesByCluster,
                outgoingEdges);

            // 将分块节点索引和节点出边整理成 Blob 可直接读取的连续片段
            int[] clusterPortalNodeIndices = FlattenClusterNodes(clusters, nodesByCluster);
            NavigationGridAbstractEdgeData[] abstractEdges = FlattenEdges(nodes, outgoingEdges);
            return new NavigationGridHierarchyBuildResult
            {
                ClusterWidth = clusterWidth,
                ClusterHeight = clusterHeight,
                Clusters = clusters,
                Portals = portals.ToArray(),
                PortalNodes = nodes,
                AbstractEdges = abstractEdges,
                ClusterPortalNodeIndices = clusterPortalNodeIndices,
            };
        }

        private static NavigationGridClusterData[] CreateClusters(
            int width,
            int height,
            int clusterSize,
            int clusterWidth,
            int clusterHeight)
        {
            var clusters = new NavigationGridClusterData[clusterWidth * clusterHeight];
            // 先遍历 Z 再遍历 X，使分块编号按行排列
            for (int clusterZ = 0; clusterZ < clusterHeight; clusterZ++)
            {
                for (int clusterX = 0; clusterX < clusterWidth; clusterX++)
                {
                    int clusterIndex = clusterX + clusterZ * clusterWidth;
                    // 分块最大边界不包含端点，并裁剪到实际地图尺寸
                    clusters[clusterIndex] = new NavigationGridClusterData
                    {
                        MinimumX = clusterX * clusterSize,
                        MinimumZ = clusterZ * clusterSize,
                        MaximumXExclusive = Math.Min(width, (clusterX + 1) * clusterSize),
                        MaximumZExclusive = Math.Min(height, (clusterZ + 1) * clusterSize),
                    };
                }
            }

            return clusters;
        }

        private static void ScanVerticalBoundary(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int boundaryX,
            float cellSize,
            List<NavigationGridPortalData> portals)
        {
            // runStart 为负表示当前还没有开始收集一段连续入口
            int runStart = -1;
            int runEnd = -1;
            int runClusterA = -1;
            int runClusterB = -1;
            int runRegion = -1;
            int runClearanceBucket = -1;
            float runMinimumClearance = float.PositiveInfinity;

            // 扫描末尾增加一个哨兵位置，用同一套逻辑结束最后一段入口
            for (int z = 0; z <= height; z++)
            {
                int clusterA = -1;
                int clusterB = -1;
                int region = -1;
                int clearanceBucket = -1;
                float clearance = 0f;
                // 哨兵位置不读取格子，只负责触发当前入口段的提交
                bool valid = z < height && TryGetBoundaryPair(
                    cells,
                    width,
                    boundaryX - 1 + z * width,
                    boundaryX + z * width,
                    NavigationNeighborMask.East,
                    NavigationNeighborMask.West,
                    cellSize,
                    out clusterA,
                    out clusterB,
                    out region,
                    out clearanceBucket,
                    out clearance);
                // 相邻分块、连通区域或可用宽度发生变化时，当前连续入口在此结束
                bool continues = valid &&
                                 runStart >= 0 &&
                                 clusterA == runClusterA &&
                                 clusterB == runClusterB &&
                                 region == runRegion &&
                                 clearanceBucket == runClearanceBucket;
                if (continues)
                {
                    runEnd = z;
                    // 入口宽度取整段最窄值，保证任何位置都能满足标记的体型要求
                    runMinimumClearance = Math.Min(runMinimumClearance, clearance);
                    continue;
                }

                if (runStart >= 0)
                {
                    // 先结束旧入口段，再从当前位置开始新入口，避免两段重叠
                    AddVerticalPortal(
                        cells,
                        width,
                        boundaryX,
                        runStart,
                        runEnd,
                        runClusterA,
                        runClusterB,
                        runRegion,
                        runMinimumClearance,
                        cellSize,
                        portals);
                }

                // 当前相邻格子对成为下一段连续入口的起点
                if (valid)
                {
                    runStart = z;
                    runEnd = z;
                    runClusterA = clusterA;
                    runClusterB = clusterB;
                    runRegion = region;
                    runClearanceBucket = clearanceBucket;
                    runMinimumClearance = clearance;
                }
                else
                {
                    runStart = -1;
                }
            }
        }

        private static void ScanHorizontalBoundary(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int boundaryZ,
            float cellSize,
            List<NavigationGridPortalData> portals)
        {
            // 水平边界使用同一套连续入口收集逻辑，只切换扫描轴和邻接方向
            int runStart = -1;
            int runEnd = -1;
            int runClusterA = -1;
            int runClusterB = -1;
            int runRegion = -1;
            int runClearanceBucket = -1;
            float runMinimumClearance = float.PositiveInfinity;

            // 末尾哨兵确保最后一段入口也会被提交
            for (int x = 0; x <= width; x++)
            {
                int clusterA = -1;
                int clusterB = -1;
                int region = -1;
                int clearanceBucket = -1;
                float clearance = 0f;
                // 到达哨兵时不读取格子，只结束最后一段入口
                bool valid = x < width && TryGetBoundaryPair(
                    cells,
                    width,
                    x + (boundaryZ - 1) * width,
                    x + boundaryZ * width,
                    NavigationNeighborMask.North,
                    NavigationNeighborMask.South,
                    cellSize,
                    out clusterA,
                    out clusterB,
                    out region,
                    out clearanceBucket,
                    out clearance);
                // 只有相邻分块、连通区域和可用宽度都相同的格子对才能合并为一段入口
                bool continues = valid &&
                                 runStart >= 0 &&
                                 clusterA == runClusterA &&
                                 clusterB == runClusterB &&
                                 region == runRegion &&
                                 clearanceBucket == runClearanceBucket;
                if (continues)
                {
                    runEnd = x;
                    // 入口段越长，其最窄可用空间只会保持或变小
                    runMinimumClearance = Math.Min(runMinimumClearance, clearance);
                    continue;
                }

                if (runStart >= 0)
                {
                    // 按扫描顺序直接添加，避免无序集合改变烘焙结果
                    AddHorizontalPortal(
                        cells,
                        width,
                        boundaryZ,
                        runStart,
                        runEnd,
                        runClusterA,
                        runClusterB,
                        runRegion,
                        runMinimumClearance,
                        cellSize,
                        portals);
                }

                // 记录新入口段对应的分块、连通区域和初始宽度
                if (valid)
                {
                    runStart = x;
                    runEnd = x;
                    runClusterA = clusterA;
                    runClusterB = clusterB;
                    runRegion = region;
                    runClearanceBucket = clearanceBucket;
                    runMinimumClearance = clearance;
                }
                else
                {
                    runStart = -1;
                }
            }
        }

        private static bool TryGetBoundaryPair(
            NavigationGridCellData[] cells,
            int width,
            int cellAIndex,
            int cellBIndex,
            NavigationNeighborMask directionA,
            NavigationNeighborMask directionB,
            float cellSize,
            out int clusterA,
            out int clusterB,
            out int region,
            out int clearanceBucket,
            out float clearance)
        {
            NavigationGridCellData cellA = cells[cellAIndex];
            NavigationGridCellData cellB = cells[cellBIndex];
            // 只有同一对相邻分块且属于同一连通区域，格子对才可能组成同一入口
            clusterA = cellA.ClusterId;
            clusterB = cellB.ClusterId;
            region = cellA.RegionId;
            // 边界处的实际宽度由两侧格子中更窄的一侧决定
            clearance = Math.Min(cellA.Clearance, cellB.Clearance);
            // 可用空间按烘焙精度量化，宽度等级变化时需要拆成不同入口段
            clearanceBucket = Mathf.RoundToInt(clearance * ClearanceQuantizationScale);
            // 两侧格子必须互相可达并属于同一有效连通区域，单向连接不能作为分块入口
            return cellA.Walkable &&
                   cellB.Walkable &&
                   clusterA >= 0 &&
                   clusterB >= 0 &&
                   clusterA != clusterB &&
                   region > 0 &&
                   region == cellB.RegionId &&
                   (cellA.NeighborMask & directionA) != 0 &&
                   (cellB.NeighborMask & directionB) != 0;
        }

        private static void AddVerticalPortal(
            NavigationGridCellData[] cells,
            int width,
            int boundaryX,
            int startZ,
            int endZ,
            int clusterA,
            int clusterB,
            int region,
            float minimumClearance,
            float cellSize,
            List<NavigationGridPortalData> portals)
        {
            // 入口长度为偶数时固定选择中间两个位置中 Z 较小的一个，保证结果一致
            int representativeZ = (startZ + endZ) / 2;
            int representativeA = boundaryX - 1 + representativeZ * width;
            int representativeB = boundaryX + representativeZ * width;
            // 入口同时记录两侧完整格子范围，并各选一个代表格子供抽象寻路使用
            portals.Add(CreatePortal(
                cells,
                representativeA,
                representativeB,
                boundaryX - 1 + startZ * width,
                boundaryX - 1 + endZ * width,
                boundaryX + startZ * width,
                boundaryX + endZ * width,
                clusterA,
                clusterB,
                region,
                minimumClearance,
                cellSize));
        }

        private static void AddHorizontalPortal(
            NavigationGridCellData[] cells,
            int width,
            int boundaryZ,
            int startX,
            int endX,
            int clusterA,
            int clusterB,
            int region,
            float minimumClearance,
            float cellSize,
            List<NavigationGridPortalData> portals)
        {
            // 入口长度为偶数时固定选择中间两个位置中 X 较小的一个
            int representativeX = (startX + endX) / 2;
            int representativeA = representativeX + (boundaryZ - 1) * width;
            int representativeB = representativeX + boundaryZ * width;
            // 水平边界入口使用相同数据结构，只是格子索引的步长不同
            portals.Add(CreatePortal(
                cells,
                representativeA,
                representativeB,
                startX + (boundaryZ - 1) * width,
                endX + (boundaryZ - 1) * width,
                startX + boundaryZ * width,
                endX + boundaryZ * width,
                clusterA,
                clusterB,
                region,
                minimumClearance,
                cellSize));
        }

        private static NavigationGridPortalData CreatePortal(
            NavigationGridCellData[] cells,
            int representativeA,
            int representativeB,
            int firstA,
            int lastA,
            int firstB,
            int lastB,
            int clusterA,
            int clusterB,
            int region,
            float minimumClearance,
            float cellSize)
        {
            return new NavigationGridPortalData
            {
                ClusterA = clusterA,
                ClusterB = clusterB,
                RegionId = region,
                FirstCellA = firstA,
                LastCellA = lastA,
                FirstCellB = firstB,
                LastCellB = lastB,
                RepresentativeCellA = representativeA,
                RepresentativeCellB = representativeB,
                // 整个入口使用该区间最窄处的可用空间
                MinimumClearance = minimumClearance,
                // 穿过入口的两个方向分别按所进入格子的地形成本计算
                StaticCostAtoB = cellSize * Math.Max(
                    MinimumTerrainCost,
                    cells[representativeB].TerrainCost),
                StaticCostBtoA = cellSize * Math.Max(
                    MinimumTerrainCost,
                    cells[representativeA].TerrainCost),
            };
        }

        private static void AddPortalCrossingEdges(
            List<NavigationGridPortalData> portals,
            List<NavigationGridAbstractEdgeData>[] outgoingEdges)
        {
            for (int portalIndex = 0; portalIndex < portals.Count; portalIndex++)
            {
                NavigationGridPortalData portal = portals[portalIndex];
                int nodeA = portalIndex * 2;
                int nodeB = nodeA + 1;
                // 从 A 到 B 使用进入 B 侧代表格子的静态成本
                outgoingEdges[nodeA].Add(new NavigationGridAbstractEdgeData
                {
                    ToNodeIndex = nodeB,
                    StaticCost = portal.StaticCostAtoB,
                    MinimumClearance = portal.MinimumClearance,
                    // 只有穿过入口的边才表示宏观路线进入了新分块
                    CrossesPortal = true,
                });
                // 反方向有自己的成本，但两方向共用入口最窄空间
                outgoingEdges[nodeB].Add(new NavigationGridAbstractEdgeData
                {
                    ToNodeIndex = nodeA,
                    StaticCost = portal.StaticCostBtoA,
                    MinimumClearance = portal.MinimumClearance,
                    CrossesPortal = true,
                });
            }
        }

        private static void AddClusterConnectionEdges(
            NavigationGridCellData[] cells,
            int width,
            float cellSize,
            NavigationGridPortalNodeData[] nodes,
            List<int>[] nodesByCluster,
            List<NavigationGridAbstractEdgeData>[] outgoingEdges)
        {
            int cellCount = cells.Length;
            // 按整张地图分配搜索数组，并在所有分块和入口起点之间复用
            var costs = new float[cellCount];
            var widths = new float[cellCount];
            var heap = new int[cellCount];
            var heapPositions = new int[cellCount];

            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                List<int> clusterNodes = nodesByCluster[clusterIndex];
                // 依次从每个入口节点出发，计算它到同分块其他入口的连接
                for (int sourceListIndex = 0; sourceListIndex < clusterNodes.Count; sourceListIndex++)
                {
                    int sourceNodeIndex = clusterNodes[sourceListIndex];
                    int sourceCellIndex = nodes[sourceNodeIndex].CellIndex;
                    // 最低移动成本用于比较路线快慢
                    CalculateShortestCosts(
                        cells,
                        width,
                        clusterIndex,
                        sourceCellIndex,
                        cellSize,
                        costs,
                        heap,
                        heapPositions);
                    // 最宽可行路线单独记录，用于运行时判断角色体型是否能通过
                    CalculateWidestClearance(
                        cells,
                        width,
                        clusterIndex,
                        sourceCellIndex,
                        widths,
                        heap,
                        heapPositions);

                    for (int targetListIndex = 0;
                         targetListIndex < clusterNodes.Count;
                         targetListIndex++)
                    {
                        int targetNodeIndex = clusterNodes[targetListIndex];
                        // 不创建入口节点连接到自身的零成本边
                        if (targetNodeIndex == sourceNodeIndex)
                        {
                            continue;
                        }

                        int targetCellIndex = nodes[targetNodeIndex].CellIndex;
                        // 两入口不可达或无法确认可用宽度时，不生成抽象连接
                        if (float.IsPositiveInfinity(costs[targetCellIndex]) ||
                            widths[targetCellIndex] < 0f)
                        {
                            continue;
                        }

                        outgoingEdges[sourceNodeIndex].Add(new NavigationGridAbstractEdgeData
                        {
                            ToNodeIndex = targetNodeIndex,
                            // 最低成本和最大可用宽度分别预计算；它们不一定来自同一条格子路线
                            StaticCost = costs[targetCellIndex],
                            MinimumClearance = widths[targetCellIndex],
                            CrossesPortal = false,
                        });
                    }
                }
            }
        }

        private static void CalculateShortestCosts(
            NavigationGridCellData[] cells,
            int width,
            int clusterId,
            int sourceCellIndex,
            float cellSize,
            float[] costs,
            int[] heap,
            int[] heapPositions)
        {
            // 从新入口开始搜索前重置成本，避免读取上一个入口留下的数据
            Array.Fill(costs, float.PositiveInfinity);
            Array.Fill(heapPositions, -1);
            int heapCount = 0;
            costs[sourceCellIndex] = 0f;
            IndexedFloatHeap.PushMin(sourceCellIndex, costs, heap, heapPositions, ref heapCount);

            // 地形成本都不为负，因此可以在分块内使用 Dijkstra
            while (heapCount > 0)
            {
                int current = IndexedFloatHeap.PopMin(costs, heap, heapPositions, ref heapCount);
                int currentX = current % width;
                int currentZ = current / width;
                NavigationNeighborMask mask = cells[current].NeighborMask;
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    // NeighborMask 已经排除了斜向穿过障碍尖角的情况
                    if ((mask & (NavigationNeighborMask)(1 << directionIndex)) == 0)
                    {
                        continue;
                    }

                    NavigationGridDirections.GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighbor = currentX + deltaX + (currentZ + deltaZ) * width;
                    // 搜索到达分块边界后不再向外扩展
                    if (cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 斜走距离按根号二计算，并使用所进入格子的地形成本
                    float distance = cellSize * (deltaX != 0 && deltaZ != 0 ? SquareRootTwo : 1f);
                    float candidate = costs[current] + distance * Math.Max(
                        MinimumTerrainCost,
                        cells[neighbor].TerrainCost);
                    // 只有找到更低成本时才更新最小堆
                    if (candidate + CostEpsilon >= costs[neighbor])
                    {
                        continue;
                    }

                    costs[neighbor] = candidate;
                    IndexedFloatHeap.PushMin(neighbor, costs, heap, heapPositions, ref heapCount);
                }
            }
        }

        private static void CalculateWidestClearance(
            NavigationGridCellData[] cells,
            int width,
            int clusterId,
            int sourceCellIndex,
            float[] widths,
            int[] heap,
            int[] heapPositions)
        {
            // 负值表示从当前入口尚未到达该格子
            Array.Fill(widths, -1f);
            Array.Fill(heapPositions, -1);
            int heapCount = 0;
            // 起点路线的初始宽度等于入口代表格子的可用空间
            widths[sourceCellIndex] = cells[sourceCellIndex].Clearance;
            IndexedFloatHeap.PushMax(sourceCellIndex, widths, heap, heapPositions, ref heapCount);

            // 使用最大堆优先扩展当前最宽的候选路线
            while (heapCount > 0)
            {
                int current = IndexedFloatHeap.PopMax(widths, heap, heapPositions, ref heapCount);
                int currentX = current % width;
                int currentZ = current / width;
                NavigationNeighborMask mask = cells[current].NeighborMask;
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    if ((mask & (NavigationNeighborMask)(1 << directionIndex)) == 0)
                    {
                        continue;
                    }

                    NavigationGridDirections.GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighbor = currentX + deltaX + (currentZ + deltaZ) * width;
                    // 最宽路线和最低成本路线都不能越过当前分块边界
                    if (cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 一条路线的可用宽度等于沿途最窄格子的空间
                    float candidate = Math.Min(widths[current], cells[neighbor].Clearance);
                    if (deltaX != 0 && deltaZ != 0)
                    {
                        // 斜走时还要计入两侧正交格子，避免把障碍尖角当成宽通道
                        int sideA = currentX + deltaX + currentZ * width;
                        int sideB = currentX + (currentZ + deltaZ) * width;
                        candidate = Math.Min(
                            candidate,
                            Math.Min(cells[sideA].Clearance, cells[sideB].Clearance));
                    }
                    // 候选路线没有变宽时，无需再次加入堆
                    if (candidate <= widths[neighbor] + CostEpsilon)
                    {
                        continue;
                    }

                    widths[neighbor] = candidate;
                    IndexedFloatHeap.PushMax(neighbor, widths, heap, heapPositions, ref heapCount);
                }
            }
        }

        private static int[] FlattenClusterNodes(
            NavigationGridClusterData[] clusters,
            List<int>[] nodesByCluster)
        {
            int totalCount = 0;
            // 先统计节点索引总数，再一次性分配结果数组
            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                totalCount += nodesByCluster[clusterIndex].Count;
            }

            var result = new int[totalCount];
            int offset = 0;
            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                List<int> clusterNodes = nodesByCluster[clusterIndex];
                // 节点索引按升序写入，重复烘焙会得到相同数据
                clusterNodes.Sort();
                NavigationGridClusterData cluster = clusters[clusterIndex];
                // 每个分块只记录自己在连续节点索引数组中的起点和数量
                cluster.PortalNodeOffset = offset;
                cluster.PortalNodeCount = clusterNodes.Count;
                clusters[clusterIndex] = cluster;
                for (int index = 0; index < clusterNodes.Count; index++)
                {
                    result[offset++] = clusterNodes[index];
                }
            }

            return result;
        }

        private static NavigationGridAbstractEdgeData[] FlattenEdges(
            NavigationGridPortalNodeData[] nodes,
            List<NavigationGridAbstractEdgeData>[] outgoingEdges)
        {
            int totalCount = 0;
            for (int nodeIndex = 0; nodeIndex < outgoingEdges.Length; nodeIndex++)
            {
                // 出边按目标节点排序，运行时搜索不依赖列表的插入顺序
                outgoingEdges[nodeIndex].Sort(CompareEdges);
                totalCount += outgoingEdges[nodeIndex].Count;
            }

            var result = new NavigationGridAbstractEdgeData[totalCount];
            int offset = 0;
            for (int nodeIndex = 0; nodeIndex < outgoingEdges.Length; nodeIndex++)
            {
                NavigationGridPortalNodeData node = nodes[nodeIndex];
                // 节点通过起点和数量直接读取连续出边，无需运行时字典
                node.EdgeOffset = offset;
                node.EdgeCount = outgoingEdges[nodeIndex].Count;
                nodes[nodeIndex] = node;
                for (int edgeIndex = 0; edgeIndex < outgoingEdges[nodeIndex].Count; edgeIndex++)
                {
                    result[offset++] = outgoingEdges[nodeIndex][edgeIndex];
                }
            }

            return result;
        }

        private static int CompareEdges(
            NavigationGridAbstractEdgeData left,
            NavigationGridAbstractEdgeData right)
        {
            // 先按目标节点索引排列出边
            int targetComparison = left.ToNodeIndex.CompareTo(right.ToNodeIndex);
            if (targetComparison != 0)
            {
                return targetComparison;
            }

            // 目标相同时，穿过分块入口的连接排在前面
            return right.CrossesPortal.CompareTo(left.CrossesPortal);
        }

    }
}
