using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在不穿过障碍或高成本区域的前提下，删除 A* 路径中多余的拐点
    /// </summary>
    public static class NavigationPathSmoothing
    {
        private const float CostEpsilon = 0.00001f;

        // 先把父节点链恢复为从起点到终点的顺序，再优先尝试连接更远的节点
        // 能直线通过还不够，新线段成本也不能超过原路径允许的范围，以免抄近路穿过昂贵地形
        // 平滑复用统一的格子查询和通行规则，不另建一套可见性判断
        // 输出始终包含纠正后的起点和终点

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
            // 先恢复父节点链；如果链损坏，调用方会丢弃这条请求的全部输出
            pathLength = 0;
            int rawPathLength = 0;
            int currentIndex = endCellIndex;
            // 父节点链从终点倒序写入已有数组，不再分配临时 NativeList
            // 链长不应超过格子总数，超过说明数据损坏或出现循环
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
            // 成功路径始终从纠正后的起点开始
            pathCells.Add(startCellIndex);
            int anchorOrderedIndex = 0;

            // 从最远候选向近处尝试直连，并检查新线段是否满足成本容差
            // 即使几何上看得见，也不能让平滑线穿过高成本地形
            while (anchorOrderedIndex < rawPathLength - 1)
            {
                int anchorCellIndex = GetOrderedRawPathCell(
                    reconstruction,
                    rawPathLength,
                    anchorOrderedIndex);
                int selectedOrderedIndex = anchorOrderedIndex + 1;
                // 每个保留点都优先连接最远可行节点，得到尽量少且可重复的拐点
                for (int candidateOrderedIndex = rawPathLength - 1;
                     candidateOrderedIndex > anchorOrderedIndex;
                     candidateOrderedIndex--)
                {
                    int candidateCellIndex = GetOrderedRawPathCell(
                        reconstruction,
                        rawPathLength,
                        candidateOrderedIndex);
                    // A* 父节点链上的累计成本只增不减，两端差值就是原路径这一段的成本
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
            // reconstruction 以终点到起点的顺序保存，这里按起点到终点读取
            return reconstruction[rawPathLength - 1 - orderedIndex];
        }

    }
}
