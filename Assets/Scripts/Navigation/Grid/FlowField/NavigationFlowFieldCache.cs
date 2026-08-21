using Unity.Collections;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 管理 Flow Field 缓存键、版本和独立结果切片
    /// </summary>
    public static class NavigationFlowFieldCache
    {
        private const int MaximumCacheEntries = 64;

        // 缓存容器由 ECS System 持有和释放，本类只处理纯数据切片
        // 浮点键使用位表示，不能改为近似比较合并不同体型的请求
        // Corridor Hash 只承担初筛，命中后仍逐项核对完整 Cluster 序列
        // 输出始终复制到当前批次列表，不能暴露跨批次缓存容器的切片
        // Overlay 签名只覆盖 Corridor 内 Cluster，保留局部失效语义
        // 缓存上限同时限制 Entry 数和长期保留的切片代数
        // 命中时的 Field copy 是 ECS Buffer 独立所有权所必需的线性操作
        // start-cost 查询仍是当前 Field 切片内的单次线性扫描
        // 容量换代不会影响已经复制到当前 Job 输出的结果
        // 整代清理也会回收局部失效后遗留但不再被 Entry 引用的旧切片
        // 新一代立即接纳触发换代的目标，避免缓存永久冻结在首批请求
        // 该策略不是 LRU，不维护访问时间，也不引入每次命中的写操作
        // 64 项以内保持原有追加和确定性命中顺序

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
            // 浮点参数按位进入键，避免近似比较合并体型或代价不同的请求
            int requiredClearanceBits = math.asint(requiredClearance);
            int penaltyBits = math.asint(clearancePenaltyWeight);
            for (int entryIndex = 0; entryIndex < cacheEntries.Length; entryIndex++)
            {
                NavigationFlowFieldCacheEntry entry = cacheEntries[entryIndex];
                // 版本随 Grid 变化或容量回收递增，过期切片不能跨代复用
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
                    // Hash 碰撞时仍需逐项比较，Cluster 顺序也是缓存键的一部分
                    if (cacheCorridorClusters[entry.CorridorOffset + index] !=
                        corridorClusters[index])
                    {
                        corridorMatches = false;
                        break;
                    }
                }

                // 任一 Cluster 不同都使 Hash 初筛失效
                if (!corridorMatches)
                {
                    continue;
                }

                // 批次输出拥有独立连续切片，不能直接暴露跨批次缓存容器
                for (int index = 0; index < entry.FieldCount; index++)
                {
                    // 复制保持缓存 Field 的 CellIndex 与成本配对关系
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
            // 空 Field 不进入缓存，当前请求结果仍保持有效
            if (fieldCount <= 0)
            {
                return;
            }

            // 容量满时按代整体回收；命中结果已复制到批次输出，不持有这些切片
            // 这种有界代际淘汰避免缓存被前 64 个目标永久冻结，也同步回收失效切片
            if (cacheEntries.Length >= MaximumCacheEntries)
            {
                cacheEntries.Clear();
                cacheCorridorClusters.Clear();
                cacheFlowCells.Clear();
            }

            // 缓存只追加切片，不搬移可能正被当前批次引用的数据
            int corridorOffset = cacheCorridorClusters.Length;
            for (int index = 0; index < corridorClusters.Length; index++)
            {
                cacheCorridorClusters.Add(corridorClusters[index]);
            }

            // FieldOffset 在复制前捕获，随后 Field 作为一个连续值切片追加
            int cacheFieldOffset = cacheFlowCells.Length;
            for (int index = 0; index < fieldCount; index++)
            {
                cacheFlowCells.Add(flowCells[fieldOffset + index]);
            }

            // 元数据最后发布，任何可见缓存项都已经拥有完整 Corridor 和 Field
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
            // 按 Corridor 顺序对 Cluster 索引和版本执行 FNV-1a
            // Corridor 外的地图变化不会使当前局部 Field 失效
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
