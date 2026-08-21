using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 管理 A* 的待搜索节点，并能快速找到总成本最低的格子
    /// </summary>
    public static class NavigationAStarOpenSet
    {
        private const float CostEpsilon = 0.00001f;

        // HeapPositions 同时记录节点在堆中的位置，以及尚未发现或已经关闭的状态
        // 总成本相同时再比较启发值和格子索引，让搜索顺序保持一致
        // 数组由寻路系统创建和释放，本结构只负责使用它们
        // 待搜索集合采用数组最小堆，负的位置值表示节点已经离开集合
        internal static void PushHeap(
            int cellIndex,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            ref int heapCount)
        {
            // 新节点先放到堆尾，再向上调整到正确位置
            int position = heapCount++;
            heap[position] = cellIndex;
            heapPositions[cellIndex] = position;
            SiftUp(
                position,
                endCellIndex,
                ref grid,
                gCosts,
                heap,
                heapPositions);
        }

        internal static int PopHeap(
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            ref int heapCount)
        {
            // 堆顶始终是下一步应展开的最低成本节点
            int result = heap[0];

            // 弹出后把位置设为负数，后续不会再更新这个已关闭节点
            heapPositions[result] = -1;
            heapCount--;
            if (heapCount <= 0)
            {
                // 移除最后一个节点后堆已为空，无需继续调整
                return result;
            }

            // 用末尾节点补到堆顶后向下调整，移除操作保持 O(log N)
            int replacement = heap[heapCount];
            heap[0] = replacement;
            heapPositions[replacement] = 0;
            SiftDown(
                0,
                heapCount,
                endCellIndex,
                ref grid,
                gCosts,
                heap,
                heapPositions);
            return result;
        }

        internal static void SiftUp(
            int position,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions)
        {
            // 路径松弛只会降低节点成本，因此只需向上调整
            while (position > 0)
            {
                int parentPosition = (position - 1) / 2;
                if (!IsHeapNodeLower(
                        heap[position],
                        heap[parentPosition],
                        endCellIndex,
                        ref grid,
                        gCosts))
                {
                    break;
                }

                SwapHeapNodes(position, parentPosition, heap, heapPositions);
                position = parentPosition;
            }
        }

        internal static void SiftDown(
            int position,
            int heapCount,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions)
        {
            // 每次与优先级更高的子节点交换，直到恢复最小堆顺序
            while (true)
            {
                int leftPosition = position * 2 + 1;
                if (leftPosition >= heapCount)
                {
                    return;
                }

                int rightPosition = leftPosition + 1;
                int bestPosition = leftPosition;
                if (rightPosition < heapCount && IsHeapNodeLower(
                        heap[rightPosition],
                        heap[leftPosition],
                        endCellIndex,
                        ref grid,
                        gCosts))
                {
                    bestPosition = rightPosition;
                }

                if (!IsHeapNodeLower(
                        heap[bestPosition],
                        heap[position],
                        endCellIndex,
                        ref grid,
                        gCosts))
                {
                    return;
                }

                SwapHeapNodes(position, bestPosition, heap, heapPositions);
                position = bestPosition;
            }
        }

        private static bool IsHeapNodeLower(
            int leftCellIndex,
            int rightCellIndex,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts)
        {
            // 依次比较总成本、启发成本和格子索引；浮点值接近时继续使用后续字段决定顺序
            float leftHeuristic = NavigationGridCost.CalculateOctileHeuristic(
                ref grid,
                leftCellIndex,
                endCellIndex);
            float rightHeuristic = NavigationGridCost.CalculateOctileHeuristic(
                ref grid,
                rightCellIndex,
                endCellIndex);
            float leftTotal = gCosts[leftCellIndex] + leftHeuristic;
            float rightTotal = gCosts[rightCellIndex] + rightHeuristic;
            if (leftTotal < rightTotal - CostEpsilon)
            {
                return true;
            }

            if (math.abs(leftTotal - rightTotal) > CostEpsilon)
            {
                // 总成本明显更高时直接排到后面
                return false;
            }

            if (leftHeuristic < rightHeuristic - CostEpsilon)
            {
                return true;
            }

            if (math.abs(leftHeuristic - rightHeuristic) > CostEpsilon)
            {
                return false;
            }

            return leftCellIndex < rightCellIndex;
        }

        private static void SwapHeapNodes(
            int leftPosition,
            int rightPosition,
            NativeArray<int> heap,
            NativeArray<int> heapPositions)
        {
            // 节点数组和反向位置表必须同时更新，保持双向对应
            int leftCellIndex = heap[leftPosition];
            int rightCellIndex = heap[rightPosition];
            heap[leftPosition] = rightCellIndex;
            heap[rightPosition] = leftCellIndex;
            heapPositions[leftCellIndex] = rightPosition;
            heapPositions[rightCellIndex] = leftPosition;
        }

    }
}
