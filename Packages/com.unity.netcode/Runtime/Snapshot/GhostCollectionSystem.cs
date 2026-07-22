#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif

using System;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode.LowLevel;
using Unity.Profiling;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode
{
    /// <summary>
    /// 通过代码创建的 Ghost Prefab 列表
    /// </summary>
    [InternalBufferCapacity(0)]
    struct CodeGhostPrefab : IBufferElementData
    {
        public Entity entity;
        public BlobAssetReference<GhostPrefabBlobMetaData> blob;
    }

    /// <summary>
    /// <para>
    /// 负责构建和管理 <see cref="GhostCollection"/> Singleton Data 的系统
    /// </para>
    /// <para>
    /// 系统通过以下步骤处理 World 中的全部 Ghost Prefab</para>
    /// <para>- 根据 <see cref="GhostPrefabType"/> 从 Entity Prefab 剥离并移除 Component</para>
    /// <para>- 填充 <see cref="GhostCollectionPrefab"/></para>
    /// <para>- 为 Ghost 序列化准备并构建全部必要数据结构，包括 <see cref="GhostCollectionPrefabSerializer"/>、
    /// <see cref="GhostCollectionComponentIndex"/> 和 <see cref="GhostCollectionComponentType"/></para>
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [CreateAfter(typeof(DefaultVariantSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct GhostCollectionSystem : ISystem
    {
        struct ComponentHashComparer : IComparer<GhostComponentSerializer.State>
        {
            public int Compare(GhostComponentSerializer.State x, GhostComponentSerializer.State y)
            {
                var hashX = TypeManager.GetTypeInfo(x.ComponentType.TypeIndex).StableTypeHash;
                var hashY = TypeManager.GetTypeInfo(y.ComponentType.TypeIndex).StableTypeHash;

                if (hashX < hashY)
                    return -1;
                if (hashX > hashY)
                    return 1;
                // 相同 Component 按 Variant Hash 排序
                if (x.VariantHash < y.VariantHash)
                    return -1;
                if (x.VariantHash > y.VariantHash)
                    return 1;
                else
                    return 0;
            }
        }
        private byte m_ComponentCollectionInitialized;
        private Entity m_CollectionSingleton;
#if UNITY_EDITOR || NETCODE_DEBUG
        private NativeList<PredictionErrorNames> m_PredictionErrorNames;
        private NativeList<FixedString64Bytes> m_GhostNames;

        // 解析 GhostComponentSerializer.State.PredictionErrorName 列表，缓存全部 Component 预测错误名称
        private UnsafeList<(short, short)> m_PredictionErrorNamesStartEndCache;
        private NativeList<PendingNameAssignment> m_PendingNameAssignments;
        private int m_currentPredictionErrorNamesCount;
        private int m_currentPredictionErrorCount;

        private int m_PrevPredictionErrorNamesCount;
        private int m_PrevGhostNamesCount;

        private bool m_HasGhostNamesSingleton;
        private bool m_HasPredictionErrorNamesSingleton;
#endif

        private EntityQuery m_InGameQuery;
        private EntityQuery m_AllConnectionsQuery;
        private EntityQuery m_RuntimeStripQuery;
        private EntityQuery m_DestroyedGhostPrefabQuery;
        private EntityQuery m_NewPrefabGhostQuery;
        private EntityQuery m_RegisteredGhostTypesQuery;

        private struct UsedComponentType
        {
            public int UsedIndex;
            public GhostCollectionComponentType ComponentType;
        }
        private NativeList<UsedComponentType> m_AllComponentTypes;
        /// <summary>
        /// 根据 Component 的 Stable Hash 获取其在 GhostCollectionComponentIndex 中的索引
        /// </summary>
        private NativeHashMap<ulong, int> m_StableHashToComponentTypeIndex;
        private NativeHashMap<GhostType, int> m_GhostTypeToGhostCollectionPrefab;
        private NativeHashMap<GhostType, int> m_PendingAssignment;
        private NativeParallelMultiHashMap<GhostType, Entity> m_GhostPrefabForGhostType;
        private Entity m_CodePrefabSingleton;
        private NativeHashMap<Hash128, GhostPrefabCustomSerializer> m_CustomSerializers;

        private ProfilerMarker m_CreateComponentCollection;
        private ProfilerMarker m_StrippingMarker;
        private ProfilerMarker m_TrackingMarker;
        private ProfilerMarker m_MappingMarker;
        private ProfilerMarker m_Processing;
        private ProfilerMarker m_UpdateNameMarker;

        // Hash 要求
        // R0：Component 不同或顺序不同时，Hash 必须变化
        // R1：大小、Owner Offset、Mask Bit、Partial Component 等不同时，必须得到不同 Hash
        // R2：Ghost 具有相同 Component 和字段，但 [GhostField] 属性不同，例如 SubType、Interpolated、Composite 时，
        //     即使最终序列化大小和 Mask 相同，也必须得到不同 Hash
        internal static ulong CalculateComponentCollectionHash(DynamicBuffer<GhostComponentSerializer.State> ghostComponentCollection)
        {
            ulong componentCollectionHash = 0;
            for (int i = 0; i < ghostComponentCollection.Length; ++i)
            {
                var comp = ghostComponentCollection[i];
                if(comp.SerializerHash !=0)
                {
                    componentCollectionHash = TypeHash.CombineFNV1A64(componentCollectionHash, comp.SerializerHash);
                }
            }
            return componentCollectionHash;
        }

        internal static void GetSerializerHashString(in GhostComponentSerializer.State comp, ref FixedString128Bytes hashString)
        {
            FixedString32Bytes title = "Type: ";
            hashString.Append(title);
            hashString.Append(TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).DebugTypeName);
            // GhostFieldsHash 对 Component 中每个 GhostField 的 Composite、Smoothing、SubType 和 Quantization 参数计算 Hash
            // 此 Hash 在构建时由生成代码确定
            title = " GhostFieldHash: ";
            hashString.Append(title);
            hashString.Append(comp.GhostFieldsHash);
            title = " SnapshotSize: ";
            hashString.Append(title);
            hashString.Append(comp.SnapshotSize);
            title = " ChangeMaskBits: ";
            hashString.Append(title);
            hashString.Append(NetDebug.PrintMask((uint)comp.SnapshotSize));
            title = " SendToOwner: ";
            hashString.Append(title);
            hashString.Append((int)comp.SendToOwner);
        }

        private ulong HashGhostType(in GhostCollectionPrefabSerializer ghostType, in NetDebug netDebug, in FixedString64Bytes ghostName, in FixedString64Bytes entityPrefabName, in Entity prefab)
        {
            ulong ghostTypeHash = ghostType.TypeHash;

            if (ghostTypeHash == 0)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException($"Unexpected 0 ghostType.TypeHash for ghost '{ghostName}' (entityPrefabName: '{entityPrefabName}', {prefab.ToFixedString()}).");
#else
                netDebug.LogError($"Unexpected 0 ghostType.TypeHash for ghost '{ghostName}' (entityPrefabName: '{entityPrefabName}', {prefab.ToFixedString()}).");
                return 0;
#endif
            }

            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.FirstComponent));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.NumComponents));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.NumChildComponents));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.SnapshotSize));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.ChangeMaskBits));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.PredictionOwnerOffset));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.OwnerPredicted));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.IsGhostGroup));
            ghostTypeHash = TypeHash.CombineFNV1A64(ghostTypeHash, TypeHash.FNV1A64(ghostType.EnableableBits));
            return ghostTypeHash;
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostCollection>();
            // TODO：移除全部不必要的 Buffer，消除此数据中的重复项
            m_CollectionSingleton = state.EntityManager.CreateSingleton<GhostCollection>("Ghost Collection");
            state.EntityManager.AddBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton);
            state.EntityManager.AddBuffer<GhostCollectionComponentIndex>(m_CollectionSingleton);
            state.EntityManager.AddBuffer<GhostCollectionPrefab>(m_CollectionSingleton);
            state.EntityManager.AddBuffer<GhostComponentSerializer.State>(m_CollectionSingleton);
            state.EntityManager.AddBuffer<GhostCollectionComponentType>(m_CollectionSingleton);
            state.EntityManager.AddComponent<SnapshotDataLookupCache>(m_CollectionSingleton);
            state.EntityManager.AddComponent<GhostCollectionCustomSerializers>(m_CollectionSingleton);

#if UNITY_EDITOR || NETCODE_DEBUG
            m_PredictionErrorNames = new NativeList<PredictionErrorNames>(16, Allocator.Persistent);
            m_GhostNames = new NativeList<FixedString64Bytes>(16, Allocator.Persistent);
            m_PendingNameAssignments = new NativeList<PendingNameAssignment>(256, Allocator.Persistent);
            m_PredictionErrorNamesStartEndCache = new UnsafeList<(short, short)>(256, Allocator.Persistent);
#endif
            m_CustomSerializers = new NativeHashMap<Hash128, GhostPrefabCustomSerializer>(256, Allocator.Persistent);
            m_GhostTypeToGhostCollectionPrefab = new NativeHashMap<GhostType, int>(256, Allocator.Persistent);
            m_GhostPrefabForGhostType = new NativeParallelMultiHashMap<GhostType, Entity>(256, Allocator.Persistent);
            m_PendingAssignment = new NativeHashMap<GhostType, int>(256, Allocator.Persistent);
            state.EntityManager.SetComponentData(m_CollectionSingleton, new GhostCollection
            {
                PendingGhostPrefabAssignment = m_PendingAssignment,
                GhostTypeToColletionIndex = m_GhostTypeToGhostCollectionPrefab.AsReadOnly()
            });
            state.EntityManager.SetComponentData(m_CollectionSingleton, new GhostCollectionCustomSerializers
            {
                Serializers = m_CustomSerializers
            });
            using var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp).WithAll<GhostPrefabMetaData, Prefab, GhostPrefabRuntimeStrip>();
            m_RuntimeStripQuery = state.GetEntityQuery(entityQueryBuilder);
            entityQueryBuilder.Reset();
            entityQueryBuilder.WithAll<NetworkStreamInGame>();
            m_InGameQuery = state.GetEntityQuery(entityQueryBuilder);
            entityQueryBuilder.Reset();
            entityQueryBuilder.WithAll<NetworkId>();
            m_AllConnectionsQuery = state.GetEntityQuery(entityQueryBuilder);
            entityQueryBuilder.Reset();
            entityQueryBuilder.WithPresent<Prefab, GhostType>()
                .WithAbsent<GhostPrefabRuntimeStrip>()
                .WithAbsent<GhostPrefabTracking>()
                .WithOptions(EntityQueryOptions.IncludePrefab);
            m_NewPrefabGhostQuery = state.GetEntityQuery(entityQueryBuilder);
            entityQueryBuilder.Reset();
            entityQueryBuilder.WithPresent<GhostPrefabTracking>().WithOptions(EntityQueryOptions.IncludePrefab);
            m_RegisteredGhostTypesQuery = state.GetEntityQuery(entityQueryBuilder);
            entityQueryBuilder.Reset();
            entityQueryBuilder.WithPresent<GhostPrefabTracking>().WithNone<GhostType>();
            m_DestroyedGhostPrefabQuery = state.GetEntityQuery(entityQueryBuilder);

            m_CreateComponentCollection = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_CreateComponentCollection");
            m_StrippingMarker = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_Stripping");
            m_TrackingMarker = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_Tracking");
            m_MappingMarker = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_Mapping");
            m_Processing = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_Processing");
            m_UpdateNameMarker = new ProfilerMarker($"{state.WorldUnmanaged.Name}-GhostCollectionSystem_UpdateNames");

            if (!SystemAPI.TryGetSingletonEntity<CodeGhostPrefab>(out m_CodePrefabSingleton))
                m_CodePrefabSingleton = state.EntityManager.CreateSingletonBuffer<CodeGhostPrefab>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            var codePrefabs = state.EntityManager.GetBuffer<CodeGhostPrefab>(m_CodePrefabSingleton);
            for (var i = 0; i < codePrefabs.Length; ++i)
            {
                codePrefabs[i].blob.Dispose();
            }
            state.EntityManager.DestroyEntity(m_CodePrefabSingleton);
            if (m_AllComponentTypes.IsCreated)
                m_AllComponentTypes.Dispose();
            if (m_StableHashToComponentTypeIndex.IsCreated)
                m_StableHashToComponentTypeIndex.Dispose();
            state.EntityManager.DestroyEntity(m_CollectionSingleton);
#if UNITY_EDITOR || NETCODE_DEBUG
            m_PredictionErrorNames.Dispose();
            m_GhostNames.Dispose();
            m_PendingNameAssignments.Dispose();
            m_PredictionErrorNamesStartEndCache.Dispose();
#endif
            m_CustomSerializers.Dispose();
            m_GhostTypeToGhostCollectionPrefab.Dispose();
            m_GhostPrefabForGhostType.Dispose();
            m_PendingAssignment.Dispose();
        }

        struct AddComponentCtx
        {
            public DynamicBuffer<GhostComponentSerializer.State> ghostSerializerCollection;
            public DynamicBuffer<GhostCollectionPrefabSerializer> ghostPrefabSerializerCollection;
            public DynamicBuffer<GhostCollectionComponentType> ghostComponentCollection;
            public DynamicBuffer<GhostCollectionComponentIndex> ghostComponentIndex;
            public DynamicBuffer<GhostCollectionPrefab> ghostPrefabCollection;
            public GhostCollectionCustomSerializers customSerializers;
            public int ghostChildIndex;
            public int childOffset;
            public GhostType ghostType;
            public FixedString64Bytes ghostName;
            public NetDebug netDebug;
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            if (m_ComponentCollectionInitialized == 0)
            {
                CreateComponentCollection(ref state, in netDebug);
            }

            if (!m_RuntimeStripQuery.IsEmptyIgnoreFilter)
            {
                m_RuntimeStripQuery.CompleteDependency();
                using var _ = m_StrippingMarker.Auto();
                RuntimeStripPrefabs(ref state, in netDebug);
            }

            if (m_InGameQuery.IsEmptyIgnoreFilter)
            {
                // 之前已进入游戏但现在不再处于游戏中，清除全部数据，使场景切换等情况下能从干净状态重建 Ghost Collection
                if (SystemAPI.GetSingletonRW<GhostCollection>().ValueRO.IsInGame)
                {
                    state.EntityManager.GetBuffer<GhostCollectionPrefab>(m_CollectionSingleton).Clear();
                    state.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(m_CollectionSingleton).Clear();
                    state.EntityManager.GetBuffer<GhostCollectionComponentIndex>(m_CollectionSingleton).Clear();
                    state.EntityManager.GetBuffer<GhostCollectionComponentType>(m_CollectionSingleton).Clear();
                    state.EntityManager.RemoveComponent<GhostPrefabTracking>(m_RegisteredGhostTypesQuery);
                    m_PendingAssignment.Clear();
                    for (int i = 0; i < m_AllComponentTypes.Length; ++i)
                    {
                        var ctype = m_AllComponentTypes[i];
                        ctype.UsedIndex = -1;
                        m_AllComponentTypes[i] = ctype;
                    }
                    m_GhostTypeToGhostCollectionPrefab.Clear();
                    m_GhostPrefabForGhostType.Clear();
#if UNITY_EDITOR || NETCODE_DEBUG
                    m_PendingNameAssignments.Clear();
                    m_PredictionErrorNames.Clear();
                    m_GhostNames.Clear();
                    m_currentPredictionErrorNamesCount = 0;
                    m_currentPredictionErrorCount = 0;
                    if (m_PrevPredictionErrorNamesCount > 0 || m_PrevGhostNamesCount > 0)
                    {

                        SystemAPI.GetSingletonRW<GhostStatsCollectionData>().ValueRW.SetGhostNames(state.WorldUnmanaged.Name,
                            m_GhostNames, m_PredictionErrorNames, 0, ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW);
                        if (SystemAPI.TryGetSingletonBuffer<GhostNames>(out var ghosts))
                            UpdateGhostNames(ghosts, m_GhostNames);
                        if (SystemAPI.TryGetSingletonBuffer<PredictionErrorNames>(out var predictionErrors))
                            UpdatePredictionErrorNames(predictionErrors, m_PredictionErrorNames);
                        m_PrevPredictionErrorNamesCount = 0;
                        m_PrevGhostNamesCount = 0;
                    }
#endif
                    var ghostCollection = SystemAPI.GetSingletonRW<GhostCollection>();
                    ghostCollection.ValueRW.IsInGame = false;
                    ghostCollection.ValueRW.NumLoadedPrefabs = 0;
#if UNITY_EDITOR || NETCODE_DEBUG
                    ghostCollection.ValueRW.NumPredictionErrors = 0;
#endif
                }
                return;
            }

            // TODO：这里只需使用 Run，因为 Prefab 处理目前尚不能在 Job 中运行
            var ghostCollectionFromEntity = SystemAPI.GetBufferLookup<GhostCollectionPrefab>();
            var collectionSingleton = m_CollectionSingleton;
            // Prefab 被卸载或销毁时，重置 GhostCollectionPrefab 中的关联，
            // 并在存在候选项时尝试为同一 Ghost 类型查找另一个 Prefab
            // 为此需要判断是否存在同类型的其他 Prefab，可使用 Query 并设置 SharedComponentFilter
            if(!m_DestroyedGhostPrefabQuery.IsEmpty)
            {
                using var _ = m_TrackingMarker.Auto();
                var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                using var ghostPrefabEntities = m_DestroyedGhostPrefabQuery.ToEntityArray(Allocator.Temp);
                using var trackedPrefabs = m_DestroyedGhostPrefabQuery.ToComponentDataArray<GhostPrefabTracking>(Allocator.Temp);
                var pendingGhostPrefabAssignment = SystemAPI.GetSingletonRW<GhostCollection>().ValueRW.PendingGhostPrefabAssignment;
                for(int i=0;i<trackedPrefabs.Length;++i)
                {
                    var tracking = trackedPrefabs[i];
                    RemoveGhostPrefabFromTracking(tracking, ghostPrefabEntities[i]);
                    if (tracking.GhostType != default)
                    {
                        // 需要重新映射到同类型的其他 Ghost，待优化查找效率
                        if (m_GhostPrefabForGhostType.TryGetFirstValue(tracking.GhostType, out var newPrefabEntity, out var _))
                        {
                            ghostCollectionList.ElementAt(tracking.GhostCollectionPrefabIndex).GhostPrefab = newPrefabEntity;
                        }
                        else
                        {
                            ghostCollectionList.ElementAt(tracking.GhostCollectionPrefabIndex).GhostPrefab = Entity.Null;
                            if(state.WorldUnmanaged.IsClient())
                            {
                                pendingGhostPrefabAssignment.TryAdd(tracking.GhostType, tracking.GhostCollectionPrefabIndex);
                                pendingGhostPrefabAssignment[default] = 1;
                            }
                        }
                    }
                }
                state.EntityManager.RemoveComponent<GhostPrefabTracking>(m_DestroyedGhostPrefabQuery);
            }

            if (state.WorldUnmanaged.IsServer() && !m_NewPrefabGhostQuery.IsEmptyIgnoreFilter)
            {
                using var _ = m_MappingMarker.Auto();
                using var ghostPrefabEntities = m_NewPrefabGhostQuery.ToEntityArray(Allocator.Temp);
                using var ghostTypes = m_NewPrefabGhostQuery.ToComponentDataArray<GhostType>(Allocator.Temp);
                state.EntityManager.AddComponent<GhostPrefabTracking>(m_NewPrefabGhostQuery);
#if NETCODE_DEBUG
                var ghostTypeFromEntity = SystemAPI.GetComponentLookup<GhostType>(true);
                var codePrefabs = state.EntityManager.GetBuffer<CodeGhostPrefab>(m_CodePrefabSingleton);
#endif
                ghostCollectionFromEntity.Update(ref state);
                var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                // 服务器将尚未存在于 Ghost Collection 的全部 Ghost Prefab 加入集合
                for (int i = 0; i < ghostPrefabEntities.Length; i++)
                {
                    var ent = ghostPrefabEntities[i];
                    var ghostType = ghostTypes[i];
#if NETCODE_DEBUG
                    ValidatePrefabGUID(ent, ghostType, codePrefabs, ghostTypeFromEntity);
#endif
                    m_GhostPrefabForGhostType.Add(ghostType, ghostPrefabEntities[i]);
                    if (!m_GhostTypeToGhostCollectionPrefab.TryGetValue(ghostType, out var index))
                    {
                        var prefabIndex = ghostCollectionList.Length;
                        ghostCollectionList.Add(new GhostCollectionPrefab {GhostType = ghostType, GhostPrefab = ent});
                        // 将条目加入映射以便快速查找，从而在某个实例被卸载或销毁时更新 Prefab
                        m_GhostTypeToGhostCollectionPrefab.Add(ghostType, prefabIndex);
                        state.EntityManager.SetComponentData(ghostPrefabEntities[i], new GhostPrefabTracking
                        {
                            GhostCollectionPrefabIndex = prefabIndex,
                            GhostType = ghostType
                        });
                    }
                    // 存在此路径的原因是 SubScene 可能加载同一 Ghost Archetype 的多个 Prefab，例如使用相同 Spawner
                    // 1.0 新逻辑使这种情况减少，但仍然可能发生
                    // 由于技术上可以按需加载或卸载 SubScene，服务器上部分成立，
                    // 当列表中的 Prefab 条目失效后，可以立即重新映射该类型的另一个 Prefab
                    else if (ghostCollectionList[index].GhostPrefab == Entity.Null)
                    {
                        ghostCollectionList.ElementAt(index).GhostPrefab = ghostPrefabEntities[i];
                        state.EntityManager.SetComponentData(ghostPrefabEntities[i], new GhostPrefabTracking
                        {
                            GhostCollectionPrefabIndex = index,
                            GhostType = ghostType
                        });
                    }
                }
            }
            else if(state.WorldUnmanaged.IsClient())
            {
                using var _ = m_MappingMarker.Auto();
                // 客户端逻辑略有不同，它会从服务器收到应加载的 Prefab 列表
                // 这些 Prefab 按服务器发送顺序加入 GhostCollectionPrefab Buffer，服务器期望客户端逐步 Ack
                // 此时 m_GhostTypeToPrefabIndex 用于跟踪是否存在待处理分配，以及对应哪个 Ghost
                var pendingAssigment = SystemAPI.GetSingletonRW<GhostCollection>().ValueRW.PendingGhostPrefabAssignment;
                if (!m_NewPrefabGhostQuery.IsEmptyIgnoreFilter)
                {
                    using var ghostPrefabEntities = m_NewPrefabGhostQuery.ToEntityArray(Allocator.Temp);
                    using var ghostTypes = m_NewPrefabGhostQuery.ToComponentDataArray<GhostType>(Allocator.Temp);
                    state.EntityManager.AddComponent<GhostPrefabTracking>(m_NewPrefabGhostQuery);
#if NETCODE_DEBUG
                    var ghostTypeFromEntity = SystemAPI.GetComponentLookup<GhostType>(true);
                    var codePrefabs = state.EntityManager.GetBuffer<CodeGhostPrefab>(m_CodePrefabSingleton);
#endif
                    ghostCollectionFromEntity.Update(ref state);
                    var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                    // 将已加载 Prefab 映射到对应的待处理条目
                    for (int i = 0; i < ghostPrefabEntities.Length; i++)
                    {
                        var ent = ghostPrefabEntities[i];
                        var ghostType = ghostTypes[i];
#if NETCODE_DEBUG
                        ValidatePrefabGUID(ent, ghostType, codePrefabs, ghostTypeFromEntity);
#endif
                        // 将 Prefab 加入指定 Ghost 类型的可用列表，必要时用于将 Ghost Prefab Entity 重新映射到另一候选项
                        m_GhostPrefabForGhostType.Add(ghostType, ghostPrefabEntities[i]);
                        // 如果此 Ghost 类型存在待分配 Prefab，则立即进行分配
                        if (pendingAssigment.TryGetValue(ghostType, out var index))
                        {
                            ghostCollectionList.ElementAt(index).GhostPrefab = ent;
                            m_GhostTypeToGhostCollectionPrefab[ghostType] = index;
                            state.EntityManager.SetComponentData(ent, new GhostPrefabTracking
                            {
                                GhostCollectionPrefabIndex = index,
                                GhostType = ghostType
                            });
                            // 移除待处理分配
                            pendingAssigment.Remove(ghostType);
                        }
                    }
                    pendingAssigment[default] = 0;
                }
                // 待处理列表不为空且自上次起发生变化时，
                // 尝试将所有待处理分配映射到该 Ghost 类型当前已注册的 Prefab
                else
                {
                    if (pendingAssigment.TryGetValue(default, out var pendingAssignmentFlag) && pendingAssignmentFlag != 0)
                    {
                        ghostCollectionFromEntity.Update(ref state);
                        var ghostCollectionList = ghostCollectionFromEntity[collectionSingleton];
                        var keyValueArrays = pendingAssigment.GetKeyValueArrays(Allocator.Temp);
                        for(int i=0;i<keyValueArrays.Length;++i)
                        {
                            if (m_GhostPrefabForGhostType.TryGetFirstValue(keyValueArrays.Keys[i], out var entity, out var _))
                            {
                                ghostCollectionList.ElementAt(keyValueArrays.Values[i]).GhostPrefab = entity;
                                m_GhostTypeToGhostCollectionPrefab[keyValueArrays.Keys[i]] = keyValueArrays.Values[i];
                                state.EntityManager.SetComponentData(entity, new GhostPrefabTracking
                                {
                                    GhostCollectionPrefabIndex = keyValueArrays.Values[i],
                                    GhostType = keyValueArrays.Keys[i]
                                });
                                pendingAssigment.Remove(keyValueArrays.Keys[i]);
                            }
                        }
                        pendingAssigment[default] = 0;
                    }
                }
            }

            SystemAPI.TryGetSingleton(out ClientServerTickRate tickRate);
            tickRate.ResolveDefaults();
            var ctx = new AddComponentCtx
            {
                ghostPrefabCollection = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionSingleton),
                ghostSerializerCollection = state.EntityManager.GetBuffer<GhostComponentSerializer.State>(collectionSingleton),
                ghostPrefabSerializerCollection = state.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collectionSingleton),
                ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionSingleton),
                ghostComponentIndex = state.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionSingleton),
                customSerializers = state.EntityManager.GetComponentData<GhostCollectionCustomSerializers>(collectionSingleton),
                netDebug = netDebug,
            };
            var data = SystemAPI.GetSingletonRW<GhostComponentSerializerCollectionData>().ValueRW;
            var ghostPrefabSerializerErrors = 0;
            // 处理新加载的 Prefab
            for (int i = ctx.ghostPrefabSerializerCollection.Length; i < ctx.ghostPrefabCollection.Length; ++i)
            {
                using var _ = m_Processing.Auto();
                var ghost = ctx.ghostPrefabCollection[i];
                // 加载集合中的每个 Ghost 并加入 m_GhostTypeCollection
                // Prefab 尚未加载时，不再处理后续 Ghost

                // 通过加载倒计时为客户端留出加载 Prefab 的时间
                if (ghost.GhostPrefab == Entity.Null && ghost.Loading == GhostCollectionPrefab.LoadingState.LoadingActive)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingNotActive;
                    ctx.ghostPrefabCollection[i] = ghost;
                    break;
                }
                ulong hash = 0;
                state.EntityManager.GetName(ghost.GhostPrefab, out var entityPrefabName);
                ctx.ghostType = ghost.GhostType;
                if (ghost.GhostPrefab != Entity.Null)
                {
                    // Prefab 已可配置，开始处理
                    ProcessGhostPrefab(ref state, ref data, ref ctx, ref tickRate, ghost.GhostPrefab);
                    // 确认已成功添加，Collection Check 可能导致失败
                    if (ctx.ghostPrefabSerializerCollection.Length > i)
                        hash = HashGhostType(ctx.ghostPrefabSerializerCollection[i], in netDebug, in ctx.ghostName, in entityPrefabName, in ghost.GhostPrefab);
                }

                if ((ghost.Hash != 0 && ghost.Hash != hash) || hash == 0)
                {
                    if (hash == 0)
                    {
                        FixedString512Bytes error = $"The ghost collection contains a ghost which does not have a valid prefab on the client! Ghost: '{ctx.ghostName}' ('{entityPrefabName}').";
#if UNITY_EDITOR || ENABLE_UNITY_COLLECTIONS_CHECKS
                        BurstDiscardAppendBetterExceptionMessage(ghost, ref error, ref state);
#endif
                        netDebug.LogError(error);
                    }
                    else
                    {
                        netDebug.LogError($"Received a ghost - {ctx.ghostName} - from the server which has a different hash on the client (got {ghost.Hash} but expected {hash}). GhostPrefab: {ghost.GhostPrefab} ('{entityPrefabName}').");
                    }
                    ++ghostPrefabSerializerErrors;
                    continue;
                }
                ghost.Hash = hash;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                // FIXME：GhostTypePartition 应始终有效，不能等于 0:0:0:0，并且通常应等于 GhostType
                if (state.EntityManager.HasComponent<GhostTypePartition>(ghost.GhostPrefab) &&
                    state.EntityManager.GetSharedComponent<GhostTypePartition>(ghost.GhostPrefab).SharedValue == default)
                {
                    netDebug.LogError($"ghost {ctx.ghostName} has an invalid GhostTypePartition value.");
                }
#endif
                ctx.ghostPrefabCollection[i] = ghost;
            }

            if (ghostPrefabSerializerErrors > 0)
            {
                netDebug.LogError("Disconnecting all the connections because of errors while processing the ghost prefabs (see previous reported errors).");
                // 此逻辑不能 Job 化，因为创建该 Query 正是为了避免对这些 Entity 产生依赖
                var connections = m_AllConnectionsQuery.ToEntityArray(Allocator.Temp);
                for (int con = 0; con < connections.Length; ++con)
                    state.EntityManager.AddComponentData(connections[con], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
#if UNITY_EDITOR || NETCODE_DEBUG
                // 重置全部待处理分配
                m_PrevPredictionErrorNamesCount = 0;
                m_PrevGhostNamesCount = 0;
                m_currentPredictionErrorNamesCount = 0;
                m_PendingNameAssignments.Clear();
#endif
                return;
            }
#if UNITY_EDITOR || NETCODE_DEBUG
            if (m_PrevPredictionErrorNamesCount < m_currentPredictionErrorNamesCount ||
                m_PrevGhostNamesCount < m_GhostNames.Length ||
                m_HasGhostNamesSingleton != SystemAPI.HasSingleton<GhostNames>() ||
                m_HasPredictionErrorNamesSingleton != SystemAPI.HasSingleton<PredictionErrorNames>())
            {
                using var _ = m_UpdateNameMarker.Auto();
                ProcessPendingNameAssignments(ctx.ghostSerializerCollection);
                SystemAPI.GetSingletonRW<GhostStatsCollectionData>().ValueRW.SetGhostNames(state.WorldUnmanaged.Name,
                    m_GhostNames, m_PredictionErrorNames, m_currentPredictionErrorCount, ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW);
                if (SystemAPI.TryGetSingletonBuffer<GhostNames>(out var ghosts))
                    UpdateGhostNames(ghosts, m_GhostNames);
                if (SystemAPI.TryGetSingletonBuffer<PredictionErrorNames>(out var predictionErrors))
                    UpdatePredictionErrorNames(predictionErrors, m_PredictionErrorNames);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Assertions.Assert.AreEqual(m_PredictionErrorNames.Length, m_currentPredictionErrorNamesCount);
#endif
                m_PrevPredictionErrorNamesCount = m_PredictionErrorNames.Length;
                m_PrevGhostNamesCount = m_GhostNames.Length;
            }

            m_HasGhostNamesSingleton = SystemAPI.HasSingleton<GhostNames>();
            m_HasPredictionErrorNamesSingleton = SystemAPI.HasSingleton<PredictionErrorNames>();
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
            ref var ghostCollectionRef = ref SystemAPI.GetSingletonRW<GhostCollection>().ValueRW;
            ghostCollectionRef.NumLoadedPrefabs = ctx.ghostPrefabSerializerCollection.Length;
            ghostCollectionRef.NumPredictionErrors = m_currentPredictionErrorCount;
            ghostCollectionRef.IsInGame = true;
#else
            ref var ghostCollectionRef = ref SystemAPI.GetSingletonRW<GhostCollection>().ValueRW;
            ghostCollectionRef.NumLoadedPrefabs = ctx.ghostPrefabSerializerCollection.Length;
            ghostCollectionRef.IsInGame = true;
#endif
        }

        private void RemoveGhostPrefabFromTracking(GhostPrefabTracking tracking, Entity trackedPrefab)
        {
            if (m_GhostPrefabForGhostType.TryGetFirstValue(tracking.GhostType, out var entity, out var iterator))
            {
                if (entity == trackedPrefab)
                {
                    m_GhostPrefabForGhostType.Remove(iterator);
                }
                else
                {
                    while(m_GhostPrefabForGhostType.TryGetNextValue(out entity, ref iterator))
                    {
                        if (entity == trackedPrefab)
                        {
                            m_GhostPrefabForGhostType.Remove(iterator);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 为便于调试，在同一进程内的 ServerWorld 中手动查找此无效 Hash 的辅助函数
        /// </summary>
        [BurstDiscard]
        private void BurstDiscardAppendBetterExceptionMessage(in GhostCollectionPrefab clientGhost,
            ref FixedString512Bytes error, ref SystemState state)
        {
#if UNITY_EDITOR || ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ClientServerBootstrap.ServerWorlds.Count == 0)
                return;

            foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
            {
                if(!serverWorld.IsCreated) continue;
                if (serverWorld != state.World)
                {
                    // 完成此 World 中全部已跟踪 Job 会使 Safety Handle 失效，
                    // 因此只对要查询的其他 World 执行该操作
                    serverWorld.EntityManager.CompleteAllTrackedJobs();
                }
                using var query = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollectionPrefab>());
                if (query.TryGetSingletonBuffer<GhostCollectionPrefab>(out var ghostCollectionPrefabs))
                {
                    var found = false;
                    foreach (var serverGhost in ghostCollectionPrefabs)
                    {
                        if (serverGhost.Hash == clientGhost.Hash)
                        {
                            serverWorld.EntityManager.GetName(serverGhost.GhostPrefab, out var serverEntityName);
                            var ghostPrefabMetadata = serverWorld.EntityManager.GetComponentData<GhostPrefabMetaData>(serverGhost.GhostPrefab);
                            ref var ghostMetaData = ref ghostPrefabMetadata.Value.Value;
                            FixedString128Bytes ghostName = default;
                            ghostMetaData.Name.CopyTo(ref ghostName);
                            error.Append($"\n Manually searching for this hash inside in-proc '{serverWorld.Unmanaged.Name}' and FOUND ghost {serverGhost.GhostPrefab.ToFixedString()} '{serverEntityName}' ('{ghostName}')!");
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        error.Append($"\n Manually searching for this hash inside in-proc '{serverWorld.Unmanaged.Name}', but NOT found!");
                    error.Append(" Ensure this Ghost is registered on the client before the server syncs its prefab list!");
                }
            }
#endif
        }

        // TODO：此处也可以使用 HashMap
        [Conditional("NETCODE_DEBUG")]
        private static void ValidatePrefabGUID(Entity ent, in GhostType ghostType, DynamicBuffer<CodeGhostPrefab> codePrefabs, ComponentLookup<GhostType> ghostTypeFromEntity)
        {
#if NETCODE_DEBUG
            // 检查是否与代码创建的 Prefab 冲突
            for (int codePrefabIdx = 0; codePrefabIdx < codePrefabs.Length; ++codePrefabIdx)
            {
                if (ent != codePrefabs[codePrefabIdx].entity && ghostTypeFromEntity[codePrefabs[codePrefabIdx].entity] == ghostType)
                {
                    var ghostNameFs = new FixedString64Bytes();
                    ref var blobString = ref codePrefabs[codePrefabIdx].blob.Value.Name;
                    blobString.CopyTo(ref ghostNameFs);
                    throw new InvalidOperationException($"Duplicate ghost prefab found at codePrefabIdx {codePrefabIdx} ('{ghostNameFs}'). All ghost prefabs must have a unique name (and thus GhostType hash).");
                }
            }
#endif
        }

        private void ProcessGhostPrefab(ref SystemState state, ref GhostComponentSerializerCollectionData data, ref AddComponentCtx ctx, ref ClientServerTickRate tickRate, Entity prefabEntity)
        {
            var ghostPrefabMetadata = state.EntityManager.GetComponentData<GhostPrefabMetaData>(prefabEntity);
            ref var ghostMetaData = ref ghostPrefabMetadata.Value.Value;
            ref var componentInfoLen = ref ghostMetaData.NumServerComponentsPerEntity;
            if (ghostMetaData.Name.Length == 0)
            {
                ctx.ghostName = $"unknown_ghost_prefab_{prefabEntity.Index}:{prefabEntity.Version}";
                ctx.netDebug.LogError($"Empty ghostMetaData.Name passed into ProcessGhostPrefab for prefab entity '{prefabEntity.ToFixedString()}'! Setting to `{ctx.ghostName}`.");
            }
            else
            {
                var nameCopyError = ghostMetaData.Name.CopyTo(ref ctx.ghostName);
                if (nameCopyError != ConversionError.None)
                {
                    ctx.netDebug.LogError($"Copy error '{(int) nameCopyError}' when attempting to save ghostName `{ghostMetaData.Name}` (length: {ghostMetaData.Name.Length}) into FixedString '{ctx.ghostName}' (capacity: {ctx.ghostName.Capacity}) for prefab entity '{prefabEntity.ToFixedString()}'!");
                }
            }

            // Burst 变通方案：通过插值字符串将 FixedString 转换为 Unmanaged String，
            // 以便传给具有 Unmanaged String 显式构造函数重载的 ProfilerMarker
            var profilerMarker = new ProfilerMarker($"{ctx.ghostName}");
            using var auto = profilerMarker.Auto();

            // 计算包含所有 Child Entity 在内的 Component 总数
            // BlobArray 为每个 Child Entity 保存一份 Component Hash 列表
            var hasLinkedGroup = state.EntityManager.HasComponent<LinkedEntityGroup>(prefabEntity);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ghostMetaData.SupportedModes != GhostPrefabBlobMetaData.GhostMode.Both && ghostMetaData.SupportedModes != ghostMetaData.DefaultMode)
            {
                ctx.netDebug.LogError($"The ghost {ctx.ghostName} has a default mode which is not supported! Check the AuthoringComponent to ensure that 'Supported Ghost Modes' includes 'Default Ghost Mode'.");
                return;
            }
#endif
            var fallbackPredictionMode = GhostSpawnBuffer.Type.Interpolated;
            if (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Predicted)
                fallbackPredictionMode = GhostSpawnBuffer.Type.Predicted;

            var nameHash = TypeHash.FNV1A64(ctx.ghostName);
            var ghostType = new GhostCollectionPrefabSerializer
            {
                TypeHash = nameHash,
                FirstComponent = ctx.ghostComponentIndex.Length,
                NumComponents = 0,
                NumChildComponents = 0,
                SnapshotSize = 0,
                ChangeMaskBits = 0,
                PredictionOwnerOffset = -1,
                OwnerPredicted = (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Both) ? 1 : 0,
                PartialComponents = 0,
                BaseImportance = ghostMetaData.Importance,
                MaxSendRateAsSimTickInterval = tickRate.CalculateNetworkSendIntervalOfGhostInTicks(ghostMetaData.MaxSendRate),
                FallbackPredictionMode = fallbackPredictionMode,
                IsGhostGroup = state.EntityManager.HasComponent<GhostGroup>(prefabEntity) ? 1 : 0,
                StaticOptimization = (byte)(ghostMetaData.StaticOptimization ? 1 :0),
                PredictedSpawnedGhostRollbackToSpawnTick = (byte)(ghostMetaData.PredictedSpawnedGhostRollbackToSpawnTick ? 1 : 0),
                RollbackPredictionOnStructuralChanges = (byte)(ghostMetaData.RollbackPredictionOnStructuralChanges ? 1 : 0),
                UseSingleBaseline = (byte)(ghostMetaData.UseSingleBaseline ? 1 : 0),
                NumBuffers = 0,
                MaxBufferSnapshotSize = 0,
                profilerMarker = profilerMarker,
            };

            ctx.childOffset = 0;
            ctx.ghostChildIndex = 0;
            // 将 Component 类型映射到 Collection 中的对应项，并创建函数指针列表
            AddComponents(ref ctx, ref data, ref ghostMetaData, ref ghostType);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (componentInfoLen.Length > 1 && !hasLinkedGroup)
            {
                ctx.netDebug.LogError($"The ghost {ctx.ghostName} expects {componentInfoLen.Length} child entities but no LinkedEntityGroup is present!");
                return;
            }
#endif
            if (hasLinkedGroup)
            {
                var linkedEntityGroup = state.EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (componentInfoLen.Length != linkedEntityGroup.Length)
                {
                    ctx.netDebug.LogError($"The ghost {ctx.ghostName} expects {componentInfoLen.Length} child entities but {linkedEntityGroup.Length} are present.");
                    return;
                }
#endif
                for (var entityIndex = 1; entityIndex < linkedEntityGroup.Length; ++entityIndex)
                {
                    ctx.childOffset += componentInfoLen[entityIndex-1];
                    ctx.ghostChildIndex = entityIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (!state.EntityManager.HasComponent<GhostChildEntity>(linkedEntityGroup[entityIndex].Value))
                    {
                        ctx.netDebug.LogError($"The ghost {ctx.ghostName} has a child entity without the GhostChildEntity!");
                        return;
                    }
#endif
                    AddComponents(ref ctx, ref data, ref ghostMetaData, ref ghostType);
                }
            }
            if (ghostType.PredictionOwnerOffset < 0)
            {
                ghostType.PredictionOwnerOffset = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (ghostType.OwnerPredicted != 0)
                {
                    ctx.netDebug.LogError($"The ghost {ctx.ghostName} is owner predicted, but the ghost owner component could not be found.");
                    return;
                }
                if(ghostType.PartialSendToOwner != 0)
                    ctx.netDebug.DebugLog($"Ghost {ctx.ghostName} has some components that have SendToOwner != All, but no GhostOwner is present. Thus, these SendToOwner flags will be ignored at runtime.");
#endif
            }
            else
            {
                ghostType.PredictionOwnerOffset += GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + GhostComponentSerializer.ChangeMaskArraySizeInUInts(ghostType.ChangeMaskBits)*sizeof(uint) + GhostComponentSerializer.ChangeMaskArraySizeInUInts(ghostType.EnableableBits)*sizeof(uint));
            }
            // 在 Snapshot 中为 Tick 和 ChangeMask 预留空间
            var enabledBitsInBytes = GhostComponentSerializer.ChangeMaskArraySizeInBytes(ghostType.EnableableBits);
            var changeMaskBitsInBytes = GhostComponentSerializer.ChangeMaskArraySizeInBytes(ghostType.ChangeMaskBits);
            ghostType.SnapshotSize += GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskBitsInBytes + enabledBitsInBytes);
            if(ctx.customSerializers.Serializers.TryGetValue((Hash128)ctx.ghostType, out var custom))
            {
                ghostType.CustomSerializer = custom.SerializeChunk;
                ghostType.CustomPreSerializer = custom.PreSerializeChunk;
#if UNITY_EDITOR || NETCODE_DEBUG
                ctx.netDebug.Log($"Successfully registered a custom serializer for ghost with name {ctx.ghostName} and type {(Hash128)ctx.ghostType}");
#endif
            }
            ctx.ghostPrefabSerializerCollection.Add(ghostType);
#if UNITY_EDITOR || NETCODE_DEBUG
            m_GhostNames.Add(ctx.ghostName);
#endif
        }

        /// <summary>
        /// 对全部需要运行时剥离的 Prefab 执行剥离
        /// </summary>
        /// <param name="state"></param>
        /// <exception cref="InvalidOperationException"></exception>
        private void RuntimeStripPrefabs(ref SystemState state, in NetDebug netDebug)
        {
            using var prefabEntities = m_RuntimeStripQuery.ToEntityArray(Allocator.Temp);
            if (state.WorldUnmanaged.IsHost())
            {
                // 在 Single World Host 中手动剥离必要项并提前返回
                state.EntityManager.RemoveComponent(m_RuntimeStripQuery, GhostPrefabCreation.RemoveOnServerWorldsSharedList(Entity.Null, state.EntityManager));
                state.EntityManager.RemoveComponent<GhostPrefabRuntimeStrip>(m_RuntimeStripQuery);

                // Single World Host 无法继续剥离其他内容，因此提前返回
                return;
            }

            using var metaDatas = m_RuntimeStripQuery.ToComponentDataArray<GhostPrefabMetaData>(Allocator.Temp);
            for (int i = 0; i < prefabEntities.Length; i++)
            {
                var prefabEntity = prefabEntities[i];
                ref var ghostMetaData = ref metaDatas[i].Value.Value;

                // 从 Prefab 中删除待移除列表里的全部内容
                ref var removeOnWorld = ref GetRemoveOnWorldList(ref ghostMetaData, state.WorldUnmanaged.IsServer());
                if (removeOnWorld.Length > 0)
                {
                    // 移除 Component 属于结构性变更，因此需要创建副本
                    // Entity 值保持不变，但其所属 Chunk 及对应内存会变化
                    var entities = state.EntityManager.GetBuffer<LinkedEntityGroup>(prefabEntity).ToNativeArray(Allocator.Temp);
                    for (int rm = 0; rm < removeOnWorld.Length; ++rm)
                    {
                        var indexHashPair = removeOnWorld[rm];
                        var compType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(indexHashPair.StableHash));
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (indexHashPair.EntityIndex >= entities.Length)
                        {
                            ref var ghostName = ref ghostMetaData.Name;
                            var ghostNameFs = new FixedString64Bytes();
                            ghostName.CopyTo(ref ghostNameFs);
                            netDebug.LogError($"Cannot remove component from child entity {indexHashPair.EntityIndex} for ghost {ghostNameFs}. Child EntityIndex ({indexHashPair.EntityIndex}) out of bounds ({entities.Length})!");
                            return;
                        }
#endif
                        var ent = entities[indexHashPair.EntityIndex].Value;
                        if (state.EntityManager.HasComponent(ent, compType))
                            state.EntityManager.RemoveComponent(ent, compType);
                    }
                }
            }
            state.EntityManager.RemoveComponent<GhostPrefabRuntimeStrip>(m_RuntimeStripQuery);

            ref BlobArray<GhostPrefabBlobMetaData.ComponentReference> GetRemoveOnWorldList(ref GhostPrefabBlobMetaData ghostMetaData, bool isServer)
            {
                if (isServer)
                    return ref ghostMetaData.RemoveOnServerOnlyWorld;
                return ref ghostMetaData.RemoveOnClientWorlds;
            }
        }

        private void CreateComponentCollection(ref SystemState state, in NetDebug netDebug)
        {
            using var _ = m_CreateComponentCollection.Auto();
            ref var data = ref SystemAPI.GetSingletonRW<GhostComponentSerializerCollectionData>().ValueRW;
            data.ThrowIfCollectionNotFinalized("update GhostCollectionSystem");
            data.Validate();

            var ghostComponentCollectionCount = data.Serializers.Length;
            m_StableHashToComponentTypeIndex = new NativeHashMap<ulong, int>(ghostComponentCollectionCount, Allocator.Persistent);

            // 对 Serializer 排序，并重新映射到对应 SerializationStrategy
            data.Serializers.Sort(default(ComponentHashComparer));
            for (var i = 0; i < data.Serializers.Length; i++)
            {
                ref var stateToRemap = ref data.Serializers.ElementAt(i);
                stateToRemap.SerializationStrategyIndex = -1;
                data.MapSerializerToStrategy(ref stateToRemap, (short) i);
            }

            data.Validate();
            data.CollectionFinalized.Value = 2; // 2 表示此方法已经调用

            // 使用全部状态填充 Ghost Serializer Collection Buffer
            var ghostSerializerCollection = state.EntityManager.GetBuffer<GhostComponentSerializer.State>(m_CollectionSingleton);
            ghostSerializerCollection.Clear();
            ghostSerializerCollection.AddRange(data.Serializers.AsArray());

            // 重置并调整以下 Buffer 的大小，以便后续写入
            {
                var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(m_CollectionSingleton);
                ghostComponentCollection.Clear();
                ghostComponentCollection.Capacity = data.Serializers.Length;
            }

            // 创建 Component 类型唯一列表，为 Ghost Serializer 列表提供反向映射
            m_AllComponentTypes = new NativeList<UsedComponentType>(data.Serializers.Length, Allocator.Persistent);
            for (int i = 0; i < data.Serializers.Length;)
            {
                int firstSerializer = i;
                var compType = data.Serializers[i].ComponentType;
                do
                {
                    ++i;
                } while (i < data.Serializers.Length && data.Serializers[i].ComponentType == compType);
                m_AllComponentTypes.Add(new UsedComponentType
                {
                    UsedIndex = -1,
                    ComponentType = new GhostCollectionComponentType
                    {
                        Type = compType,
                        FirstSerializer = firstSerializer,
                        LastSerializer = i - 1
                    }
                });

                m_StableHashToComponentTypeIndex.Add(TypeManager.GetTypeInfo(compType.TypeIndex).StableTypeHash, m_AllComponentTypes.Length - 1);
            }

            // 此列表不取决于 Prefab 数量，只取决于项目中可用 Serializer 数量
            // 构建时间与预测字段数量呈线性关系，不会变为 Prefab 数量乘以预测字段数量的二次复杂度
#if UNITY_EDITOR || NETCODE_DEBUG
            PrecomputeComponentErrorNameList(ref ghostSerializerCollection);
#endif
            m_ComponentCollectionInitialized = 1;
        }

        private unsafe void AddComponents(ref AddComponentCtx ctx, ref GhostComponentSerializerCollectionData data, ref GhostPrefabBlobMetaData ghostMeta, ref GhostCollectionPrefabSerializer ghostType)
        {
            var isRoot = ctx.ghostChildIndex == 0;
            var allComponentTypes = m_AllComponentTypes;
            ref var serverComponents = ref ghostMeta.ServerComponentList;
            var componentCount = ghostMeta.NumServerComponentsPerEntity[ctx.ghostChildIndex];
            var ghostOwnerHash = TypeManager.GetTypeInfo<GhostOwner>().StableTypeHash;
            for (var i = 0; i < componentCount; ++i)
            {
                ref var componentInfo = ref serverComponents[ctx.childOffset + i];
                if (isRoot && componentInfo.StableHash == ghostOwnerHash)
                    ghostType.PredictionOwnerOffset = ghostType.SnapshotSize;

                if (!m_StableHashToComponentTypeIndex.TryGetValue(componentInfo.StableHash, out var componentIndex))
                    continue;

                ref var usedComponent = ref allComponentTypes.ElementAt(componentIndex);
                var type = usedComponent.ComponentType.Type;
                var variant = data.GetCurrentSerializationStrategyForComponent(type, componentInfo.Variant, isRoot);

                // 选中 Client Only 或 Don't Send Variant 时跳过 Component
                if (variant.IsSerialized == 0 || (!isRoot && variant.SendForChildEntities == 0))
                    continue;

                // 此查找接近 MultiHashMap，平均复杂度为 O(1)，低于线性
                // 但随机的 Component Index Cache Miss 是主要成本
                var serializerIndex = usedComponent.ComponentType.FirstSerializer;
                while (serializerIndex <= usedComponent.ComponentType.LastSerializer &&
                       ctx.ghostSerializerCollection.ElementAt(serializerIndex).VariantHash != variant.Hash)
                    ++serializerIndex;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (serializerIndex > usedComponent.ComponentType.LastSerializer)
                {
                    FixedString512Bytes errorMsg = $"Cannot find serializer for componentIndex {componentIndex} with componentInfo (with variant: '{componentInfo.Variant}') with '{variant}' type returned, for ghost '";
                    errorMsg.Append(ctx.ghostName);
                    errorMsg.Append((FixedString128Bytes) $"'. serializerIndex: {serializerIndex} vs f:{allComponentTypes[componentIndex].ComponentType.FirstSerializer} l:{allComponentTypes[componentIndex].ComponentType.LastSerializer}!");
                    ctx.netDebug.LogError(errorMsg);
                    return;
                }
#endif

                // 应用可能存在的 Prefab Override
                ref var compState = ref ctx.ghostSerializerCollection.ElementAt(serializerIndex);

                var sendMask = componentInfo.SendMaskOverride >= 0
                    ? (GhostSendType) componentInfo.SendMaskOverride
                    : compState.SendMask;

                if (sendMask == 0)
                    continue;
                var supportedModes = ghostMeta.SupportedModes;
                if ((sendMask & GhostSendType.OnlyInterpolatedClients) == 0 &&
                    supportedModes == GhostPrefabBlobMetaData.GhostMode.Interpolated)
                    continue;
                if ((sendMask & GhostSendType.OnlyPredictedClients) == 0 &&
                    supportedModes == GhostPrefabBlobMetaData.GhostMode.Predicted)
                    continue;

                // 找到可序列化 Component
                ++ghostType.NumComponents;
                if (!isRoot)
                    ++ghostType.NumChildComponents;

                if(compState.HasGhostFields)
                {
                    if (type.IsBuffer)
                    {
                        ghostType.SnapshotSize += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                        ghostType.ChangeMaskBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                        ghostType.MaxBufferSnapshotSize = math.max(compState.SnapshotSize, ghostType.MaxBufferSnapshotSize);
                        ++ghostType.NumBuffers;
                    }
                    else
                    {
                        ghostType.SnapshotSize += GhostComponentSerializer.SnapshotSizeAligned(compState.SnapshotSize);
                        ghostType.ChangeMaskBits += compState.ChangeMaskBits;
                    }
                }
                ghostType.EnableableBits += compState.SerializesEnabledBit; // 1 表示 true，0 表示 false，此处隐式映射为计数器

                // 确保此 Component 已标记为正在使用
                if (usedComponent.UsedIndex < 0)
                {
                    usedComponent.UsedIndex = ctx.ghostComponentCollection.Length;
                    ctx.ghostComponentCollection.Add(usedComponent.ComponentType);
                }
                ctx.ghostComponentIndex.Add(new GhostCollectionComponentIndex
                {
                    EntityIndex = ctx.ghostChildIndex,
                    ComponentIndex = usedComponent.UsedIndex,
                    SerializerIndex = serializerIndex,
                    TypeIndex = compState.ComponentType.TypeIndex,
                    ComponentSize = compState.ComponentSize,
                    SnapshotSize = compState.SnapshotSize,
                    SendMask = sendMask,
                    SendToOwner = compState.SendToOwner,
#if UNITY_EDITOR || NETCODE_DEBUG
                    PredictionErrorBaseIndex = m_currentPredictionErrorCount
#endif
                });
                if (sendMask != GhostSendType.AllClients)
                    ghostType.PartialComponents = 1;

                if (compState.SendToOwner != SendToOwnerType.All)
                    ghostType.PartialSendToOwner = 1;

#if UNITY_EDITOR || NETCODE_DEBUG
                m_currentPredictionErrorCount += compState.NumPredictionErrors;
                if (compState.NumPredictionErrorNames > 0)
                {
                    m_currentPredictionErrorNamesCount += compState.NumPredictionErrorNames;
                    m_PendingNameAssignments.Add(new PendingNameAssignment(m_GhostNames.Length, ctx.ghostChildIndex, serializerIndex));
                }
#endif
                var serializationHash =
                    TypeHash.CombineFNV1A64(compState.SerializerHash, TypeHash.FNV1A64((int) sendMask));
                serializationHash = TypeHash.CombineFNV1A64(serializationHash, variant.Hash);
                ghostType.TypeHash = TypeHash.CombineFNV1A64(ghostType.TypeHash, serializationHash);
            }
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// 用于跟踪待处理并追加预测错误名称的 Component Serialization、Ghost 和 Child 元组的内部结构体
        /// </summary>
        internal struct PendingNameAssignment
        {
            public PendingNameAssignment(int nameIndex, int childIndex, int serializer)
            {
                ghostName = nameIndex;
                ghostChildIndex = childIndex;
                serializerIndex = serializer;
            }
            /// <summary>
            /// GhostName Collection 数组中的索引
            /// </summary>
            public int ghostName;
            /// <summary>
            /// Prefab 中的 Child 索引
            /// </summary>
            public int ghostChildIndex;
            /// <summary>
            /// Ghost Component Serializer Collection 中的索引
            /// </summary>
            public int serializerIndex;
        }

        private void ProcessPendingNameAssignments(DynamicBuffer<GhostComponentSerializer.State> ghostComponentSerializers)
        {
            var appendIndex = m_PredictionErrorNames.Length;
            Assertions.Assert.IsTrue(m_currentPredictionErrorNamesCount >= m_PredictionErrorNames.Length);
            m_PredictionErrorNames.ResizeUninitialized(m_currentPredictionErrorNamesCount);
            foreach (var nameToAssign in m_PendingNameAssignments)
            {
                ref var ghostName = ref m_GhostNames.ElementAt(nameToAssign.ghostName);
                ref var compState = ref ghostComponentSerializers.ElementAt(nameToAssign.serializerIndex);
                var ghostChildIndex = nameToAssign.ghostChildIndex;
                for (var i = 0; i < compState.NumPredictionErrorNames; ++i)
                {
                    ref var errorName = ref m_PredictionErrorNames.ElementAt(appendIndex).Name;
                    var compStartEnd = m_PredictionErrorNamesStartEndCache[compState.FirstNameIndex + i];
                    errorName = ghostName;
                    if (ghostChildIndex != 0)
                    {
                        errorName.Append('[');
                        errorName.Append(ghostChildIndex);
                        errorName.Append(']');
                    }
                    errorName.Append('.');
                    errorName.Append(compState.ComponentType.GetDebugTypeName());
                    errorName.Append('.');
                    unsafe
                    {
                        errorName.Append(compState.PredictionErrorNames.GetUnsafePtr() + compStartEnd.Item1, compStartEnd.Item2 - compStartEnd.Item1);
                    }
                    ++appendIndex;
                }
            }
            m_PendingNameAssignments.Clear();
        }
        static private void UpdateGhostNames(DynamicBuffer<GhostNames> ghosts, NativeList<FixedString64Bytes> ghostNames)
        {
            ghosts.Clear();
            ghosts.ResizeUninitialized(ghostNames.Length);
            unsafe
            {
                UnsafeUtility.MemCpy(ghosts.GetUnsafePtr(), ghostNames.GetUnsafeReadOnlyPtr(), sizeof(FixedString64Bytes) * ghostNames.Length);
            }
        }
        static private void UpdatePredictionErrorNames(DynamicBuffer<PredictionErrorNames> predictionErrors, NativeList<PredictionErrorNames> predictionErrorNames)
        {
            predictionErrors.Clear();
            predictionErrors.AddRange(predictionErrorNames.AsArray());
        }
        /// <summary>
        /// 预处理 GhostComponentSerializer.State Collection，
        /// 并预先解析全部 Serializer 的 PredictionErrorNames
        /// </summary>
        /// <param name="serializers"></param>
        private void PrecomputeComponentErrorNameList(ref DynamicBuffer<GhostComponentSerializer.State> serializers)
        {
            int totalNameCount = 0;
            // 计算所需名称数量，此值为上限
            for (int i = 0; i < serializers.Length; ++i)
            {
                ref var serializer = ref serializers.ElementAt(i);
                totalNameCount += serializer.NumPredictionErrors;
            }
            m_PredictionErrorNamesStartEndCache.Clear();
            m_PredictionErrorNamesStartEndCache.Capacity = totalNameCount;
            for(int i=0;i<serializers.Length;++i)
            {
                ref var serializer = ref serializers.ElementAt(i);
                serializer.FirstNameIndex = m_PredictionErrorNamesStartEndCache.Length;
                short strStart = 0;
                short strEnd = 0;
                int strLen = serializer.PredictionErrorNames.Length;
                while (strStart < strLen)
                {
                    strEnd = strStart;
                    while (strEnd < strLen && serializer.PredictionErrorNames[strEnd] != ',')
                        ++strEnd;
                    m_PredictionErrorNamesStartEndCache.Add(ValueTuple.Create(strStart, strEnd));
                    strStart = (short)(strEnd + 1);
                }
                // 分配可用名称子集，其数量必须始终小于或等于错误总数
                serializer.NumPredictionErrorNames = m_PredictionErrorNamesStartEndCache.Length - serializer.FirstNameIndex;
                Assertions.Assert.IsTrue(serializer.NumPredictionErrorNames <= serializer.NumPredictionErrors);
            }
        }
#endif
    }
}
