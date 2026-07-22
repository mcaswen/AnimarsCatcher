using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst.Intrinsics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.NetCode
{

    /// <summary>
    /// 将此组件添加到 Ghost 会触发该 Ghost 的预序列化
    /// 预序列化会在常规序列化阶段之前执行部分序列化流程
    /// 并且可以一次处理后供所有连接复用
    /// 如果 Ghost 通常每帧都要发送给多个玩家，且包含子实体或 Buffer 等复杂序列化数据
    /// 此方式可以节省部分 CPU 时间
    /// </summary>
    public struct PreSerializedGhost : IComponentData
    {}

    internal unsafe struct SnapshotPreSerializeData
    {
        public void* Data;
        public int DynamicSize;
        public int Capacity;
        public int DynamicCapacity;
    }
    internal unsafe struct GhostPreSerializer : IDisposable
    {
        public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotData;
        private NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> PreviousSnapshotData;
        private EntityQuery m_Query;

        public GhostPreSerializer(EntityQuery query)
        {
            SnapshotData = new NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData>(1024, Allocator.Persistent);
            PreviousSnapshotData = new NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData>(1024, Allocator.Persistent);
            m_Query = query;
        }
        void CleanupSnapshotData()
        {
            // FIXME：此清理也可以通过 Job 执行
            var chunks = PreviousSnapshotData.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < chunks.Length; ++i)
            {
                if (!SnapshotData.ContainsKey(chunks[i]))
                {
                    // 释放 PreviousSnapshotData 中为此 Key 存储的数据
                    PreviousSnapshotData.TryGetValue(chunks[i], out var snapshot);
                    UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                }
            }
            PreviousSnapshotData.Clear();
            var temp = SnapshotData;
            SnapshotData = PreviousSnapshotData;
            PreviousSnapshotData = temp;
        }
        public void Dispose()
        {
            CleanupSnapshotData();
            var snapshots = PreviousSnapshotData.GetValueArray(Allocator.Temp);
            for (int i = 0; i < snapshots.Length; ++i)
                UnsafeUtility.Free(snapshots[i].Data, Allocator.Persistent);
            PreviousSnapshotData.Dispose();
            SnapshotData.Dispose();
        }

        public JobHandle Schedule(JobHandle dependency,
            BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity,
            BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity,
            BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity,
            Entity GhostCollectionSingleton,
            BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity,
            BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType,
            EntityStorageInfoLookup childEntityLookup,
            ComponentTypeHandle<GhostInstance> ghostComponentType,
            ComponentTypeHandle<GhostType> ghostTypeComponentType,
            EntityTypeHandle entityType,
            ComponentLookup<GhostInstance> ghostFromEntity,
            NativeArray<ConnectionStateData> connectionStateData,
            NetDebug netDebug,
            NetworkTick currentTick,
            int useCustomSerializer,
            ref SystemState system,
            DynamicBuffer<GhostCollectionComponentType> ghostCollection)
        {
            CleanupSnapshotData();
            var job = new GhostPreSerializeJob
            {
                SnapshotData = SnapshotData.AsParallelWriter(),
                PreviousSnapshotData = PreviousSnapshotData,
                GhostComponentCollectionFromEntity = GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = GhostComponentIndexFromEntity,
                GhostCollectionSingleton = GhostCollectionSingleton,
                GhostCollectionFromEntity = GhostCollectionFromEntity,
                entityType = entityType,
                linkedEntityGroupType = linkedEntityGroupType,
                childEntityLookup = childEntityLookup,
                ghostComponentType = ghostComponentType,
                ghostTypeComponentType = ghostTypeComponentType,
                ghostFromEntity = ghostFromEntity,
                connectionStateData = connectionStateData,
                netDebug = netDebug,
                currentTick = currentTick,
                useCustomSerializer = useCustomSerializer
            };
            DynamicTypeList.PopulateList(ref system, ghostCollection, true, ref job.dynamicTypeList);
            return job.ScheduleParallelByRef(m_Query, dependency);
        }

        [BurstCompile]
        struct GhostPreSerializeJob : IJobChunk
        {
            [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> PreviousSnapshotData;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostType> ghostTypeComponentType;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] public NativeArray<ConnectionStateData> connectionStateData;
            [ReadOnly] public EntityTypeHandle entityType;

            public NetDebug netDebug;
            public NetworkTick currentTick;
            public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData>.ParallelWriter SnapshotData;
            public Entity GhostCollectionSingleton;
            public DynamicTypeList dynamicTypeList;
            public int useCustomSerializer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                if(connectionStateData.Length == 0)
                    return;
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var ghosts = chunk.GetNativeArray(ref ghostComponentType);
                // 序列化 Chunk 数据前，需要检查 Ghost 是否已经处理
                // 查找此 Chunk 的 Ghost 类型
                var ghostType = ghosts[0].ghostType;
                // 预生成 Ghost 可能尚无正确的 Ghost 类型索引，因此在此为其计算
                // 这种情况几乎不应发生，因为所有预生成 Ghost 都会在场景加载后初始化
                // 除非错误阻止 GhostCollectionSystem 正确处理并初始化 Ghost Prefab
                // 否则服务器应一次初始化全部预生成 Ghost
                if (ghostType < 0)
                {
                    var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    var ghostTypeComponent = chunk.GetNativeArray(ref ghostTypeComponentType)[0];
                    for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                    {
                        if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                            break;
                    }

                    if (ghostType >= GhostCollection.Length)
                    {
                        netDebug.LogError($"Could not find ghost type {(Hash128)ghostTypeComponent} for a pre-spawned ghosts in the GhostCollectionPrefab list. This usually indicates the GhostCollection has not been able to process the ghost prefab or the prefab entity has not been loaded as depedency or has been deleted. Please check for error log in relation to the GhostCollectionSystem.");
                        return;
                    }
                    // 已检测到 Prefab，但尚未建立 GhostCollectionPrefabSerializer 条目，可能原因只有以下两种
                    // - 初始化另一个 Prefab 时发生错误
                    // - 服务器因游戏中已无连接而重置集合
                    if (ghostType >= GhostTypeCollection.Length)
                    {
                        netDebug.LogError($"Could not find ghost type {(Hash128)ghostTypeComponent} in the GhostCollectionPrefabSerializer list. The ghost prefab has been detected by the GhostCollectionSystem but the serialization data has not being initialized. That usually indicates some error during the initialization of the serialization data for some other prefab type. Please check for error log in relation to the GhostCollectionSystem.");
                        return;
                    }
                }
                // 如果 Chunk 不属于预生成内容且 Ghost 的 Spawn Tick 无效，说明该 Chunk 刚刚生成
                // 当前信息不足以处理，因此跳过此 Chunk
                else if(!ghosts[0].spawnTick.IsValid)
                    return;

                // 该类型不在集合中，虽然属于边界情况，但在特定场景下仍可能发生
                if(ghostType >= GhostTypeCollection.Length)
                    return;

                // 如果存在 Spawn Tick 为 0 等无效实体，则需要进一步防御
                // 服务器已经执行更新后，理论上不应有无效 Ghost 到达此处
                var typeData = GhostTypeCollection[ghostType];
                int dynamicDataCapacity = 0;
                int dynamicDataHeaderSize = 0;

                var helper = new GhostSerializeHelper
                {
                    serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                    ghostChunkComponentTypesPtr = dynamicTypeList.GetData(),
                    GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                    GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    ghostChunkComponentTypesPtrLen = dynamicTypeList.Length,
                };

                if (typeData.NumBuffers != 0)
                {
                    // 计算 Buffer 动态数据所需空间
                    dynamicDataCapacity = helper.GatherBufferSize(chunk, 0, chunk.Count, typeData);
                    dynamicDataHeaderSize = GhostChunkSerializationState.GetDynamicDataHeaderSize(chunk.Capacity);
                }
                int snapshotDataCapacity = typeData.SnapshotSize * chunk.Capacity;
                // 确定所需分配大小
                if (!PreviousSnapshotData.TryGetValue(chunk, out var snapshot) || snapshot.Capacity != snapshotDataCapacity || snapshot.DynamicCapacity < dynamicDataCapacity)
                {
                    // 分配新的 Snapshot
                    if (snapshot.Data != null)
                    {
                        UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                    }
                    snapshot.Capacity = snapshotDataCapacity;
                    // 向上取整到整数 KB
                    snapshot.DynamicCapacity = (dynamicDataCapacity + 1023) & (~1023);
                    snapshot.Data = UnsafeUtility.Malloc(snapshot.Capacity + snapshot.DynamicCapacity, 16, Allocator.Persistent);
                }
                snapshot.DynamicSize = dynamicDataCapacity;
                // 加入新的 Snapshot 数据查找表
                if (!SnapshotData.TryAdd(chunk, snapshot))
                {
                    netDebug.LogError("Could not register snapshot data for pre-serialization");
                    UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                    return;
                }

                typeData.profilerMarker.Begin();
                int snapshotSize = typeData.SnapshotSize;
                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);
                int snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));
                // 遍历所有实体并将数据序列化到 Snapshot 存储区
                helper.snapshotPtr = (byte*)snapshot.Data;
                helper.snapshotOffset = snapshotOffset;
                helper.snapshotSize = snapshotSize;
                helper.changeMaskUints = changeMaskUints;
                if (typeData.NumBuffers != 0)
                {
                    // 此处需要说明预序列化 Ghost Snapshot 的数据布局
                    //
                    //   snapshot.Capacity   snapshot.DynamicCapacity
                    // [  SNAPSHOT 数据  ][       动态数据          ]
                    //
                    // GhostChunkSerializer 会复制并重定位动态数据
                    // 将其放入 Chunk 的 DynamicSnapshotBuffer 中，紧跟在 Header 之后
                    //
                    //   Chunk Snapshot 容量      Chunk 动态数据容量
                    // [   SNAPSHOT 数据    ][ HEADER][      动态数据     ]
                    //
                    // 因此，Snapshot 数据中用于指示 Dynamic Buffer 内容起点的相对偏移量
                    // 必须再加上动态 Header 的大小
                    //
                    //  [Snapshot 数据]
                    // ..  Buffer ...                Chunk 动态数据
                    //   长度, 偏移量                 [Header][内容]
                    //    X     |                                 |
                    //          |_________________________________|
                    //
                    // 预序列化数据实际紧跟在 Snapshot 之后存储
                    // 但辅助器会写入 snapshotDynamicPtr + dynamicSnapshotDataOffset 地址
                    // 因此将 Buffer 起始位置向前偏移一个 Header 容量
                    helper.snapshotDynamicPtr = (byte*)snapshot.Data + snapshot.Capacity - dynamicDataHeaderSize;
                    helper.dynamicSnapshotDataOffset = dynamicDataHeaderSize;
                    // 最大容量也必须包含 Header 大小，确保所有偏移量计算保持一致
                    helper.dynamicSnapshotCapacity = snapshot.DynamicCapacity + dynamicDataHeaderSize;
                }
                if (useCustomSerializer != 0 && typeData.CustomPreSerializer.Ptr.IsCreated)
                {
                    var context = new GhostPrefabCustomSerializer.Context
                    {
                        startIndex = 0,
                        endIndex = chunk.Count,
                        ghostType = ghostType,
                        childEntityLookup = helper.childEntityLookup,
                        serializerState = helper.serializerState,
                        snapshotDataPtr = (IntPtr)helper.snapshotPtr,
                        snapshotDynamicDataPtr = (IntPtr)helper.snapshotDynamicPtr,
                        snapshotOffset = helper.snapshotOffset,
                        snapshotStride = helper.snapshotSize,
                        dynamicDataOffset = helper.dynamicSnapshotDataOffset,
                        dynamicDataCapacity = helper.dynamicSnapshotCapacity,
                        ghostChunkComponentTypes = (IntPtr)helper.ghostChunkComponentTypesPtr,
                        linkedEntityGroupTypeHandle = helper.linkedEntityGroupType,
                        // 与预序列化无关的数据
                        // networkId = default,
                        // hasPreserializedData = default,
                        // entityStartBit = default,
                        // baselinePerEntityPtr = default,
                        // sameBaselinePerEntityPtr = default,
                        // dynamicDataSizePerEntityPtr = default,
                        // zeroBaseline = default,
                        // ghostInstances = default,
                    };
                    typeData.CustomPreSerializer.Ptr.Invoke(chunk, typeData, helper.GhostComponentIndex, ref context);
                }
                else
                {
                    helper.CopyChunkToSnapshot(chunk, typeData);
                }
                for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                {
                    *(uint*)((byte*)snapshot.Data + snapshotSize * ent) = currentTick.SerializedData;
                }
                typeData.profilerMarker.End();
            }
        }
    }
}
