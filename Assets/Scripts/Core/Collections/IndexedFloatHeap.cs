using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Core
{
    /// <summary>
    /// 提供可按节点索引快速定位的浮点键二叉堆，并固定相同键值的弹出顺序
    /// </summary>
    public static class IndexedFloatHeap
    {
        /// <summary>
        /// 将节点按浮点键加入托管最小堆，已存在节点只向上修复
        /// </summary>
        /// <remarks>调用方必须先用负位置标记未入堆节点，修改已入堆节点时只能降低键值</remarks>
        public static void PushMin(
            int node,
            float[] values,
            int[] heap,
            int[] positions,
            ref int count,
            float epsilon = 0.00001f)
        {
            int position = positions[node];
            if (position < 0)
            {
                // 负位置表示节点当前不在堆中，追加到末尾后建立反向索引
                position = count++;
                heap[position] = node;
                positions[node] = position;
            }

            // 成本只会降低，向上修复即可恢复根路径上的堆序
            SiftUpMin(position, values, heap, positions, epsilon);
        }

        /// <summary>
        /// 从托管最小堆弹出根节点
        /// </summary>
        /// <remarks>调用方必须保证 count 大于零，closedPosition 用于标记已弹出的节点</remarks>
        public static int PopMin(
            float[] values,
            int[] heap,
            int[] positions,
            ref int count,
            float epsilon = 0.00001f,
            int closedPosition = -1)
        {
            int result = heap[0];
            // 弹出节点写入调用方指定的负位置，允许后续搜索按需重新入堆
            positions[result] = closedPosition;
            count--;
            if (count > 0)
            {
                // 末尾节点补根后向下修复，保持反向索引与堆数组同步
                heap[0] = heap[count];
                positions[heap[0]] = 0;
                SiftDownMin(0, count, values, heap, positions, epsilon);
            }

            return result;
        }

        /// <summary>
        /// 将节点按浮点键加入托管最大堆，已存在节点只向上修复
        /// </summary>
        /// <remarks>调用方必须先用负位置标记未入堆节点，修改已入堆节点时只能提高键值</remarks>
        public static void PushMax(
            int node,
            float[] values,
            int[] heap,
            int[] positions,
            ref int count,
            float epsilon = 0.00001f)
        {
            int position = positions[node];
            if (position < 0)
            {
                // 负位置表示节点当前不在堆中，追加到末尾后建立反向索引
                position = count++;
                heap[position] = node;
                positions[node] = position;
            }

            // 瓶颈值只会提高，向上修复即可恢复根路径上的堆序
            SiftUpMax(position, values, heap, positions, epsilon);
        }

        /// <summary>
        /// 从托管最大堆弹出根节点
        /// </summary>
        /// <remarks>调用方必须保证 count 大于零，closedPosition 用于标记已弹出的节点</remarks>
        public static int PopMax(
            float[] values,
            int[] heap,
            int[] positions,
            ref int count,
            float epsilon = 0.00001f,
            int closedPosition = -1)
        {
            int result = heap[0];
            // 弹出节点写入调用方指定的负位置，允许后续搜索按需重新入堆
            positions[result] = closedPosition;
            count--;
            if (count > 0)
            {
                // 末尾节点补根后向下修复，保持反向索引与堆数组同步
                heap[0] = heap[count];
                positions[heap[0]] = 0;
                SiftDownMax(0, count, values, heap, positions, epsilon);
            }

            return result;
        }

        /// <summary>
        /// 将节点按浮点键加入 Native 最小堆，已存在节点只向上修复
        /// </summary>
        /// <remarks>调用方必须先用负位置标记未入堆节点，修改已入堆节点时只能降低键值</remarks>
        public static void PushMin(
            int node,
            NativeArray<float> values,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count,
            float epsilon = 0.00001f)
        {
            int position = positions[node];
            if (position < 0)
            {
                // 负位置表示节点当前不在堆中，追加到末尾后建立反向索引
                position = count++;
                heap[position] = node;
                positions[node] = position;
            }

            // 成本只会降低，向上修复即可恢复根路径上的堆序
            SiftUpMin(position, values, heap, positions, epsilon);
        }

        /// <summary>
        /// 从 Native 最小堆弹出根节点
        /// </summary>
        /// <remarks>调用方必须保证 count 大于零，closedPosition 用于标记已弹出的节点</remarks>
        public static int PopMin(
            NativeArray<float> values,
            NativeArray<int> heap,
            NativeArray<int> positions,
            ref int count,
            float epsilon = 0.00001f,
            int closedPosition = -1)
        {
            int result = heap[0];
            // 弹出节点写入调用方指定的负位置，允许后续搜索按需重新入堆
            positions[result] = closedPosition;
            count--;
            if (count > 0)
            {
                // 末尾节点补根后向下修复，保持反向索引与堆数组同步
                heap[0] = heap[count];
                positions[heap[0]] = 0;
                SiftDownMin(0, count, values, heap, positions, epsilon);
            }

            return result;
        }

        private static void SiftUpMin(
            int position,
            float[] values,
            int[] heap,
            int[] positions,
            float epsilon)
        {
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (!IsLower(heap[position], heap[parent], values, epsilon))
                {
                    break;
                }

                Swap(position, parent, heap, positions);
                position = parent;
            }
        }

        private static void SiftDownMin(
            int position,
            int count,
            float[] values,
            int[] heap,
            int[] positions,
            float epsilon)
        {
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= count)
                {
                    return;
                }

                int right = left + 1;
                int best = right < count && IsLower(heap[right], heap[left], values, epsilon)
                    ? right
                    : left;
                if (!IsLower(heap[best], heap[position], values, epsilon))
                {
                    return;
                }

                Swap(position, best, heap, positions);
                position = best;
            }
        }

        private static void SiftUpMax(
            int position,
            float[] values,
            int[] heap,
            int[] positions,
            float epsilon)
        {
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (!IsHigher(heap[position], heap[parent], values, epsilon))
                {
                    break;
                }

                Swap(position, parent, heap, positions);
                position = parent;
            }
        }

        private static void SiftDownMax(
            int position,
            int count,
            float[] values,
            int[] heap,
            int[] positions,
            float epsilon)
        {
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= count)
                {
                    return;
                }

                int right = left + 1;
                int best = right < count && IsHigher(heap[right], heap[left], values, epsilon)
                    ? right
                    : left;
                if (!IsHigher(heap[best], heap[position], values, epsilon))
                {
                    return;
                }

                Swap(position, best, heap, positions);
                position = best;
            }
        }

        private static void SiftUpMin(
            int position,
            NativeArray<float> values,
            NativeArray<int> heap,
            NativeArray<int> positions,
            float epsilon)
        {
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (!IsLower(heap[position], heap[parent], values, epsilon))
                {
                    break;
                }

                Swap(position, parent, heap, positions);
                position = parent;
            }
        }

        private static void SiftDownMin(
            int position,
            int count,
            NativeArray<float> values,
            NativeArray<int> heap,
            NativeArray<int> positions,
            float epsilon)
        {
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= count)
                {
                    return;
                }

                int right = left + 1;
                int best = right < count && IsLower(heap[right], heap[left], values, epsilon)
                    ? right
                    : left;
                if (!IsLower(heap[best], heap[position], values, epsilon))
                {
                    return;
                }

                Swap(position, best, heap, positions);
                position = best;
            }
        }

        private static bool IsLower(int left, int right, float[] values, float epsilon)
        {
            // 键值相同按节点索引打破平局，保证不同运行顺序仍得到稳定弹出顺序
            return values[left] < values[right] - epsilon ||
                   (math.abs(values[left] - values[right]) <= epsilon && left < right);
        }

        private static bool IsHigher(int left, int right, float[] values, float epsilon)
        {
            // 最大堆也按节点索引处理相同键值，保证每次运行的弹出顺序一致
            return values[left] > values[right] + epsilon ||
                   (math.abs(values[left] - values[right]) <= epsilon && left < right);
        }

        private static bool IsLower(
            int left,
            int right,
            NativeArray<float> values,
            float epsilon)
        {
            return values[left] < values[right] - epsilon ||
                   (math.abs(values[left] - values[right]) <= epsilon && left < right);
        }

        private static void Swap(int left, int right, int[] heap, int[] positions)
        {
            // 堆数组和反向位置必须同时交换，否则后续改善会从错误位置修复
            int value = heap[left];
            heap[left] = heap[right];
            heap[right] = value;
            positions[heap[left]] = left;
            positions[heap[right]] = right;
        }

        private static void Swap(
            int left,
            int right,
            NativeArray<int> heap,
            NativeArray<int> positions)
        {
            int value = heap[left];
            heap[left] = heap[right];
            heap[right] = value;
            positions[heap[left]] = left;
            positions[heap[right]] = right;
        }
    }
}
