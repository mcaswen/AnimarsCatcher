using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    public static partial class NavigationGridPathAlgorithms
    {
        // Open Set 使用数组最小堆，HeapPositions 保存反向索引并用负值表示 Closed
        private static void PushHeap(
            int cellIndex,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            ref int heapCount)
        {
            // 新节点追加到堆尾，并同步反向索引后向上恢复堆序
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

        private static int PopHeap(
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            ref int heapCount)
        {
            // 根节点是排序键最小的待展开节点
            int result = heap[0];

            // 负位置表示节点已离开 Open Set，后续松弛不能再修改它
            heapPositions[result] = -1;
            heapCount--;
            if (heapCount <= 0)
            {
                // 最后一个节点移除后无需执行替换和下沉
                return result;
            }

            // 用末尾节点补根并向下修复，使移除成本保持为 O(log N)
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

        private static void SiftUp(
            int position,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions)
        {
            // 松弛只会降低 G Cost，因此从当前位置向父节点恢复堆序即可
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

        private static void SiftDown(
            int position,
            int heapCount,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions)
        {
            // 每轮与排序键更小的子节点交换，维持稳定的最小堆顺序
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
            // 比较键依次为 F Cost、H Cost 和 Cell Index，近似相等时继续比较稳定键
            float leftHeuristic = CalculateOctileHeuristic(
                ref grid,
                leftCellIndex,
                endCellIndex);
            float rightHeuristic = CalculateOctileHeuristic(
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
                // 总成本明显更高时直接判定排序靠后
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
            // 两个数组必须同步交换，维持节点与堆位置的双向映射
            int leftCellIndex = heap[leftPosition];
            int rightCellIndex = heap[rightPosition];
            heap[leftPosition] = rightCellIndex;
            heap[rightPosition] = leftCellIndex;
            heapPositions[leftCellIndex] = rightPosition;
            heapPositions[rightCellIndex] = leftPosition;
        }

    }
}
