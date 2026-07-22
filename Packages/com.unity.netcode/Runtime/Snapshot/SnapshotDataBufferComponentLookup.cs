using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.LowLevel
{
    /// <summary>
    /// 用于检查 <see cref="SnapshotData"/> Buffer 中是否存在 Component 并获取其数据的辅助结构体
    /// 该 Lookup 可以传入 Job
    /// </summary>
    /// <remarks>
    /// 该辅助类型仅允许读取 Component 数据，不支持读取 Buffer 数据
    /// </remarks>
    public struct SnapshotDataBufferComponentLookup
    {
        [ReadOnly]DynamicBuffer<GhostCollectionPrefabSerializer> m_ghostPrefabType;
        [ReadOnly]DynamicBuffer<GhostCollectionComponentIndex> m_ghostComponentIndices;
        [ReadOnly]DynamicBuffer<GhostCollectionComponentType> m_ghostComponentTypes;
        [ReadOnly]DynamicBuffer<GhostComponentSerializer.State> m_ghostSerializers;
        readonly NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly m_ghostMap;
        NativeHashMap<SnapshotLookupCacheKey, SnapshotDataLookupCache.SerializerIndexAndOffset> m_componentOffsetCacheRW;

        internal SnapshotDataBufferComponentLookup(
            in DynamicBuffer<GhostCollectionPrefabSerializer> ghostPrefabType,
            in DynamicBuffer<GhostCollectionComponentIndex> ghostComponentIndices,
            in DynamicBuffer<GhostCollectionComponentType> ghostComponentTypes,
            in DynamicBuffer<GhostComponentSerializer.State> ghostSerializers,
            in NativeHashMap<SnapshotLookupCacheKey, SnapshotDataLookupCache.SerializerIndexAndOffset> componentOffsetCache,
            in NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap)
        {
           m_ghostPrefabType = ghostPrefabType;
           m_ghostComponentIndices = ghostComponentIndices;
           m_ghostComponentTypes = ghostComponentTypes;
           m_ghostSerializers = ghostSerializers;
           m_componentOffsetCacheRW = componentOffsetCache;
           m_ghostMap = ghostMap;
        }

        /// <summary>
        /// 检查正在生成的 Ghost 是否使用 Owner Predicted 模式
        /// </summary>
        /// <param name="ghost">正在生成的 Ghost</param>
        /// <returns>Ghost 使用 Owner Predicted 模式时返回 true</returns>
        public bool IsOwnerPredicted(in GhostSpawnBuffer ghost)
        {
            return m_ghostPrefabType.ElementAtRO(ghost.GhostType).OwnerPredicted != 0;
        }

        /// <summary>
        /// 检查正在生成的 Ghost 是否具有 <see cref="GhostOwner"/>
        /// </summary>
        /// <param name="ghost">正在生成的 Ghost</param>
        /// <returns>Ghost 具有 <see cref="GhostOwner"/> 时返回 true</returns>
        public bool HasGhostOwner(in GhostSpawnBuffer ghost)
        {
            return m_ghostPrefabType.ElementAtRO(ghost.GhostType).PredictionOwnerOffset != 0;
        }

        /// <summary>
        /// 如果 Ghost Archetype 具有 <see cref="GhostOwner"/>，则获取拥有该 Ghost 的玩家 NetworkId
        /// </summary>
        /// <param name="ghost">正在生成的 Ghost</param>
        /// <param name="data">Snapshot 数据 Buffer</param>
        /// <returns>存在 <see cref="GhostOwner"/> 时返回拥有该 Ghost 的玩家 NetworkId，否则返回 0</returns>
        public int GetGhostOwner(in GhostSpawnBuffer ghost, in DynamicBuffer<SnapshotDataBuffer> data)
        {
            ref readonly var ghostPrefabSerializer = ref m_ghostPrefabType.ElementAtRO(ghost.GhostType);
            if (ghostPrefabSerializer.PredictionOwnerOffset != 0)
            {
                unsafe
                {
                    var dataPtr = (byte*)data.GetUnsafeReadOnlyPtr() + ghost.DataOffset;
                    return *(int*)(dataPtr + ghostPrefabSerializer.PredictionOwnerOffset);
                }
            }
            return 0;
        }

        /// <summary>
        /// 获取正在生成的 Ghost 尚未完成分类时使用的后备预测模式
        /// </summary>
        /// <param name="ghost">正在生成的 Ghost</param>
        /// <returns>要使用的后备模式</returns>
        public GhostSpawnBuffer.Type GetFallbackPredictionMode(in GhostSpawnBuffer ghost)
        {
            return m_ghostPrefabType.ElementAtRO(ghost.GhostType).FallbackPredictionMode;
        }

        /// <summary>
        /// 检查正在生成的 Ghost 中是否存在 <typeparamref name="T"/> 类型的 Component
        /// </summary>
        /// <param name="ghostTypeIndex">在 <see cref="GhostCollectionPrefabSerializer"/> 集合中的索引</param>
        /// <typeparam name="T">正在生成的 Ghost 中的 Component 类型</typeparam>
        /// <returns>正在生成的 Ghost 中是否存在该 Component</returns>
        /// <remarks>
        /// 对 IComponentData 和 IBufferElementData 均适用
        /// </remarks>
        public bool HasComponent<T>(int ghostTypeIndex) where T: unmanaged, IComponentData
        {
            return GetComponentDataOffset(TypeManager.GetTypeIndex<T>(), ghostTypeIndex, out _) >= 0;
        }

        /// <summary>
        /// 检查正在生成的 Ghost 中是否存在 <typeparamref name="T"/> 类型的 Buffer
        /// </summary>
        /// <param name="ghostTypeIndex">在 <see cref="GhostCollectionPrefabSerializer"/> 集合中的索引</param>
        /// <typeparam name="T">Buffer 元素类型</typeparam>
        /// <returns>正在生成的 Ghost 中是否存在该类型</returns>
        /// <remarks>
        /// 对 IComponentData 和 IBufferElementData 均适用
        /// </remarks>
        public bool HasBuffer<T>(int ghostTypeIndex) where T: unmanaged, IBufferElementData
        {
            return GetComponentDataOffset(TypeManager.GetTypeIndex<T>(), ghostTypeIndex, out _) >= 0;
        }

        /// <summary>
        /// 尝试从 Snapshot 历史 Buffer 获取 <typeparamref name="T"/> 类型的 Component 数据
        /// </summary>
        /// <remarks>
        /// 不支持 Buffer，且只能获取根 Entity 上的 Component，不支持获取子 Entity 上的 Component 数据
        /// </remarks>
        /// <param name="ghostTypeIndex">在 <see cref="GhostCollectionPrefabSerializer"/> 集合中的索引</param>
        /// <param name="snapshotBuffer">Entity 的 Snapshot 历史 Buffer</param>
        /// <param name="componentData">反序列化后的 Component 数据</param>
        /// <param name="slotIndex">要使用的历史 Buffer 槽位</param>
        /// <typeparam name="T">Component 类型</typeparam>
        /// <returns>存在该 Component 且其数据已初始化时返回 true，否则返回 false</returns>
        public bool TryGetComponentDataFromSnapshotHistory<T>(int ghostTypeIndex, in DynamicBuffer<SnapshotDataBuffer> snapshotBuffer,
            out T componentData, int slotIndex=0) where T : unmanaged, IComponentData
        {
            componentData = default;
            var offset = GetComponentDataOffset(TypeManager.GetTypeIndex<T>(), ghostTypeIndex, out var serializerIndex);
            if (offset < 0)
                return false;

            var snapshotSize = m_ghostPrefabType.ElementAtRO(ghostTypeIndex).SnapshotSize;
            var dataOffset = snapshotSize * slotIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (snapshotSize > 0 && dataOffset > (snapshotBuffer.Length - snapshotSize) )
                throw new System.IndexOutOfRangeException($"Cannot read component data from the snapshot buffer at index {slotIndex}. The snapshot buffer has {snapshotBuffer.Length/snapshotSize} slots.");
#endif
            CopyDataFromSnapshot(snapshotBuffer, dataOffset + offset, serializerIndex, ref componentData);
            return true;
        }

        /// <summary>
        /// 尝试从生成 Buffer 获取 <typeparamref name="T"/> 类型的 Component 数据
        /// </summary>
        /// <remarks>
        /// 不支持 Buffer，且只能获取根 Entity 上的 Component，不支持获取子 Entity 上的 Component 数据
        /// </remarks>
        /// <param name="ghost">生成 Buffer 中的 Ghost 条目</param>
        /// <param name="snapshotData">Snapshot 数据</param>
        /// <param name="componentData">Component 数据</param>
        /// <typeparam name="T">Component 类型</typeparam>
        /// <returns>存在该 Component 且其数据已初始化时返回 true，否则返回 false</returns>
        public bool TryGetComponentDataFromSpawnBuffer<T>(in GhostSpawnBuffer ghost,
            in DynamicBuffer<SnapshotDataBuffer> snapshotData, out T componentData) where T: unmanaged, IComponentData
        {
            componentData = default;
            var offset = GetComponentDataOffset(TypeManager.GetTypeIndex<T>(), ghost.GhostType, out var serializerIndex);
            if (offset < 0)
                return false;
            CopyDataFromSnapshot(snapshotData, ghost.DataOffset + offset, serializerIndex, ref componentData);
            return true;
        }

        private unsafe void CopyDataFromSnapshot<T>(DynamicBuffer<SnapshotDataBuffer> historyBuffer, int dataOffset,
            int serializerIndex , ref T componentData) where T : unmanaged, IComponentData
        {
            // 从这里开始，获取数据需要使用该 Component 类型与 Ghost 对应的 Serializer
            ref readonly var serializer = ref m_ghostSerializers.ElementAtRO(serializerIndex);
            // 无论客户端过滤条件如何都强制复制该类型
            // 最坏情况下 Component 保持预期的默认数据
            var deserializerState = new GhostDeserializerState
            {
                GhostMap = m_ghostMap,
                SendToOwner = SendToOwnerType.All
            };
            // TODO: 后续可以提供职责更窄、专门用于此场景的函数版本
            var compDataPtr = (byte*)historyBuffer.GetUnsafeReadOnlyPtr() + dataOffset;
            var dataAtTick = new SnapshotData.DataAtTick
            {
                SnapshotBefore = (System.IntPtr)compDataPtr,
                SnapshotAfter = (System.IntPtr)compDataPtr,
                RequiredOwnerSendMask = SendToOwnerType.All
            };
            m_ghostSerializers[serializerIndex].CopyFromSnapshot.Invoke(
                (System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                (System.IntPtr)UnsafeUtility.AddressOf(ref dataAtTick),
                0,
                0,
                (System.IntPtr)UnsafeUtility.AddressOf(ref componentData), serializer.ComponentSize,
                1);
        }

        // GetComponentDataOffset 会缓存用户要检查的 Component 偏移及其 Serializer
        // 缓存这些信息有两种时机：
        // - 处理 Prefab 时，前提是能预先知道哪些 Component 类型需要检查，例如通过特性或集合注册
        // - 提供一个小型的 Ghost 类型与 Component 类型键值缓存，按需缓存此函数的结果
        // 若要在处理 Prefab 时预缓存，需要提供注册 API 或代码生成特性来声明可检查的 Component
        // 为保持简单，这里只在实际需要时按需缓存
        // 不将缓存放入 GhostCollectionPrefabType，是因为用户通常只需检查 Ghost Buffer 来解析和分类预测生成 Ghost
        // 即使有 1000 个 Prefab，真正能预测生成的通常也很少，因此该缓存一般规模较小
        // 大多数 Prefab 都不需要对应条目
        // 同样，需要检查的 Component 类型通常也不多，整个项目可能只用一两个自定义 Component 唯一标识一次生成
        // 目前尚无数据能确定其上限，因此保留更灵活的按需缓存方案
        private int GetComponentDataOffset(int typeIndex, int ghostType, out int serializerIndex)
        {
            if (!m_componentOffsetCacheRW.IsCreated)
                return FindSerializerIndexAndComponentDataOffset(typeIndex, ghostType, out serializerIndex);

            var key = new SnapshotLookupCacheKey(typeIndex, ghostType);
            if (!m_componentOffsetCacheRW.TryGetValue(key, out var cachedOffset))
            {
                cachedOffset.dataOffset = FindSerializerIndexAndComponentDataOffset(typeIndex, ghostType, out cachedOffset.serializerIndex);
                m_componentOffsetCacheRW.Add(key, cachedOffset);
            }
            serializerIndex = cachedOffset.serializerIndex;
            return cachedOffset.dataOffset;
        }

        // 计算出的偏移也包含由 Ghost 类型决定的 Snapshot Header
        private int FindSerializerIndexAndComponentDataOffset(int typeIndex, int ghostType, out int compSerializerIndex)
        {
            var prefabType = m_ghostPrefabType.ElementAtRO(ghostType);
            var offset = GhostComponentSerializer.SnapshotHeaderSizeInBytes(prefabType);
            for (var i = 0; i < prefabType.NumComponents; ++i)
            {
                ref readonly var compIndices = ref m_ghostComponentIndices.ElementAtRO(prefabType.FirstComponent + i); ;
                var comType = m_ghostComponentTypes.ElementAtRO(compIndices.ComponentIndex).Type;
                if (comType.TypeIndex == typeIndex)
                {
                    compSerializerIndex = compIndices.SerializerIndex;
                    return offset;
                }

                if (compIndices.SnapshotSize != 0)
                {
                    var compSize =  comType.IsBuffer
                        ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                        : compIndices.SnapshotSize;
                    offset += GhostComponentSerializer.SnapshotSizeAligned(compSize);
                }
            }
            // 未找到对应 Component
            compSerializerIndex = default;
            return -1;
        }
    }

    internal struct SnapshotLookupCacheKey : System.IEquatable<SnapshotLookupCacheKey>
    {
        public int ghostType;
        public int typeIndex;

        public SnapshotLookupCacheKey(int ghostType, int typeIndex)
        {
            this.ghostType = ghostType;
            this.typeIndex = typeIndex;
        }

        public bool Equals(SnapshotLookupCacheKey other)
        {
            return ghostType == other.ghostType && typeIndex == other.typeIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is SnapshotLookupCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)math.hash(new int2(ghostType, typeIndex));
        }
    }

    /// <summary>
    /// 向 GhostCollection Singleton 添加供 <see cref="SnapshotDataBufferComponentLookup"/> 使用的
    /// <see cref="SnapshotDataLookupCache"/> 组件
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [CreateAfter(typeof(GhostCollectionSystem))]
    [CreateBefore(typeof(GhostReceiveSystem))]
    internal partial struct SnapshotLookupCacheSystem : ISystem
    {
        /// <summary>
        /// 将 Component 与 Ghost 类型组合映射到 Snapshot 内的数据偏移
        /// </summary>
        private NativeHashMap<SnapshotLookupCacheKey, SnapshotDataLookupCache.SerializerIndexAndOffset> m_SnapshotDataLookupCache;

        public void OnCreate(ref SystemState state)
        {
            m_SnapshotDataLookupCache = new NativeHashMap<SnapshotLookupCacheKey, SnapshotDataLookupCache.SerializerIndexAndOffset>(128, Allocator.Persistent);
            var collection = SystemAPI.GetSingletonEntity<GhostCollection>();
            state.EntityManager.SetComponentData(collection, new SnapshotDataLookupCache
            {
                ComponentDataOffsets = m_SnapshotDataLookupCache
            });
            state.Enabled = false;
        }
        public void OnDestroy(ref SystemState state)
        {
            m_SnapshotDataLookupCache.Dispose();
        }
    }

    /// <summary>
    /// 添加到 <see cref="GhostCollection"/> Singleton Entity 的内部 Component
    /// 用于缓存不同 Ghost 类型中被检查 Component 在 Snapshot Buffer 内的偏移
    /// </summary>
    internal struct SnapshotDataLookupCache : IComponentData
    {
        public struct SerializerIndexAndOffset
        {
            public int serializerIndex;
            public int dataOffset;
        }
        internal NativeHashMap<SnapshotLookupCacheKey, SerializerIndexAndOffset> ComponentDataOffsets;
    }
}
