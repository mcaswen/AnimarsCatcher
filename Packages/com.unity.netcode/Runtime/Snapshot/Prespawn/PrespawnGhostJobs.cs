using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 为 World 中的所有预生成 Ghost 构建 Ghost Baseline
    /// 该 Job 会向 Entity 添加 PrespawnGhostBaseline Buffer
    /// 其中包含 Job 运行时该 Entity 的预序列化 Snapshot
    ///
    /// 注意：序列化不依赖 Component 剥离
    /// 它只依赖由 GhostCollectionSystem 处理的 Ghost 类型 Archetype Serializer 与 Component
    /// 这些内容保证在客户端与服务器保持一致
    /// </summary>
    // Baseline Snapshot 数据布局：
    // -------------------------------------------------------------
    // [COMPONENT 数据][大小][填充（3 个 uint）][动态 Buffer 数据]
    // -------------------------------------------------------------
    [BurstCompile]
    internal struct PrespawnGhostSerializer : IJobChunk
    {
        [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
        [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
        [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
        [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
        [ReadOnly] public ComponentTypeHandle<GhostType> ghostTypeComponentType;
        [ReadOnly] public EntityTypeHandle entityType;
        [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
        [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
        [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
        [ReadOnly] public DynamicTypeList ghostChunkComponentTypes;
        public NativeList<ulong>.ParallelWriter baselineHashes;
        [NativeDisableParallelForRestriction]
        public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaseline;
        public Entity GhostCollectionSingleton;

        public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            // 此 Job 不支持包含可启用 Component 类型的查询
            Assert.IsFalse(useEnabledMask);

            var entities = chunk.GetNativeArray(entityType);
            var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
            var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
            var ghostTypeComponent = chunk.GetNativeArray(ref ghostTypeComponentType)[0];
            int ghostType;
            for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
            {
                if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                    break;
            }
            // 该类型尚未处理完成，此时无法继续序列化
            if (ghostType >= GhostCollection.Length || ghostType >= GhostTypeCollection.Length)
            {
                UnityEngine.Debug.LogError($"Cannot serialize prespawn ghost baselines as the `GhostCollection` didn't correctly process some prefabs. GhostTypeCollection.Length: {GhostTypeCollection.Length}.");
                return;
            }

            var buffersSize = new NativeArray<int>(chunk.Count, Allocator.Temp);
            var ghostChunkComponentTypesPtr = ghostChunkComponentTypes.GetData();
            var helper = new GhostSerializeHelper
            {
                serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                childEntityLookup = childEntityLookup,
                linkedEntityGroupType = linkedEntityGroupType,
                ghostChunkComponentTypesPtrLen = ghostChunkComponentTypes.Length
            };

            var typeData = GhostTypeCollection[ghostType];
            // 收集每个 Entity 及其子 Entity 的 Buffer 大小
            if (GhostTypeCollection[ghostType].NumBuffers > 0)
                helper.GatherBufferSize(chunk, 0, chunk.Count, typeData, ref buffersSize);

            var snapshotSize = typeData.SnapshotSize;
            int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
            int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);
            var snapshotBaseOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));

            var bufferAccessor = chunk.GetBufferAccessor(ref prespawnBaseline);
            var chunkHashes = stackalloc ulong[entities.Length];
            for (int i = 0; i < entities.Length; ++i)
            {
                // 初始化用于容纳 Component 数据的 Baseline Buffer
                var baselineBuffer = bufferAccessor[i];
                // 前 4 个字节记录动态数据大小
                var dynamicDataCapacity = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)) + buffersSize[i];
                baselineBuffer.ResizeUninitialized(snapshotSize + dynamicDataCapacity);
                var baselinePtr = baselineBuffer.GetUnsafePtr();
                var headerSize = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint));
                UnsafeUtility.MemClear(baselinePtr, baselineBuffer.Length);
                helper.changeMaskUints = changeMaskUints;
                helper.snapshotOffset = snapshotBaseOffset;
                helper.snapshotPtr = (byte*) baselinePtr;
                // Prespawn Ghost Baseline 假定动态数据偏移从 Buffer 起点计算，与服务器行为保持一致
                helper.snapshotDynamicHeaderPtr = (byte*)baselinePtr + snapshotSize;
                helper.snapshotDynamicPtr = (byte*)baselinePtr + snapshotSize;
                helper.dynamicSnapshotDataOffset = headerSize;
                helper.snapshotSize = snapshotSize;
                helper.dynamicSnapshotCapacity = baselineBuffer.Length - snapshotSize;
                helper.CopyEntityToSnapshot(chunk, i, typeData, GhostSerializeHelper.ClearOption.DontClear);

                // 计算该 Baseline 的 Hash
                chunkHashes[i] =
                    Unity.Core.XXHash.Hash64((byte*)baselineBuffer.GetUnsafeReadOnlyPtr(), baselineBuffer.Length);
            }
            baselineHashes.AddRangeNoResize(chunkHashes, entities.Length);

            buffersSize.Dispose();
        }
    }

    /// <summary>
    /// 从预生成 Ghost 实例中剥离所有标记为应移除或禁用的运行时 Component
    /// </summary>
    /// <remarks>
    /// 该 Job 使用了并非 SharedStatic 的 TypeManager 内部静态成员，因此不兼容 Burst
    /// </remarks>
    [BurstCompile]
    internal struct PrespawnGhostStripComponentsJob : IJobChunk
    {
        [ReadOnly]public ComponentTypeHandle<GhostType> ghostTypeHandle;
        [ReadOnly]public ComponentLookup<GhostPrefabMetaData> metaDataFromEntity;
        [ReadOnly]public BufferTypeHandle<LinkedEntityGroup> linkedEntityTypeHandle;
        [ReadOnly]public NativeParallelHashMap<GhostType, Entity> prefabFromType;
        public EntityCommandBuffer.ParallelWriter commandBuffer;
        public NetDebug netDebug;
        public byte server;
        public byte isHost;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            // 此 Job 不支持包含可启用 Component 类型的查询
            Assert.IsFalse(useEnabledMask);

            var ghostTypes = chunk.GetNativeArray(ref ghostTypeHandle);
            if (!prefabFromType.TryGetValue(ghostTypes[0], out var ghostPrefabEntity))
            {
                netDebug.LogError("Failed to look up ghost type");
                return;
            }
            // 将 Entity 调整为当前 World 所需的正确版本
            if (!metaDataFromEntity.HasComponent(ghostPrefabEntity))
            {
                netDebug.LogWarning($"Could not find a valid ghost prefab for the ghostType");
                return;
            }

            ref var ghostMetaData = ref metaDataFromEntity[ghostPrefabEntity].Value.Value;
            var linkedEntityBufferAccessor = chunk.GetBufferAccessor(ref linkedEntityTypeHandle);

            for (int index = 0, chunkEntityCount = chunk.Count; index < chunkEntityCount; ++index)
            {
                var linkedEntityGroup = linkedEntityBufferAccessor[index];
                if (server == 1)
                {
                    ref var toRemoveArray = ref ghostMetaData.RemoveOnServerOnlyWorld;
                    if (isHost == 1)
                    {
                        toRemoveArray = ref ghostMetaData.RemoveOnAllServerWorldsSharedList;
                    }
                    for (int rm = 0; rm < ghostMetaData.RemoveOnServerOnlyWorld.Length; ++rm)
                    {
                        var childIndexCompHashPair = ghostMetaData.RemoveOnServerOnlyWorld[rm];
                        var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                        commandBuffer.RemoveComponent(unfilteredChunkIndex, linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                    }
                }
                else
                {
                    for (int rm = 0; rm < ghostMetaData.RemoveOnClientWorlds.Length; ++rm)
                    {
                        var childIndexCompHashPair = ghostMetaData.RemoveOnClientWorlds[rm];
                        var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                        commandBuffer.RemoveComponent(unfilteredChunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                    }
                    // FIXME: 能够在不产生结构变更的情况下禁用后，应改为禁用而不是移除
                    if (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Predicted)
                    {
                        for (int rm = 0; rm < ghostMetaData.DisableOnPredictedClient.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.DisableOnPredictedClient[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            commandBuffer.RemoveComponent(unfilteredChunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                    }
                    else if (ghostMetaData.DefaultMode == GhostPrefabBlobMetaData.GhostMode.Interpolated)
                    {
                        for (int rm = 0; rm < ghostMetaData.DisableOnInterpolatedClient.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.DisableOnInterpolatedClient[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            commandBuffer.RemoveComponent(unfilteredChunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 为所有 Prespawn Ghost 的 GhostInstance 与 GhostCleanup 分配 GhostId
    /// 同时使用全部已生成 Ghost 填充 SpawnedGhostMapping 列表
    /// </summary>
    [BurstCompile]
    internal struct AssignPrespawnGhostIdJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle entityType;
        [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnIndexType;
        [NativeDisableParallelForRestriction]
        public ComponentTypeHandle<GhostInstance> ghostComponentType;
        [NativeDisableParallelForRestriction]
        public ComponentTypeHandle<GhostCleanup> ghostStateTypeHandle;
        [NativeDisableParallelForRestriction]
        public NativeList<SpawnedGhostMapping>.ParallelWriter spawnedGhosts;
        public int startGhostId;
        public NetDebug netDebug;
        public bool isServer;
        [ReadOnly] public ComponentTypeHandle<GhostType> ghostType;
        [ReadOnly] public NativeHashMap<GhostType, int>.ReadOnly GhostTypeToColletionIndex;

        public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            // 此 Job 不支持包含可启用 Component 类型的查询
            Assert.IsFalse(useEnabledMask);

            // 查找 Ghost 类型索引
            int ghostTypeIndex = -1;
            if (isServer) // 客户端会在 Ghost Receive 阶段反序列化该值，因此这里只在服务器处理
            {
                GhostType currentChunkGhostType = ((GhostType*)chunk.GetRequiredComponentDataPtrRO(ref ghostType))[0];
                ghostTypeIndex = GhostTypeToColletionIndex[currentChunkGhostType];
            }

            var entities = chunk.GetNativeArray(entityType);
            var preSpawnedIndices = chunk.GetNativeArray(ref prespawnIndexType);
            var ghostComponents = chunk.GetNativeArray(ref ghostComponentType);
            var ghostStates = chunk.GetNativeArray(ref ghostStateTypeHandle);

            var chunkSpawnedGhostMappings = stackalloc SpawnedGhostMapping[chunk.Count];
            int spawnedGhostCount = 0;
            for (int index = 0, chunkEntityCount = chunk.Count; index < chunkEntityCount; ++index)
            {
                var entity = entities[index];
                // 检查该 Entity 是否已经处理
                if (ghostComponents[index].ghostId != 0)
                {
                    netDebug.LogWarning($"{entity} already has ghostId={ghostComponents[index].ghostId} PreSpawnedGhostIndex={preSpawnedIndices[index].Value}");
                    continue;
                }
                // 对 Prespawn 索引使用类似命名空间的特殊编码
                var ghostId = PrespawnHelper.MakePrespawnGhostId(preSpawnedIndices[index].Value + startGhostId);
                if (ghostStates.IsCreated && ghostStates.Length > 0)
                    ghostStates[index] = new GhostCleanup {ghostId = ghostId, despawnTick = NetworkTick.Invalid, spawnTick = NetworkTick.Invalid};

                chunkSpawnedGhostMappings[spawnedGhostCount++] = new SpawnedGhostMapping
                {
                    ghost = new SpawnedGhost {ghostId = ghostId, spawnTick = NetworkTick.Invalid}, entity = entity
                };
                // GhostType -1 是 Prespawn Ghost 的特殊值，GhostId 确定后会在发送或接收 System 中转换为正确类型
                // Prespawn 使用无效 spawnTick；引用目标 Ghost 的 spawnTick 无效时，该引用始终会尝试解析
                // 这是可行的，因为这类 Despawn 具有高优先级，而且连接建立后不会再创建新的 Prespawn Ghost
                ghostComponents[index] = new GhostInstance {ghostId = ghostId, ghostType = ghostTypeIndex, spawnTick = NetworkTick.Invalid};
            }
            spawnedGhosts.AddRangeNoResize(chunkSpawnedGhostMappings, spawnedGhostCount);
        }
    }
}
