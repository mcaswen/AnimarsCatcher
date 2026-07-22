using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.LowLevel
{
    /// <summary>
    /// 可在生成分类 System 或分类 Job 中创建 <see cref="SnapshotDataBufferComponentLookup"/> 实例的辅助结构体
    /// 由于需要获取 <see cref="SpawnedGhostEntityMap"/> 和 <see cref="SnapshotDataLookupCache"/> 数据
    /// 创建或持有该实例的 System 必须在 <see cref="GhostCollectionSystem"/> 与
    /// <see cref="GhostReceiveSystem"/> 之后创建
    /// </summary>
    public struct SnapshotDataLookupHelper
    {
        [ReadOnly] private BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerLookup;
        [ReadOnly] private BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexLookup;
        [ReadOnly] private BufferLookup<GhostCollectionComponentType> m_GhostCollectionComponentTypeLookup;
        [ReadOnly] private BufferLookup<GhostComponentSerializer.State> m_GhostCollectionSerializersLookup;
        [ReadOnly] internal NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly m_ghostMap;
        internal NativeHashMap<SnapshotLookupCacheKey, SnapshotDataLookupCache.SerializerIndexAndOffset> m_SnapshotDataLookupCache;
        internal Entity m_GhostCollectionEntity;
        /// <summary>
        /// 收集并初始化所有内部 <see cref="BufferFromEntity{T}"/> Handle
        /// 同时获取所需数据结构
        /// </summary>
        /// <param name="state">参见 <see cref="SystemState"/></param>
        /// <param name="ghostCollectionEntity">持有 GhostCollection Component 的 Entity</param>
        /// <param name="spawnMapEntity">持有 SpawnedGhostEntityMap Component 的 Entity</param>
        public SnapshotDataLookupHelper(ref SystemState state,
            Entity ghostCollectionEntity, Entity spawnMapEntity)
        {
            m_GhostCollectionPrefabSerializerLookup = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexLookup = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionComponentTypeLookup = state.GetBufferLookup<GhostCollectionComponentType>(true);
            m_GhostCollectionSerializersLookup = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            // 这会为持有该辅助类型的 System 添加正确依赖
            // 此场景并不需要长期持有 Lookup，因此只获取一次
            var ghostMap = state.GetComponentLookup<SpawnedGhostEntityMap>(true);
            var lookupCache = state.GetComponentLookup<SnapshotDataLookupCache>();
            m_ghostMap = ghostMap[spawnMapEntity].Value;
            m_SnapshotDataLookupCache = lookupCache[ghostCollectionEntity].ComponentDataOffsets;
            m_GhostCollectionEntity = ghostCollectionEntity;
        }

        /// <summary>
        /// 在 System 的 OnUpdate 中调用此方法以刷新所有内部 <see cref="BufferFromEntity{T}"/> Handle
        /// </summary>
        /// <param name="state">参见 <see cref="SystemState"/></param>
        public void Update(ref SystemState state)
        {
            m_GhostCollectionPrefabSerializerLookup.Update(ref state);
            m_GhostCollectionComponentIndexLookup.Update(ref state);
            m_GhostCollectionComponentTypeLookup.Update(ref state);
            m_GhostCollectionSerializersLookup.Update(ref state);
        }

        /// <summary>
        /// 创建可在主线程或 Job 中使用的 <see cref="SnapshotDataBufferComponentLookup"/> 实例
        /// 由于内部会获取所有必要的 <see cref="DynamicBuffer{T}"/>，该方法会引入同步点
        /// </summary>
        /// <remarks>
        /// 调用前必须已经调用 <see cref="Update"/>，并完成所有内部 Handle 的更新
        /// </remarks>
        /// <returns>有效的 <see cref="SnapshotDataBufferComponentLookup"/> 实例</returns>
        public SnapshotDataBufferComponentLookup CreateSnapshotBufferLookup()
        {
            return new SnapshotDataBufferComponentLookup(
                m_GhostCollectionPrefabSerializerLookup[m_GhostCollectionEntity],
                m_GhostCollectionComponentIndexLookup[m_GhostCollectionEntity],
                m_GhostCollectionComponentTypeLookup[m_GhostCollectionEntity],
                m_GhostCollectionSerializersLookup[m_GhostCollectionEntity],
                m_SnapshotDataLookupCache,
                m_ghostMap);
        }
    }
}
