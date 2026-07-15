using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Animars.Navigation.Grid
{
    public static partial class NavigationGridPathAlgorithms
    {
        // Heap 只保存 Open Set 节点 比较顺序固定为 F Cost、H Cost 和 Cell Index
        // HeapPositions 同时保存反向索引 允许松弛已有节点时按对数复杂度向上修复
        private static void PushHeap(
            int cellIndex,
            int endCellIndex,
            ref NavigationGridBlob grid,
            NativeArray<float> gCosts,
            NativeArray<int> heap,
            NativeArray<int> heapPositions,
            ref int heapCount)
        {
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
            // 根节点移出后立即标为 Closed 防止邻居再次尝试修改已经展开的节点
            int result = heap[0];
            heapPositions[result] = -1;
            heapCount--;
            if (heapCount <= 0)
            {
                return result;
            }

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
            // 每次交换同步更新反向索引 保持 Heap 与 HeapPositions 双向一致
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
            // 选择左右子节点中排序键更小者 避免结构相同但路径结果不稳定
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
            // F Cost 相同时优先更接近目标的节点 最后用 Cell Index 得到全序关系
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
            // 所有 Heap 交换必须经过此方法 否则反向索引会失效
            int leftCellIndex = heap[leftPosition];
            int rightCellIndex = heap[rightPosition];
            heap[leftPosition] = rightCellIndex;
            heap[rightPosition] = leftCellIndex;
            heapPositions[leftCellIndex] = rightPosition;
            heapPositions[rightCellIndex] = leftPosition;
        }

    }
}
