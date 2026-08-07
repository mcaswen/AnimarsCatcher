using System;
using System.Collections.Generic;
using AnimarsCatcher.Core;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存由静态 Cell 拓扑派生出的 HPA 星分层导航数据
    /// </summary>
    public sealed class NavigationGridHierarchyBuildResult
    {
        // 保存 Grid 在 X 轴生成的 Cluster 数量
        public int ClusterWidth;

        // 保存 Grid 在 Z 轴生成的 Cluster 数量
        public int ClusterHeight;

        // 保存按 ClusterId 排列的规则分块
        public NavigationGridClusterData[] Clusters = Array.Empty<NavigationGridClusterData>();

        // 保存按边界扫描顺序排列的连续 Portal
        public NavigationGridPortalData[] Portals = Array.Empty<NavigationGridPortalData>();

        // 保存每个 Portal 两侧对应的抽象节点
        public NavigationGridPortalNodeData[] PortalNodes = Array.Empty<NavigationGridPortalNodeData>();

        // 保存由 Portal Node 切片引用的有向边
        public NavigationGridAbstractEdgeData[] AbstractEdges = Array.Empty<NavigationGridAbstractEdgeData>();

        // 保存由 Cluster 切片引用的 Portal Node 索引
        public int[] ClusterPortalNodeIndices = Array.Empty<int>();
    }

    /// <summary>
    /// 从确定性 Grid 拓扑构建 Cluster、Portal 和 HPA 星抽象图
    /// </summary>
    public static class NavigationGridHierarchyBuilder
    {
        private const float MinimumTerrainCost = 0.01f;
        private const float SquareRootTwo = 1.41421356237f;
        private const float CostEpsilon = 0.00001f;
        private const float ClearanceQuantizationScale = 10_000f;

        /// <summary>
        /// 构建 Portal 区间、Portal 双端节点和 Cluster 内静态成本边
        /// </summary>
        /// <param name="cells">已经完成邻接、Clearance、Region 和 Cluster 分配的 Cell</param>
        /// <param name="width">Grid 在 X 轴的 Cell 数量</param>
        /// <param name="height">Grid 在 Z 轴的 Cell 数量</param>
        /// <param name="clusterSizeInCells">规则 Cluster 的 Cell 边长</param>
        /// <param name="cellSize">Cell 在 XZ 平面的世界边长</param>
        /// <returns>可写入 Bake Asset 和运行时 Blob 的稳定分层数据</returns>
        public static NavigationGridHierarchyBuildResult Build(
            NavigationGridCellData[] cells,
            int width,
            int height,
            int clusterSizeInCells,
            float cellSize)
        {
            // Cell 数量必须与二维 Grid 形状完全对应
            if (cells == null || width <= 0 || height <= 0 || cells.Length != width * height)
            {
                throw new ArgumentException("Navigation Grid hierarchy input shape is invalid");
            }

            // 分块尺寸和世界步长都必须为正值
            if (clusterSizeInCells <= 0 || cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(clusterSizeInCells));
            }

            // Cluster 按行主序编号，边缘分块可以小于配置尺寸
            int clusterWidth = (width + clusterSizeInCells - 1) / clusterSizeInCells;
            int clusterHeight = (height + clusterSizeInCells - 1) / clusterSizeInCells;
            NavigationGridClusterData[] clusters = CreateClusters(
                width,
                height,
                clusterSizeInCells,
                clusterWidth,
                clusterHeight);

            var portals = new List<NavigationGridPortalData>();
            // 先扫描竖直边界，固定 Portal 顺序以稳定资产 Hash
            for (int boundaryX = clusterSizeInCells;
                 boundaryX < width;
                 boundaryX += clusterSizeInCells)
            {
                // boundaryX 左右两列分别属于相邻 Cluster
                ScanVerticalBoundary(cells, width, height, boundaryX, cellSize, portals);
            }

            // 水平边界接在竖直边界之后，重复烘焙保持相同顺序
            for (int boundaryZ = clusterSizeInCells;
                 boundaryZ < height;
                 boundaryZ += clusterSizeInCells)
            {
                // boundaryZ 下上两行分别属于相邻 Cluster
                ScanHorizontalBoundary(cells, width, height, boundaryZ, cellSize, portals);
            }

            // 每个 Portal 分配两个连续节点，分别归属边界两侧 Cluster
            var nodes = new NavigationGridPortalNodeData[portals.Count * 2];
            var nodesByCluster = new List<int>[clusters.Length];
            for (int clusterIndex = 0; clusterIndex < clusters.Length; clusterIndex++)
            {
                nodesByCluster[clusterIndex] = new List<int>();
            }

            // 建立 Cluster 到其 Portal Node 的反向索引
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

            // 每个 Portal Node 独占一个出边列表，最后再扁平化
            var outgoingEdges = new List<NavigationGridAbstractEdgeData>[nodes.Length];
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                outgoingEdges[nodeIndex] = new List<NavigationGridAbstractEdgeData>();
            }

            // 先加入 Portal 跨边，再计算受限于单个 Cluster 的内部边
            AddPortalCrossingEdges(portals, outgoingEdges);
            AddClusterConnectionEdges(
                cells,
                width,
                cellSize,
                nodes,
                nodesByCluster,
                outgoingEdges);

            // 将 Cluster 节点索引和 Node 出边固化为 Blob 可用的连续切片
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
            // 外层遍历 Z、内层遍历 X，使 ClusterId 保持行主序
            for (int clusterZ = 0; clusterZ < clusterHeight; clusterZ++)
            {
                for (int clusterX = 0; clusterX < clusterWidth; clusterX++)
                {
                    int clusterIndex = clusterX + clusterZ * clusterWidth;
                    // 最大边界采用半开区间，并裁剪到实际 Grid 尺寸
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
            // runStart 为负表示当前没有正在聚合的 Portal 游程
            int runStart = -1;
            int runEnd = -1;
            int runClusterA = -1;
            int runClusterB = -1;
            int runRegion = -1;
            int runClearanceBucket = -1;
            float runMinimumClearance = float.PositiveInfinity;

            // 末尾哨兵 z 等于 height，用同一分支提交最后一个游程
            for (int z = 0; z <= height; z++)
            {
                int clusterA = -1;
                int clusterB = -1;
                int region = -1;
                int clearanceBucket = -1;
                float clearance = 0f;
                // 哨兵位置不读取 Cell，只让 valid 变为 false 触发提交
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
                // Cluster 对、Region 或量化 Clearance 变化都会中断当前游程
                bool continues = valid &&
                                 runStart >= 0 &&
                                 clusterA == runClusterA &&
                                 clusterB == runClusterB &&
                                 region == runRegion &&
                                 clearanceBucket == runClearanceBucket;
                if (continues)
                {
                    runEnd = z;
                    // Portal Clearance 取区间最小值，对整个通道保持保守
                    runMinimumClearance = Math.Min(runMinimumClearance, clearance);
                    continue;
                }

                if (runStart >= 0)
                {
                    // 先提交旧游程，再开始新区间，保证 Portal 不重叠
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

                // 当前 Cell 对成为下一段 Portal 游程的首元素
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
            // 水平边界复用同一游程状态机，仅旋转扫描轴和邻接方向
            int runStart = -1;
            int runEnd = -1;
            int runClusterA = -1;
            int runClusterB = -1;
            int runRegion = -1;
            int runClearanceBucket = -1;
            float runMinimumClearance = float.PositiveInfinity;

            // 末尾哨兵 x 等于 width，保证尾部 Portal 不被遗漏
            for (int x = 0; x <= width; x++)
            {
                int clusterA = -1;
                int clusterB = -1;
                int region = -1;
                int clearanceBucket = -1;
                float clearance = 0f;
                // 哨兵位置跳过 Cell 访问并结束最后一段游程
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
                // 只有完整分组键相同的相邻 Cell 对才能合并
                bool continues = valid &&
                                 runStart >= 0 &&
                                 clusterA == runClusterA &&
                                 clusterB == runClusterB &&
                                 region == runRegion &&
                                 clearanceBucket == runClearanceBucket;
                if (continues)
                {
                    runEnd = x;
                    // 最小 Clearance 随区间扩展单调不增
                    runMinimumClearance = Math.Min(runMinimumClearance, clearance);
                    continue;
                }

                if (runStart >= 0)
                {
                    // 按扫描顺序直接提交，避免无序集合影响资产结果
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

                // 记录新区间的完整分组键和初始 Clearance
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
            // Cluster 对与 Region 构成 Portal 游程的拓扑分组键
            clusterA = cellA.ClusterId;
            clusterB = cellB.ClusterId;
            region = cellA.RegionId;
            // 边界通道宽度受两侧 Cell 中较窄一侧限制
            clearance = Math.Min(cellA.Clearance, cellB.Clearance);
            // Clearance 按烘焙精度量化，变化处必须拆分 Portal
            clearanceBucket = Mathf.RoundToInt(clearance * ClearanceQuantizationScale);
            // 两侧必须互相声明邻接并属于同一正 Region，单向边不构成 Portal
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
            // 偶数长度区间固定取较小 Z，保持代表点选择稳定
            int representativeZ = (startZ + endZ) / 2;
            int representativeA = boundaryX - 1 + representativeZ * width;
            int representativeB = boundaryX + representativeZ * width;
            // Portal 保存两侧完整 Cell 区间和一个稳定代表点
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
            // 偶数长度区间固定取较小 X，保持代表点选择稳定
            int representativeX = (startX + endX) / 2;
            int representativeA = representativeX + (boundaryZ - 1) * width;
            int representativeB = representativeX + boundaryZ * width;
            // 水平 Portal 使用同样的数据布局，仅索引步长不同
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
                // 整个 Portal 使用区间内的最小 Clearance
                MinimumClearance = minimumClearance,
                // 跨边成本按进入目标 Cell 的 TerrainCost 分别计算
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
                // A 到 B 使用进入 B 侧代表 Cell 的静态成本
                outgoingEdges[nodeA].Add(new NavigationGridAbstractEdgeData
                {
                    ToNodeIndex = nodeB,
                    StaticCost = portal.StaticCostAtoB,
                    MinimumClearance = portal.MinimumClearance,
                    // 路径还原只在跨 Portal 边上推进 Corridor Cluster
                    CrossesPortal = true,
                });
                // 反向边使用独立成本，但共享同一 Portal Clearance
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
            // 全图尺寸的搜索数组在所有 Cluster 和源 Portal 之间复用
            var costs = new float[cellCount];
            var widths = new float[cellCount];
            var heap = new int[cellCount];
            var heapPositions = new int[cellCount];

            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                List<int> clusterNodes = nodesByCluster[clusterIndex];
                // 每个 Portal Node 依次作为 Cluster 内完全图的源节点
                for (int sourceListIndex = 0; sourceListIndex < clusterNodes.Count; sourceListIndex++)
                {
                    int sourceNodeIndex = clusterNodes[sourceListIndex];
                    int sourceCellIndex = nodes[sourceNodeIndex].CellIndex;
                    // 最短成本只负责路线排序
                    CalculateShortestCosts(
                        cells,
                        width,
                        clusterIndex,
                        sourceCellIndex,
                        cellSize,
                        costs,
                        heap,
                        heapPositions);
                    // 最大瓶颈 Clearance 独立负责运行时体型过滤
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
                        // 完全图不生成节点到自身的零成本边
                        if (targetNodeIndex == sourceNodeIndex)
                        {
                            continue;
                        }

                        int targetCellIndex = nodes[targetNodeIndex].CellIndex;
                        // 成本不可达或瓶颈未计算时不生成抽象边
                        if (float.IsPositiveInfinity(costs[targetCellIndex]) ||
                            widths[targetCellIndex] < 0f)
                        {
                            continue;
                        }

                        outgoingEdges[sourceNodeIndex].Add(new NavigationGridAbstractEdgeData
                        {
                            ToNodeIndex = targetNodeIndex,
                            // 成本和 Clearance 分别取各自最优值，不要求来自同一条 Cell 路径
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
            // 每个源 Portal 开始前重置成本，避免复用数组残留旧搜索结果
            Array.Fill(costs, float.PositiveInfinity);
            Array.Fill(heapPositions, -1);
            int heapCount = 0;
            costs[sourceCellIndex] = 0f;
            IndexedFloatHeap.PushMin(sourceCellIndex, costs, heap, heapPositions, ref heapCount);

            // 非负地形成本允许在当前 Cluster 内使用 Dijkstra
            while (heapCount > 0)
            {
                int current = IndexedFloatHeap.PopMin(costs, heap, heapPositions, ref heapCount);
                int currentX = current % width;
                int currentZ = current / width;
                NavigationNeighborMask mask = cells[current].NeighborMask;
                for (int directionIndex = 0; directionIndex < 8; directionIndex++)
                {
                    // NeighborMask 已包含对角穿角约束
                    if ((mask & (NavigationNeighborMask)(1 << directionIndex)) == 0)
                    {
                        continue;
                    }

                    NavigationGridAlgorithms.GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighbor = currentX + deltaX + (currentZ + deltaZ) * width;
                    // Cluster 边界截断预计算搜索
                    if (cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 对角步长使用根号二，成本按进入目标 Cell 的 TerrainCost 计算
                    float distance = cellSize * (deltaX != 0 && deltaZ != 0 ? SquareRootTwo : 1f);
                    float candidate = costs[current] + distance * Math.Max(
                        MinimumTerrainCost,
                        cells[neighbor].TerrainCost);
                    // 只有严格改善的成本才需要调整最小堆
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
            // 负值表示本次源 Portal 尚未到达该 Cell
            Array.Fill(widths, -1f);
            Array.Fill(heapPositions, -1);
            int heapCount = 0;
            // 源 Cell 以自身 Clearance 作为初始瓶颈
            widths[sourceCellIndex] = cells[sourceCellIndex].Clearance;
            IndexedFloatHeap.PushMax(sourceCellIndex, widths, heap, heapPositions, ref heapCount);

            // 最大堆优先传播当前最宽的候选路径
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

                    NavigationGridAlgorithms.GetDirection(directionIndex, out int deltaX, out int deltaZ);
                    int neighbor = currentX + deltaX + (currentZ + deltaZ) * width;
                    // Widest Path 与最短成本使用相同的 Cluster 边界
                    if (cells[neighbor].ClusterId != clusterId)
                    {
                        continue;
                    }

                    // 路径宽度只能保持或收窄，取沿途 Clearance 最小值
                    float candidate = Math.Min(widths[current], cells[neighbor].Clearance);
                    if (deltaX != 0 && deltaZ != 0)
                    {
                        // 对角瓶颈还要包含两个正交侧 Cell，避免穿过几何尖角
                        int sideA = currentX + deltaX + currentZ * width;
                        int sideB = currentX + (currentZ + deltaZ) * width;
                        candidate = Math.Min(
                            candidate,
                            Math.Min(cells[sideA].Clearance, cells[sideB].Clearance));
                    }
                    // 未改善已知瓶颈的候选路径无需重新入堆
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
            // 先统计总量，结果数组只分配一次
            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                totalCount += nodesByCluster[clusterIndex].Count;
            }

            var result = new int[totalCount];
            int offset = 0;
            for (int clusterIndex = 0; clusterIndex < nodesByCluster.Length; clusterIndex++)
            {
                List<int> clusterNodes = nodesByCluster[clusterIndex];
                // 节点索引升序写入，使重复烘焙得到相同切片
                clusterNodes.Sort();
                NavigationGridClusterData cluster = clusters[clusterIndex];
                // Cluster 只保存其在连续索引数组中的偏移和数量
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
                // 出边按目标索引排序，运行时搜索不依赖 List 插入顺序
                outgoingEdges[nodeIndex].Sort(CompareEdges);
                totalCount += outgoingEdges[nodeIndex].Count;
            }

            var result = new NavigationGridAbstractEdgeData[totalCount];
            int offset = 0;
            for (int nodeIndex = 0; nodeIndex < outgoingEdges.Length; nodeIndex++)
            {
                NavigationGridPortalNodeData node = nodes[nodeIndex];
                // Node 通过偏移和数量直接定位连续出边，无需运行时字典
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
            // 目标节点是出边稳定顺序的主键
            int targetComparison = left.ToNodeIndex.CompareTo(right.ToNodeIndex);
            if (targetComparison != 0)
            {
                return targetComparison;
            }

            // 同目标时跨 Portal 边优先，避免依赖插入顺序
            return right.CrossesPortal.CompareTo(left.CrossesPortal);
        }

    }
}
