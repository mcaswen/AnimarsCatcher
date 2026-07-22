using System;
using Unity.Assertions;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Jobs;

namespace Unity.NetCode
{
    // Prediction Backup State 的 Header
    // Header 后依次存放以下数据
    // Entity[Capacity]：此历史记录对应的实体，用于避免结构变更导致实体错配
    // ulong[Capacity*enabledBits]：每个启用位按所有实体组成一个连续数组，并按 ulong 对齐
    // int[root components + Capacity * num_child_component]：Chunk 及子 Chunk 的版本号
    // byte*[Capacity * sizeof(IComponentData)]：此 Ghost 类型全部复制组件的原始备份数据，Buffer 则存储 uint 数对 length 和 offset
    // [Opt]byte*[BuffersDataSize]：Chunk 中存在的 Buffer 元素原始数据
    // Buffer 总大小在运行时计算并据此调整备份状态，每段 Buffer 内容从 16 字节对齐偏移量开始
    // 布局示例：Align(b1Elem*b1ElemSize, 16), Align(b2Elem*b2ElemSize, 16) ...

    internal unsafe struct PredictionBackupState
    {
        // Ghost 类型发生变化时必须丢弃数据，因为该 Chunk 已被用于其他内容
        public int ghostType;
        public int entityCapacity;
        public int entitiesOffset;
        public int enabledBitOffset;
        public int enabledBits;
        public int ghostOwnerOffset;
        // Ghost 组件的序列化大小
        public int dataOffset;
        public int dataSize;
        // Chunk 版本
        public int chunkVersionsOffset;
        public int chunkVersionsSize;
        // 动态数据容量，Dynamic Buffer 存储在组件备份之后
        public int bufferDataCapacity;
        public int bufferDataOffset;

        public static IntPtr AllocNew(int ghostTypeId, int enabledBits,
            int numComponents, int dataSize, int entityCapacity, int buffersDataCapacity, int predictionOwnerOffset)
        {
            var entitiesSize = (ushort)GetEntitiesSize(entityCapacity, out var _);
            var headerSize = GetHeaderSize();
            // 每个启用位都使用一个足以容纳所有实体的独立数组
            var enabledBitSize = (((entityCapacity+63)&(~63))/8 * enabledBits + 15) & (~15);
            var versionSize = (sizeof(int) * numComponents * entityCapacity + 15) & ~15;
            var state = (PredictionBackupState*)UnsafeUtility.Malloc(headerSize + enabledBitSize + entitiesSize + versionSize + dataSize + buffersDataCapacity, 16, Allocator.Persistent);
            state->ghostType = ghostTypeId;
            state->entityCapacity = entityCapacity;
            state->entitiesOffset = headerSize;
            state->enabledBitOffset = headerSize + entitiesSize;
            state->ghostOwnerOffset = predictionOwnerOffset;
            state->enabledBits = enabledBits;
            state->chunkVersionsOffset = headerSize + entitiesSize + enabledBitSize;
            state->chunkVersionsSize = versionSize;
            state->dataOffset = state->chunkVersionsOffset + versionSize;
            state->dataSize = dataSize;
            state->bufferDataCapacity = buffersDataCapacity;
            state->bufferDataOffset = state->dataOffset + dataSize;
            return (IntPtr)state;
        }

        public static int GetEntityCapacity(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return ps->entityCapacity;
        }
        public static int GetHeaderSize()
        {
            return (UnsafeUtility.SizeOf<PredictionBackupState>() + 15) & (~15);
        }
        public static int GetEntitiesSize(int chunkCapacity, out int singleEntitySize)
        {
            singleEntitySize = UnsafeUtility.SizeOf<Entity>();
            return ((singleEntitySize * chunkCapacity) + 15) & (~15);
        }
        public static int GetDataSize(int componentSize, int chunkCapacity)
        {
            return (componentSize * chunkCapacity + 15) &(~15);
        }
        public static Entity* GetEntities(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return (Entity*)(((byte*)state) + ps->entitiesOffset);
        }
        public static bool MatchEntity(IntPtr state, int ent, in Entity entity)
        {
            var ps = ((PredictionBackupState*) state);
            return ((Entity*)(((byte*)state) + ps->entitiesOffset))[ent] == entity;
        }
        public static byte* GetData(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return ((byte*) state) + ps->dataOffset;
        }

        public static uint* GetChunkVersion(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return (uint*)((byte*)state + ps->chunkVersionsOffset);
        }

        public static int GetBufferDataCapacity(IntPtr state)
        {
            return ((PredictionBackupState*) state)->bufferDataCapacity;
        }
        public static byte* GetBufferDataPtr(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return ((byte*) state) + ps->bufferDataOffset;
        }
        public static byte* GetNextData(byte* data, int componentSize, int chunkCapacity)
        {
            return data + GetDataSize(componentSize, chunkCapacity);
        }
        public static ulong* GetEnabledBits(IntPtr state)
        {
            var ps = ((PredictionBackupState*) state);
            return (ulong*)(((byte*) state) + ps->enabledBitOffset);
        }
        public static ulong* GetNextEnabledBits(ulong* data, int chunkCapacity)
        {
            return data + (chunkCapacity+63)/64;
        }
        public static int GetGhostOwner(IntPtr state, int ent)
        {
            var ps = ((PredictionBackupState*) state);
            if (ps->ghostOwnerOffset == -1)
                return -1;
            var owners = (int*)((byte*)state + ps->dataOffset + ps->ghostOwnerOffset);
            return owners[ent];
        }

        public static uint* GetNextChildChunkVersion(uint* changeVersionPtr, int chunkCapacity)
        {
            return changeVersionPtr + chunkCapacity;
        }
    }

    /// <summary>
    /// 存在 Snapshot 备份的最后一个完整 Tick，仅存在于客户端 World
    /// </summary>
    internal struct GhostSnapshotLastBackupTick : IComponentData
    {
        public NetworkTick Value;
    }

    internal struct GhostPredictionHistoryState : IComponentData
    {
        public NativeParallelHashMap<ArchetypeChunk, System.IntPtr>.ReadOnly PredictionState;
        public NativeParallelHashMap<Entity, GhostPredictionHistorySystem.PredictionBufferHistoryData>.ReadOnly EntityData;
    }

    /// <summary>
    /// 在一帧预测循环的最后一个完整 Tick，而非分数 Tick，完成后备份当前预测状态的系统
    /// 备份会将所有 Ghost 组件复制到与 Chunk 关联的独立内存区域
    /// 没有新数据到达时，此备份用于恢复最后一个完整 Tick 并继续预测
    /// 注意：恢复时只会写回实际作为 Snapshot 一部分序列化的字段，而不是整个组件
    /// 因此可以保留所有非 GhostField 状态
    /// 备份数据还用于以下用途
    /// - 检测预测误差
    /// - 对预测值进行平滑
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GhostPredictionEnableSimulateSystem))]
    [UpdateBefore(typeof(EndPredictedSimulationEntityCommandBufferSystem))]
    [BurstCompile]
    public unsafe partial struct GhostPredictionHistorySystem : ISystem
    {
        struct PredictionStateEntry
        {
            public ArchetypeChunk chunk;
            public System.IntPtr data;
        }

        /// <summary>
        /// 用于在发生结构变更后仍能检索或推导给定实体预测历史数据的数据结构
        /// 此结构会在备份时记录实体历史数据所在的 Chunk 和索引
        /// GhostUpdateSystem 后续从备份恢复实体状态时会使用这些信息
        /// </summary>
        internal struct PredictionBufferHistoryData
        {
            /// <summary>
            /// 上次历史备份时的 ArchetypeChunk
            /// </summary>
            public ArchetypeChunk lastChunk;
            /// <summary>
            /// 上次历史备份时实体在 Chunk 中的索引
            /// </summary>
            public int LastIndexInChunk;
            /// <summary>
            /// 对应 Chunk 的容量，用于正确解码历史数据
            /// </summary>
            public int LastChunkCapacity;
        }

        NativeParallelHashMap<ArchetypeChunk, System.IntPtr> m_PredictionState;
        NativeParallelHashMap<Entity, PredictionBufferHistoryData> m_EntityData;
        NativeParallelHashMap<ArchetypeChunk, int> m_StillUsedPredictionState;
        NativeQueue<PredictionStateEntry> m_NewPredictionState;
        NativeQueue<PredictionStateEntry> m_UpdatedPredictionState;
        EntityQuery m_PredictionQuery;

        ComponentTypeHandle<GhostInstance> m_GhostComponentHandle;
        ComponentTypeHandle<GhostType> m_GhostTypeComponentHandle;
        ComponentTypeHandle<PreSpawnedGhostIndex> m_PreSpawnedGhostIndexHandle;
        ComponentTypeHandle<PredictedGhostSpawnRequest> m_PredictedSpawnRequestTypeHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        EntityTypeHandle m_EntityTypeHandle;

        BufferLookup<GhostComponentSerializer.State> m_GhostComponentSerializerStateFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionPrefabFromEntity;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_PredictionState = new NativeParallelHashMap<ArchetypeChunk, System.IntPtr>(128, Allocator.Persistent);
            m_StillUsedPredictionState = new NativeParallelHashMap<ArchetypeChunk, int>(128, Allocator.Persistent);
            m_EntityData = new NativeParallelHashMap<Entity, PredictionBufferHistoryData>(128, Allocator.Persistent);
            m_NewPredictionState = new NativeQueue<PredictionStateEntry>(Allocator.Persistent);
            m_UpdatedPredictionState = new NativeQueue<PredictionStateEntry>(Allocator.Persistent);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PredictedGhost, GhostInstance>();
            m_PredictionQuery = state.GetEntityQuery(builder);

            state.RequireForUpdate<GhostCollection>();

            m_GhostComponentHandle = state.GetComponentTypeHandle<GhostInstance>(true);
            m_GhostTypeComponentHandle = state.GetComponentTypeHandle<GhostType>(true);
            m_PreSpawnedGhostIndexHandle = state.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_PredictedSpawnRequestTypeHandle = state.GetComponentTypeHandle<PredictedGhostSpawnRequest>(true);
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_EntityTypeHandle = state.GetEntityTypeHandle();

            m_GhostComponentSerializerStateFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostCollectionPrefabSerializerFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionPrefabFromEntity = state.GetBufferLookup<GhostCollectionPrefab>(true);

            var atype = new NativeArray<ComponentType>(1, Allocator.Temp);
            atype[0] = ComponentType.ReadWrite<GhostPredictionHistoryState>();
            var historySingleton = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(atype));
            FixedString64Bytes singletonName = "GhostPredictionHistoryState-Singleton";
            state.EntityManager.SetName(historySingleton, singletonName);
            // 声明本系统会写入 GhostPredictionHistoryState，使 OnUpdate 依赖此单例的所有读取者
            ref var predictionHistoryState = ref SystemAPI.GetSingletonRW<GhostPredictionHistoryState>().ValueRW;
            predictionHistoryState.PredictionState = m_PredictionState.AsReadOnly();
            predictionHistoryState.EntityData = m_EntityData.AsReadOnly();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;
            var values = m_PredictionState.GetValueArray(Allocator.Temp);
            for (int i = 0; i < values.Length; ++i)
            {
                UnsafeUtility.Free((void*)values[i], Allocator.Persistent);
            }
            m_PredictionState.Dispose();
            m_StillUsedPredictionState.Dispose();
            m_NewPredictionState.Dispose();
            m_UpdatedPredictionState.Dispose();
            m_EntityData.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!networkTime.IsFinalFullPredictionTick)
                return;
            SystemAPI.SetSingleton(new GhostSnapshotLastBackupTick { Value = networkTime.ServerTick });

            var predictionState = m_PredictionState;
            var newPredictionState = m_NewPredictionState;
            var stillUsedPredictionState = m_StillUsedPredictionState;
            var updatedPredictionState = m_UpdatedPredictionState;
            stillUsedPredictionState.Clear();
            m_EntityData.Clear();
            var count = m_PredictionQuery.CalculateEntityCount();
            if(count > m_EntityData.Capacity)
                m_EntityData.Capacity = count;
            if (stillUsedPredictionState.Capacity < predictionState.Capacity)
                stillUsedPredictionState.Capacity = predictionState.Capacity;

            m_GhostComponentHandle.Update(ref state);
            m_GhostTypeComponentHandle.Update(ref state);
            m_PreSpawnedGhostIndexHandle.Update(ref state);
            m_PredictedSpawnRequestTypeHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);
            m_EntityTypeHandle.Update(ref state);
            m_GhostComponentSerializerStateFromEntity.Update(ref state);
            m_GhostCollectionPrefabSerializerFromEntity.Update(ref state);
            m_GhostCollectionComponentIndexFromEntity.Update(ref state);
            m_GhostCollectionPrefabFromEntity.Update(ref state);
            var backupJob = new PredictionBackupJob
            {
                predictionState = predictionState,
                stillUsedPredictionState = stillUsedPredictionState.AsParallelWriter(),
                newPredictionState = newPredictionState.AsParallelWriter(),
                updatedPredictionState = updatedPredictionState.AsParallelWriter(),
                entityData = m_EntityData.AsParallelWriter(),
                ghostComponentType = m_GhostComponentHandle,
                ghostType = m_GhostTypeComponentHandle,
                prespawnIndexType = m_PreSpawnedGhostIndexHandle,
                predictedGhostSpawnRequestType = m_PredictedSpawnRequestTypeHandle,
                entityType = m_EntityTypeHandle,

                GhostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = m_GhostComponentSerializerStateFromEntity,
                GhostTypeCollectionFromEntity = m_GhostCollectionPrefabSerializerFromEntity,
                GhostComponentIndexFromEntity = m_GhostCollectionComponentIndexFromEntity,
                GhostPrefabCollectionFromEntity = m_GhostCollectionPrefabFromEntity,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),

                childEntityLookup = state.GetEntityStorageInfoLookup(),
                linkedEntityGroupType = m_LinkedEntityGroupHandle,
            };

            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(backupJob.GhostCollectionSingleton);
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref backupJob.DynamicTypeList);
            state.Dependency = backupJob.ScheduleParallelByRef(m_PredictionQuery, state.Dependency);

            var cleanupJob = new CleanupPredictionStateJob
            {
                predictionState = predictionState,
                stillUsedPredictionState = stillUsedPredictionState,
                newPredictionState = newPredictionState,
                updatedPredictionState = updatedPredictionState
            };
            state.Dependency = cleanupJob.Schedule(state.Dependency);
        }

        [BurstCompile]
        struct CleanupPredictionStateJob : IJob
        {
            public NativeParallelHashMap<ArchetypeChunk, System.IntPtr> predictionState;
            [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, int> stillUsedPredictionState;
            public NativeQueue<PredictionStateEntry> newPredictionState;
            public NativeQueue<PredictionStateEntry> updatedPredictionState;
            public void Execute()
            {
                var keys = predictionState.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; ++i)
                {
                    if (!stillUsedPredictionState.TryGetValue(keys[i], out var temp))
                    {
                        // 释放内存并从查找表移除 Chunk
                        predictionState.TryGetValue(keys[i], out var alloc);
                        UnsafeUtility.Free((void*)alloc, Allocator.Persistent);
                        predictionState.Remove(keys[i]);
                    }
                }
                while (newPredictionState.TryDequeue(out var newState))
                {
                    if (!predictionState.TryAdd(newState.chunk, newState.data))
                    {
                        // 移除并释放旧值后添加新值，这会发生在 Chunk 被过快复用时
                        predictionState.TryGetValue(newState.chunk, out var alloc);
                        UnsafeUtility.Free((void*)alloc, Allocator.Persistent);
                        predictionState.Remove(newState.chunk);
                        // 重新添加新备份状态
                        predictionState.TryAdd(newState.chunk, newState.data);
                    }
                }
                while (updatedPredictionState.TryDequeue(out var updatedState))
                {
                    if(!predictionState.ContainsKey(updatedState.chunk))
                        throw new InvalidOperationException($"Prediction backup state has been updated but is not present in the map.");
                    predictionState[updatedState.chunk] = updatedState.data;
                }
            }
        }

        [BurstCompile]
        struct PredictionBackupJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;

            [ReadOnly]public NativeParallelHashMap<ArchetypeChunk, System.IntPtr> predictionState;
            public NativeParallelHashMap<ArchetypeChunk, int>.ParallelWriter stillUsedPredictionState;
            public NativeParallelHashMap<Entity, PredictionBufferHistoryData>.ParallelWriter entityData;
            public NativeQueue<PredictionStateEntry>.ParallelWriter newPredictionState;
            public NativeQueue<PredictionStateEntry>.ParallelWriter updatedPredictionState;
            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostType> ghostType;
            [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnIndexType;
            [ReadOnly] public ComponentTypeHandle<PredictedGhostSpawnRequest> predictedGhostSpawnRequestType;
            [ReadOnly] public EntityTypeHandle entityType;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostPrefabCollectionFromEntity;
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public NetDebug netDebug;
            const GhostSendType requiredSendMask = GhostSendType.OnlyPredictedClients;

            // 汇总所有 Dynamic Buffer 原始数据的大小，每段 Buffer 内容按 16 字节对齐
            private int GetChunkBuffersDataSize(GhostCollectionPrefabSerializer typeData, ArchetypeChunk chunk,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength, DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex, DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int bufferTotalSize = 0;
                int baseOffset = typeData.FirstComponent;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException($"Component index {comp} (of numBaseComponents: {numBaseComponents}) out of range for root component in method 'GetChunkBuffersDataSize'. ghostChunkComponentTypesLength is {ghostChunkComponentTypesLength}.");
#endif
                    if ((GhostComponentIndex[baseOffset + comp].SendMask & requiredSendMask) == 0)
                        continue;

                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    if (!ghostSerializer.ComponentType.IsBuffer)
                        continue;

                    if (chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                    {
                        var bufferData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        for (int i = 0; i < bufferData.Length; ++i)
                        {
                            bufferTotalSize += bufferData.GetBufferCapacity(i) * ghostSerializer.ComponentSize;
                        }
                        bufferTotalSize = GhostComponentSerializer.SnapshotSizeAligned(bufferTotalSize);
                    }
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException($"Component index {comp} (of numBaseComponents: {numBaseComponents}) out of range for child component in method 'GetChunkBuffersDataSize'. ghostChunkComponentTypesLength is {ghostChunkComponentTypesLength}.");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask & requiredSendMask) == 0)
                            continue;

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (!ghostSerializer.ComponentType.IsBuffer)
                            continue;

                        for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value;
                            if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufferData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                bufferTotalSize += bufferData.GetBufferCapacity(childChunk.IndexInChunk) * ghostSerializer.ComponentSize;
                            }
                            bufferTotalSize = GhostComponentSerializer.SnapshotSizeAligned(bufferTotalSize);
                        }
                    }
                }

                return bufferTotalSize;
            }

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用组件类型的查询
                Assert.IsFalse(useEnabledMask);

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicTypeList.Length;
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];
                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                var GhostPrefabCollection = GhostPrefabCollectionFromEntity[GhostCollectionSingleton];

                var ghostComponents = chunk.GetNativeArray(ref ghostComponentType);
                var ghostTypes = chunk.GetNativeArray(ref ghostType);
                int ghostTypeId = ghostComponents.GetFirstGhostTypeId();
                var isPrespawnedGhost = chunk.Has(ref prespawnIndexType);
                var isPredictedSpawnedGhost = chunk.Has(ref predictedGhostSpawnRequestType);
                if (ghostTypeId < 0 && !isPrespawnedGhost)
                {
                    netDebug.LogError($"Found chunk with ghost type {(Hash128)ghostTypes[0]} that is not a pre-spawned ghost (there is not PreSpawnedGhostIndex component), and has a negative GhostInstance.type. Only prespawned ghosts are allowed to use a negative type index.");
                    return;
                }

                // 预生成 Ghost 和尚未收到服务器更新的预测生成 Ghost 需要先解析其类型
                if (ghostTypeId < 0 || isPredictedSpawnedGhost)
                {
                    // 使用 GhostTypeCollection 而不是 GhostPrefabCollection 作为循环范围
                    // 因为前者长度保证小于或等于后者长度，并在此通过断言验证
                    Assertions.Assert.IsTrue(GhostTypeCollection.Length <= GhostPrefabCollection.Length);
                    for (ghostTypeId = 0; ghostTypeId < GhostTypeCollection.Length; ++ghostTypeId)
                    {
                        if (GhostPrefabCollection[ghostTypeId].GhostType == ghostTypes[0])
                            break;
                    }
                    // Ghost 集合的 Prefab Serializer 尚未完全初始化时，这是合法状态
                    if (ghostTypeId >= GhostTypeCollection.Length)
                        return;
                }
                if(ghostTypeId >= GhostTypeCollection.Length)
                    throw new InvalidOperationException($"Cannot find ghostTypeId in GhostPrefabCollection as expected to match {ghostTypes[0]} but didn't. (GhostPrefabCollection.Length: {GhostPrefabCollection.Length}).");

                var typeData = GhostTypeCollection[ghostTypeId];
                var singleEntitySize = UnsafeUtility.SizeOf<Entity>();
                int baseOffset = typeData.FirstComponent;
                int predictionOwnerOffset = -1;
                var ghostOwnerTypeIndex = TypeManager.GetTypeIndex<GhostOwner>();
                if (!predictionState.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).ghostType != ghostTypeId ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                {
                    int dataSize = 0;
                    int enabledBits = 0;
                    // 汇总所有组件对齐后的大小
                    // 规则
                    // - 如果组件或 Buffer 的 SendMask 不匹配 PredictedClient，则备份中既不包含数据，也不包含启用位
                    // - 如果组件或 Buffer 会复制启用位，则备份中包含这些位
                    // - 如果组件没有 GhostField，则备份中不包含其数据

                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException($"Component index {comp} (of numBaseComponents: {typeData.NumComponents}) out of range in method 'Execute'. ghostChunkComponentTypesLength is {ghostChunkComponentTypesLength}.");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                            continue;

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (ghostSerializer.SerializesEnabledBit != 0)
                            ++enabledBits;

                        if (!ghostSerializer.HasGhostFields)
                            continue;

                        if (ghostSerializer.ComponentType.TypeIndex == ghostOwnerTypeIndex)
                            predictionOwnerOffset = dataSize;

                        // Buffer 使用一对 uint 存储元数据
                        // uint length：元素数量
                        // uint backupDataOffset：数据在备份 Buffer 中的起始位置
                        if (!ghostSerializer.ComponentType.IsBuffer)
                            dataSize += PredictionBackupState.GetDataSize(ghostSerializer.ComponentSize, chunk.Capacity);
                        else
                            dataSize += PredictionBackupState.GetDataSize(GhostComponentSerializer.DynamicBufferComponentSnapshotSize, chunk.Capacity);
                    }

                    // 计算存储该 Chunk Dynamic Buffer 数据所需的空间
                    int buffersDataCapacity = 0;
                    if (typeData.NumBuffers > 0)
                        buffersDataCapacity = GetChunkBuffersDataSize(typeData, chunk, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, GhostComponentIndex, GhostComponentCollection);

                    // Chunk 尚不存在于历史记录中或其 Ghost 类型已变化，因此需要创建新的备份状态
                    state = PredictionBackupState.AllocNew(ghostTypeId, enabledBits, typeData.NumComponents, dataSize, chunk.Capacity, buffersDataCapacity, predictionOwnerOffset);
                    newPredictionState.Enqueue(new PredictionStateEntry{chunk = chunk, data = state});
                }
                else
                {
                    stillUsedPredictionState.TryAdd(chunk, 1);
                    if (typeData.NumBuffers > 0)
                    {
                        // 调整备份状态大小以容纳 Dynamic Buffer 内容
                        var buffersDataCapacity = GetChunkBuffersDataSize(typeData, chunk, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, GhostComponentIndex, GhostComponentCollection);
                        int bufferBackupDataCapacity = PredictionBackupState.GetBufferDataCapacity(state);
                        if (bufferBackupDataCapacity < buffersDataCapacity)
                        {
                            var dataSize = ((PredictionBackupState*)state)->dataSize;
                            var enabledBits = ((PredictionBackupState*)state)->enabledBits;
                            var ghostOwnerOffset = ((PredictionBackupState*)state)->ghostOwnerOffset;
                            var newState =  PredictionBackupState.AllocNew(ghostTypeId, enabledBits, typeData.NumComponents, dataSize, chunk.Capacity, buffersDataCapacity, ghostOwnerOffset);
                            UnsafeUtility.Free((void*) state, Allocator.Persistent);
                            state = newState;
                            updatedPredictionState.Enqueue(new PredictionStateEntry{chunk = chunk, data = newState});
                        }
                    }
                }
                Entity* entities = PredictionBackupState.GetEntities(state);
                var srcEntities = chunk.GetNativeArray(entityType).GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(entities, srcEntities, chunk.Count * singleEntitySize);
                if (chunk.Count < chunk.Capacity)
                    UnsafeUtility.MemClear(entities + chunk.Count, (chunk.Capacity - chunk.Count) * singleEntitySize);
                for (int i = 0; i < chunk.Count; ++i)
                {
                    entityData.TryAdd(entities[i], new PredictionBufferHistoryData
                    {
                        lastChunk = chunk,
                        LastIndexInChunk = i,
                        LastChunkCapacity = chunk.Capacity
                    });
                }
                byte* dataPtr = PredictionBackupState.GetData(state);
                byte* bufferBackupDataPtr = PredictionBackupState.GetBufferDataPtr(state);
                ulong* enabledBitPtr = PredictionBackupState.GetEnabledBits(state);
                uint* changeVersionPtr = PredictionBackupState.GetChunkVersion(state);

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int bufferBackupDataOffset = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException($"Component index {comp} (of numBaseComponents: {numBaseComponents}) out of range for root component in method 'Execute'. ghostChunkComponentTypesLength is {ghostChunkComponentTypesLength}.");
#endif
                    if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                        continue;
                    uint chunkVersion = chunk.GetChangeVersion(ref ghostChunkComponentTypesPtr[compIdx]);
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var compSize = ghostSerializer.ComponentType.IsBuffer
                        ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                        : ghostSerializer.ComponentSize;

                    // 存储根实体上此组件的 ChangeVersion
                    // 对根实体而言，此 Chunk 中每种组件只有一个版本条目
                    changeVersionPtr[comp] = chunkVersion;

                    if (ghostSerializer.SerializesEnabledBit != 0)
                    {
                        var handle = ghostChunkComponentTypesPtr[compIdx];
                        var bitArray = chunk.GetEnableableBits(ref handle);
                        UnsafeUtility.MemCpy(enabledBitPtr, &bitArray, ((chunk.Count+63)&(~63))/8);

                        enabledBitPtr = PredictionBackupState.GetNextEnabledBits(enabledBitPtr, chunk.Capacity);
                    }

                    // 注意，HasGhostFields 读取该类型的 SnapshotSize，但此处保存的是完整组件
                    // 如果最终不会写回任何数据，就没有必要复制完整组件状态
                    // 恢复时实际只会写回 GhostField
                    if (!ghostSerializer.HasGhostFields)
                        continue;

                    if (!chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                    {
                        UnsafeUtility.MemClear(dataPtr, chunk.Count * compSize);
                        // 组件数据不存在时将 ChangeVersion 重置为 0
                        // 如果组件之后出现，就必须将其视为已变化
                        changeVersionPtr[comp] = 0;
                    }
                    else if (!ghostSerializer.ComponentType.IsBuffer)
                    {
                        var compData = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        UnsafeUtility.MemCpy(dataPtr, compData, chunk.Count * compSize);
                    }
                    else
                    {
                        var bufferData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var bufElemSize = ghostSerializer.ComponentSize;
                        // 使用局部指针迭代并设置 Buffer 偏移量和长度
                        // dataPtr 必须按 Chunk 推进到下一个正确位置
                        var tempDataPtr = dataPtr;
                        for (int i = 0; i < bufferData.Length; ++i)
                        {
                            // 获取并复制每段 Buffer 数据，在组件备份中记录其长度和备份 Buffer 偏移量
                            var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(i, out var size);
                            ((int*) tempDataPtr)[0] = size;
                            ((int*) tempDataPtr)[1] = bufferBackupDataOffset;
                            if (size > 0)
                                UnsafeUtility.MemCpy(bufferBackupDataPtr + bufferBackupDataOffset, (byte*) bufferPtr, size * bufElemSize);
                            bufferBackupDataOffset += size * bufElemSize;
                            tempDataPtr += compSize;
                        }

                        bufferBackupDataOffset = GhostComponentSerializer.SnapshotSizeAligned(bufferBackupDataOffset);
                    }
                    dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    // 对于子组件，Chunk 中每个实体的每种组件类型都存储一个版本条目
                    // 布局如下
                    // 子组件1       子组件2
                    //e1, e2 .. en | e1, e2 .. en
                    var childChangeVersions = changeVersionPtr + numBaseComponents;
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
                        var handle = ghostChunkComponentTypesPtr[compIdx];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException($"Component index {comp} (of numBaseComponents: {numBaseComponents}) out of range for child component in method 'Execute'. ghostChunkComponentTypesLength is {ghostChunkComponentTypesLength}.");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                            continue;

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (ghostSerializer.SerializesEnabledBit != 0)
                        {
                            for (int rootEnt = 0, chunkEntityCount = chunk.Count; rootEnt < chunkEntityCount; ++rootEnt)
                            {
                                ulong isSet = 0;
                                var linkedEntityGroup = linkedEntityGroupAccessor[rootEnt];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value;
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk))
                                {
                                    var arr = childChunk.Chunk.GetEnableableBits(ref handle);
                                    var bits = new UnsafeBitArray(&arr, sizeof(v128));
                                    isSet = bits.IsSet(childChunk.IndexInChunk) ? 1u : 0u;
                                    childChangeVersions[rootEnt] = childChunk.Chunk.GetChangeVersion(ref ghostChunkComponentTypesPtr[compIdx]);
                                }
                                enabledBitPtr[rootEnt>>6] &= ~(1ul<<(rootEnt&0x3f));
                                enabledBitPtr[rootEnt>>6] |= (isSet<<(rootEnt&0x3f));
                            }
                            enabledBitPtr = PredictionBackupState.GetNextEnabledBits(enabledBitPtr, chunk.Capacity);
                        }
                        var isBuffer = ghostSerializer.ComponentType.IsBuffer;
                        var compSize = isBuffer ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize : ghostSerializer.ComponentSize;

                        if (!ghostSerializer.HasGhostFields)
                        {
                            if(ghostSerializer.SerializesEnabledBit != 0)
                                childChangeVersions = PredictionBackupState.GetNextChildChunkVersion(childChangeVersions, chunk.Capacity);
                            continue;
                        }

                        if (!ghostSerializer.ComponentType.IsBuffer)
                        {
                            // 此处使用临时指针迭代，否则 dataPtr 按 Chunk 偏移后会落到错误位置
                            var tempDataPtr = dataPtr;

                            for (int rootEnt = 0, chunkEntityCount = chunk.Count; rootEnt < chunkEntityCount; ++rootEnt)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[rootEnt];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value;
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var compData = (byte*) childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                    UnsafeUtility.MemCpy(tempDataPtr, compData + childChunk.IndexInChunk * compSize, compSize);
                                    // 存储组件的 ChangeVersion
                                    childChangeVersions[rootEnt] = childChunk.Chunk.GetChangeVersion(ref ghostChunkComponentTypesPtr[compIdx]);
                                }
                                else
                                {
                                    UnsafeUtility.MemClear(tempDataPtr, compSize);
                                    // 组件数据不存在时将 ChangeVersion 重置为 0
                                    // 如果组件之后出现，就必须将其视为已变化
                                    childChangeVersions[rootEnt] = 0;
                                }
                                tempDataPtr += compSize;
                            }
                        }
                        else
                        {
                            var bufElemSize = ghostSerializer.ComponentSize;
                            var tempDataPtr = dataPtr;

                            for (int rootEnt = 0, chunkEntityCount = chunk.Count; rootEnt < chunkEntityCount; ++rootEnt)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[rootEnt];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value;
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var bufferData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                    // 获取并复制每段 Buffer 数据，在组件备份中记录其长度和备份 Buffer 偏移量
                                    var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(childChunk.IndexInChunk, out var size);
                                    ((int*) tempDataPtr)[0] = size;
                                    ((int*) tempDataPtr)[1] = bufferBackupDataOffset;
                                    if (size > 0)
                                        UnsafeUtility.MemCpy(bufferBackupDataPtr + bufferBackupDataOffset, (byte*) bufferPtr, size * bufElemSize);
                                    bufferBackupDataOffset += size * bufElemSize;
                                    // 存储组件的 ChangeVersion
                                    // GhostSendSystem 从备份恢复组件时会使用此值
                                    childChangeVersions[rootEnt] = childChunk.Chunk.GetChangeVersion(ref ghostChunkComponentTypesPtr[compIdx]);
                                }
                                else
                                {
                                    // 将条目直接重置为 0，此处不使用 MemCpy 更快
                                    ((long*) tempDataPtr)[0] = 0;
                                    // 组件数据不存在时将 ChangeVersion 重置为 0
                                    // 如果组件之后出现，就必须将其视为已变化
                                    childChangeVersions[rootEnt] = 0;
                                }

                                tempDataPtr += compSize;
                            }

                            bufferBackupDataOffset = GhostComponentSerializer.SnapshotSizeAligned(bufferBackupDataOffset);
                        }

                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                        childChangeVersions = PredictionBackupState.GetNextChildChunkVersion(childChangeVersions, chunk.Capacity);
                    }
                }
            }
        }
    }
}
