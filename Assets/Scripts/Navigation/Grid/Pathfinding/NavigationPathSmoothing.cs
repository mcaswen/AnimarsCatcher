using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 路径平滑的公共查询入口，保持与旧 A* fixture 相同的离散直线规则
    /// </summary>
    public static class NavigationPathSmoothing
    {
        private const float CostEpsilon = 0.00001f;

        // Parent 链先恢复为稳定正序，再从最远候选向近处扫描
        // 几何直连还必须满足原始路径成本容差，不能穿过高成本地形
        // 平滑查询复用 Query 和 Traversal，不建立第二套可见性规则
        // 输出始终显式保留投影起点和终点

        public static bool TryCalculateLineCost(
            ref NavigationGridBlob grid,
            int fromCellIndex,
            int toCellIndex,
            float agentRadius,
            float clearanceMargin,
            float clearancePenaltyWeight,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            out float lineCost)
        {
            return NavigationGridQuery.TryCalculateLineCost(
                ref grid,
                fromCellIndex,
                toCellIndex,
                agentRadius,
                clearanceMargin,
                clearancePenaltyWeight,
                dynamicOverlay,
                out lineCost);
        }
        internal static bool AppendSmoothedPath(
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            int startCellIndex,
            int endCellIndex,
            ref NativeList<int> pathCells,
            NativeArray<float> gCosts,
            NativeArray<int> parents,
            NativeArray<int> reconstruction,
            NativeArray<NavigationDynamicOverlayCell> dynamicOverlay,
            out int pathLength)
        {
            // 父链先恢复稳定顺序，重建异常时调用方会丢弃本请求切片
            pathLength = 0;
            int rawPathLength = 0;
            int currentIndex = endCellIndex;
            // Parent 链从终点逆向写入 reconstruction 不额外分配临时 NativeList
            // Parent 链长度不能超过 Cell 数，超出说明链损坏或形成循环
            while (currentIndex >= 0 && rawPathLength < reconstruction.Length)
            {
                reconstruction[rawPathLength++] = currentIndex;
                if (currentIndex == startCellIndex)
                {
                    break;
                }

                currentIndex = parents[currentIndex];
            }

            if (rawPathLength <= 0 || reconstruction[rawPathLength - 1] != startCellIndex)
            {
                return false;
            }

            int outputStart = pathCells.Length;
            // 成功路径始终显式保留投影起点
            pathCells.Add(startCellIndex);
            int anchorOrderedIndex = 0;

            // 从最远候选向近处扫描，直连还必须满足原路径的成本容差
            // 几何可见性不能单独放行穿过高 Terrain Cost 区域的平滑线
            while (anchorOrderedIndex < rawPathLength - 1)
            {
                int anchorCellIndex = GetOrderedRawPathCell(
                    reconstruction,
                    rawPathLength,
                    anchorOrderedIndex);
                int selectedOrderedIndex = anchorOrderedIndex + 1;
                // 从最远候选向回扫描使每个锚点选择确定性的最大跨越
                for (int candidateOrderedIndex = rawPathLength - 1;
                     candidateOrderedIndex > anchorOrderedIndex;
                     candidateOrderedIndex--)
                {
                    int candidateCellIndex = GetOrderedRawPathCell(
                        reconstruction,
                        rawPathLength,
                        candidateOrderedIndex);
                    // A 星 Parent 链上的 G Cost 单调递增，差值就是原路径分段成本
                    float rawSegmentCost = math.max(
                        0f,
                        gCosts[candidateCellIndex] - gCosts[anchorCellIndex]);
                    if (NavigationGridQuery.TryCalculateLineCost(
                            ref grid,
                            anchorCellIndex,
                            candidateCellIndex,
                            request.AgentRadius,
                            request.ClearanceMargin,
                            request.ClearancePenaltyWeight,
                            dynamicOverlay,
                            out float directCost) &&
                        directCost <=
                        rawSegmentCost * (1f + request.SmoothingCostTolerance) + CostEpsilon)
                    {
                        selectedOrderedIndex = candidateOrderedIndex;
                        break;
                    }
                }

                pathCells.Add(GetOrderedRawPathCell(
                    reconstruction,
                    rawPathLength,
                    selectedOrderedIndex));
                anchorOrderedIndex = selectedOrderedIndex;
            }

            pathLength = pathCells.Length - outputStart;
            return pathLength > 0;
        }

        private static int GetOrderedRawPathCell(
            NativeArray<int> reconstruction,
            int rawPathLength,
            int orderedIndex)
        {
            // reconstruction 逆序保存父链，此处统一暴露起点到终点的正序视图
            return reconstruction[rawPathLength - 1 - orderedIndex];
        }

    }
}
