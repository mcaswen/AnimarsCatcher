using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 查找和保存可复用的 Flow Field，避免相同目标与通道重复计算
    /// </summary>
    public static class NavigationFlowFieldCache
    {
        private const int MaximumCacheEntries = 64;

        // 缓存容器由 ECS 系统创建和释放，本类只读写其中的数据片段
        // 浮点参数按精确位模式参与缓存键，不能让不同体型或成本的请求近似合并
        // 通道哈希只用于快速筛选，命中后仍会逐个比较完整分块序列
        // 输出始终复制到当前批次列表，不能暴露跨批次缓存容器的切片
        // 动态障碍签名只覆盖当前通道，通道外变化不会让缓存失效
        // 缓存最多保留 64 项，达到上限后整代清理所有索引和数据片段
        // 命中时仍需复制 Flow Field，因为每个 ECS Buffer 必须拥有自己的结果
        // 起点成本仅在当前稀疏 Flow Field 中扫描一次
        // 清理缓存不会影响已经复制到当前后台任务输出中的结果
        // 整代清理也会回收因局部失效而不再被索引引用的旧数据
        // 触发清理的新请求会立刻成为新一代第一项，不会被丢弃
        // 这不是 LRU：不记录访问时间，也不会在每次命中时改写缓存

        internal static bool TryAppendCachedField(
            int targetCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight,
            uint corridorHash,
            uint cacheVersion,
            uint dynamicOverlaySignature,
            ref NativeList<int> corridorClusters,
            ref NativeList<NavigationFlowFieldCacheEntry> cacheEntries,
            ref NativeList<int> cacheCorridorClusters,
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells,
            ref NativeList<NavigationFlowFieldCell> output)
        {
            // 使用浮点位模式比较，保证体型或惩罚参数有任何差异都会使用不同缓存项
            int requiredClearanceBits = math.asint(requiredClearance);
            int penaltyBits = math.asint(clearancePenaltyWeight);
            for (int entryIndex = 0; entryIndex < cacheEntries.Length; entryIndex++)
            {
                NavigationFlowFieldCacheEntry entry = cacheEntries[entryIndex];
                // 导航网格变化或缓存换代后版本会递增，旧数据不能跨版本复用
                if (entry.TargetCellIndex != targetCellIndex ||
                    entry.RequiredClearanceBits != requiredClearanceBits ||
                    entry.ClearancePenaltyWeightBits != penaltyBits ||
                    entry.CorridorHash != corridorHash ||
                    entry.CorridorCount != corridorClusters.Length ||
                    entry.CacheVersion != cacheVersion ||
                    entry.DynamicOverlaySignature != dynamicOverlaySignature ||
                    entry.CorridorOffset < 0 ||
                    entry.CorridorOffset + entry.CorridorCount > cacheCorridorClusters.Length ||
                    entry.FieldOffset < 0 ||
                    entry.FieldOffset + entry.FieldCount > cacheFlowCells.Length)
                {
                    continue;
                }

                bool corridorMatches = true;

                for (int index = 0; index < corridorClusters.Length; index++)
                {
                    // 哈希相同仍要逐项比较，而且经过分块的顺序也是缓存键的一部分
                    if (cacheCorridorClusters[entry.CorridorOffset + index] !=
                        corridorClusters[index])
                    {
                        corridorMatches = false;
                        break;
                    }
                }

                // 任一分块或顺序不同都不能命中该缓存项
                if (!corridorMatches)
                {
                    continue;
                }

                // 将命中结果复制到本批输出，保证当前请求拥有独立且连续的数据
                for (int index = 0; index < entry.FieldCount; index++)
                {
                    // 整项复制可保持格子索引、成本和方向互相对应
                    output.Add(cacheFlowCells[entry.FieldOffset + index]);
                }

                return true;
            }

            return false;
        }

        internal static void AddCachedField(
            int targetCellIndex,
            float requiredClearance,
            float clearancePenaltyWeight,
            uint corridorHash,
            uint cacheVersion,
            int fieldOffset,
            int fieldCount,
            ref NativeList<int> corridorClusters,
            ref NativeList<NavigationFlowFieldCell> flowCells,
            ref NativeList<NavigationFlowFieldCacheEntry> cacheEntries,
            ref NativeList<int> cacheCorridorClusters,
            ref NativeList<NavigationFlowFieldCell> cacheFlowCells,
            uint dynamicOverlaySignature)
        {
            // 空 Flow Field 不值得缓存，但当前请求本身的结果仍然有效
            if (fieldCount <= 0)
            {
                return;
            }

            // 达到容量上限时整代回收；已经命中的结果早已复制到本批输出，不受影响
            // 这样既不会永远只缓存最早的 64 个目标，也能回收失效后遗留的数据
            if (cacheEntries.Length >= MaximumCacheEntries)
            {
                cacheEntries.Clear();
                cacheCorridorClusters.Clear();
                cacheFlowCells.Clear();
            }

            // 缓存只在末尾追加，不移动当前批次可能正在引用的数据
            int corridorOffset = cacheCorridorClusters.Length;
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                cacheCorridorClusters.Add(corridorClusters[index]);
            }

            // 先记录起始位置，再把整个 Flow Field 连续追加到缓存列表
            int cacheFieldOffset = cacheFlowCells.Length;
            for (int index = 0; index < fieldCount; index++)
            {
                cacheFlowCells.Add(flowCells[fieldOffset + index]);
            }

            // 最后才添加缓存索引，确保能被查到的项目已经拥有完整通道和 Flow Field
            cacheEntries.Add(new NavigationFlowFieldCacheEntry
            {
                TargetCellIndex = targetCellIndex,
                RequiredClearanceBits = math.asint(requiredClearance),
                ClearancePenaltyWeightBits = math.asint(clearancePenaltyWeight),
                CorridorHash = corridorHash,
                CorridorOffset = corridorOffset,
                CorridorCount = corridorClusters.Length,
                FieldOffset = cacheFieldOffset,
                FieldCount = fieldCount,
                CacheVersion = cacheVersion,
                DynamicOverlaySignature = dynamicOverlaySignature,
            });
        }

        internal static uint CalculateOverlaySignature(
            ref NativeList<int> corridorClusters,
            NativeArray<NavigationDynamicOverlayCluster> dynamicOverlayClusters)
        {
            // 按通道顺序对分块索引和动态障碍版本计算 FNV-1a 签名
            // 只有通道内变化会让这个局部 Flow Field 失效
            uint hash = 2166136261u;
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                int clusterIndex = corridorClusters[index];
                hash ^= (uint)clusterIndex;
                hash *= 16777619u;
                uint version = dynamicOverlayClusters.IsCreated &&
                               clusterIndex >= 0 &&
                               clusterIndex < dynamicOverlayClusters.Length
                    ? dynamicOverlayClusters[clusterIndex].Version
                    : 0u;
                hash ^= version;
                hash *= 16777619u;
            }

            return hash == 0u ? 1u : hash;
        }

        internal static int RequiredClearanceBits(float requiredClearance)
        {
            return math.asint(requiredClearance);
        }
    }
}
