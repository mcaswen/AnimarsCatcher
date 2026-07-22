#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 通过 ID 和生成时间唯一标识 Ghost 的结构
    /// </summary>
    public struct SpawnedGhost : IEquatable<SpawnedGhost>
    {
        /// <summary>
        /// 服务器分配给 Ghost 的 ID
        /// </summary>
        public int ghostId;
        /// <summary>
        /// 服务器生成 Ghost 时的 Tick
        /// </summary>
        public NetworkTick spawnTick;
        /// <summary>
        /// 生成 SpawnedGhost 的 Hash Code
        /// </summary>
        /// <returns>Ghost ID 对应的 Hash Code</returns>
        public override int GetHashCode()
        {
            return ghostId;
        }
        /// <summary>
        /// 根据 <see cref="GhostInstance"/> 构造 SpawnedGhost
        /// </summary>
        /// <param name="ghostInstance">用于构造 SpawnedGhost 的 Ghost 实例</param>
        public SpawnedGhost(in GhostInstance ghostInstance)
        {
            ghostId = ghostInstance.ghostId;
            spawnTick = ghostInstance.spawnTick;
        }
        /// <summary>
        /// 使用 Ghost 标识符和生成 Tick 构造 SpawnedGhost
        /// </summary>
        /// <param name="ghostId">Ghost ID 值</param>
        /// <param name="spawnTick">生成 Tick</param>
        public SpawnedGhost(int ghostId, NetworkTick spawnTick)
        {
            this.ghostId = ghostId;
            this.spawnTick = spawnTick;
        }
        /// <summary>
        /// ID 和 Tick 均匹配时，两个 SpawnedGhost 相同
        /// </summary>
        /// <param name="ghost">要比较的 Ghost</param>
        /// <returns>Ghost ID 和生成 Tick 是否相同</returns>
        public bool Equals(SpawnedGhost ghost)
        {
            return ghost.ghostId == ghostId && ghost.spawnTick == spawnTick;
        }
    }
    internal struct SpawnedGhostMapping
    {
        public SpawnedGhost ghost;
        public Entity entity;
        public Entity previousEntity;
    }
    internal struct NonSpawnedGhostMapping
    {
        public int ghostId;
        public Entity entity;
    }

    /// <summary>
    /// 用于向 Ghost 组件 Serializer 传递参数的互操作结构，参见 <see cref="GhostComponentSerializer"/>
    /// </summary>
    public struct GhostDeserializerState
    {
        /// <summary>
        /// 为每个已生成 Ghost 存储实体引用的映射
        /// </summary>
        public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly GhostMap;
        /// <summary>
        /// 当前正在反序列化的服务器 Tick
        /// </summary>
        public NetworkTick SnapshotTick;
        /// <summary>
        /// 拥有该 Ghost 的客户端 NetworkId，前提是 Ghost 具有 <see cref="NetCode.GhostOwner"/>
        /// </summary>
        public int GhostOwner;
        /// <summary>
        /// <para>- 设为 <see cref="SendToOwnerType.SendToOwner"/> 时
        /// 仅当 <see cref="GhostOwner"/> 等于当前客户端 NetworkId 才反序列化组件</para>
        /// <para>- 设为 <see cref="SendToOwnerType.SendToNonOwner"/> 时
        /// 仅当 <see cref="GhostOwner"/> 不等于当前客户端 NetworkId 才反序列化组件</para>
        /// </summary>
        public SendToOwnerType SendToOwner;
    }

    /// <summary>
    /// <para>
    /// 仅存在于客户端 World，负责接收并解码服务器发送的 Ghost Snapshot
    /// </para>
    /// <para>
    /// 收到新 Snapshot 后，系统会开始解码数据包协议并提取以下内容
    /// </para>
    /// <para>- 需要销毁的 Ghost 列表</para>
    /// <para>- 每个序列化 Ghost 的增量压缩或未压缩状态</para>
    /// <para>
    /// 系统分别通过 <see cref="GhostSpawnBuffer"/> 和 <see cref="GhostDespawnQueues"/>
    /// 安排 Ghost 生成与销毁请求
    /// </para>
    /// <para>
    /// 已生成 Ghost 收到新的状态 Snapshot 时，参见 <see cref="SpawnedGhostEntityMap"/>
    /// 其状态会被反序列化并添加到实体的 <see cref="SnapshotDataBuffer"/> 历史 Buffer
    /// </para>
    /// <para>
    /// 收到的 Snapshot 会记录到 <see cref="NetworkSnapshotAck"/>
    /// 客户端随后通过 Command 流将最新收到的 Snapshot 信息发回服务器
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    [UpdateAfter(typeof(NetDebugSystem))]
    [BurstCompile]
    public unsafe partial struct GhostReceiveSystem : ISystem
    {
        EntityQuery m_ConnectionsQuery;
        EntityQuery m_GhostCleanupQuery;
        EntityQuery m_SubSceneQuery;

        NativeParallelHashMap<int, Entity> m_GhostEntityMap;
        NativeParallelHashMap<SpawnedGhost, Entity> m_SpawnedGhostEntityMap;
        NativeList<byte> m_TempDynamicData;

        NativeArray<int> m_GhostCompletionCount;
        StreamCompressionModel m_CompressionModel;
        static readonly Unity.Profiling.ProfilerMarker k_Scheduling = new Unity.Profiling.ProfilerMarker("GhostUpdateSystem_Scheduling");

        EntityTypeHandle m_EntityTypeHandle;
        ComponentLookup<SnapshotData> m_SnapshotDataFromEntity;
        ComponentLookup<NetworkSnapshotAck> m_SnapshotAckFromEntity;
        ComponentLookup<PredictedGhost> m_PredictedFromEntity;
        ComponentLookup<GhostInstance> m_GhostFromEntity;
        ComponentLookup<GhostOwner> m_GhostOwnerFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
#if NETCODE_DEBUG
        ComponentLookup<PrefabDebugName> m_PrefabNamesFromEntity;
        FixedString512Bytes m_LogFolder;
#endif
        ComponentLookup<EnablePacketLogging> m_EnableLoggingFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentCollectionFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostTypeCollectionFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostComponentIndexFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionFromEntity;
        BufferLookup<IncomingSnapshotDataStreamBuffer> m_SnapshotFromEntity;
        BufferLookup<SnapshotDataBuffer> m_SnapshotDataBufferFromEntity;
        BufferLookup<SnapshotDynamicDataBuffer> m_SnapshotDynamicDataFromEntity;
        BufferLookup<GhostSpawnBuffer> m_GhostSpawnBufferFromEntity;
        BufferLookup<PrespawnGhostBaseline> m_PrespawnBaselineBufferFromEntity;

#if NETCODE_DEBUG
        PacketDumpLogger m_NetDebugPacket;
#endif

        // 由于调用 NetDebugInterop.Initialize，此方法无法使用 Burst 编译
        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
#if NETCODE_DEBUG
            m_LogFolder = NetDebug.LogFolderForPlatform();
            NetDebugInterop.Initialize();
#endif
            m_GhostEntityMap = new NativeParallelHashMap<int, Entity>(2048, Allocator.Persistent);
            m_SpawnedGhostEntityMap = new NativeParallelHashMap<SpawnedGhost, Entity>(2048, Allocator.Persistent);
            m_GhostCompletionCount = new NativeArray<int>(3, Allocator.Persistent);

            var componentTypes = new NativeArray<ComponentType>(1, Allocator.Temp);
            componentTypes[0] = ComponentType.ReadWrite<SpawnedGhostEntityMap>();
            var spawnedGhostMap = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(componentTypes));
            componentTypes[0] = ComponentType.ReadWrite<GhostCount>();
            var ghostCompletionCount = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(componentTypes));

            FixedString64Bytes spawnedGhostMapName = "SpawnedGhostEntityMapSingleton";
            state.EntityManager.SetName(spawnedGhostMap, spawnedGhostMapName);
            SystemAPI.SetSingleton(new SpawnedGhostEntityMap{Value = m_SpawnedGhostEntityMap.AsReadOnly(), SpawnedGhostMapRW = m_SpawnedGhostEntityMap, ClientGhostEntityMap = m_GhostEntityMap});

            FixedString64Bytes ghostCompletionCountName = "GhostCountSingleton";
            state.EntityManager.SetName(ghostCompletionCount, ghostCompletionCountName);
            SystemAPI.SetSingleton(new GhostCount(m_GhostCompletionCount));

            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamConnection, NetworkStreamInGame>();
            m_ConnectionsQuery = state.GetEntityQuery(builder);

            builder.Reset();
            builder.WithAll<GhostInstance>()
                .WithNone<PreSpawnedGhostIndex>();
            m_GhostCleanupQuery = state.GetEntityQuery(builder);

            builder.Reset();
            builder.WithAll<SubSceneWithGhostCleanup>();
            m_SubSceneQuery = state.GetEntityQuery(builder);

            m_CompressionModel = StreamCompressionModel.Default;

            state.RequireForUpdate<GhostCollection>();

            m_TempDynamicData = new NativeList<byte>(Allocator.Persistent);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SnapshotDataFromEntity = state.GetComponentLookup<SnapshotData>();
            m_SnapshotAckFromEntity = state.GetComponentLookup<NetworkSnapshotAck>();
            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            m_PredictedFromEntity = state.GetComponentLookup<PredictedGhost>(true);
            m_GhostFromEntity = state.GetComponentLookup<GhostInstance>();
            m_GhostOwnerFromEntity = state.GetComponentLookup<GhostOwner>(true);
#if NETCODE_DEBUG
            m_PrefabNamesFromEntity = state.GetComponentLookup<PrefabDebugName>(true);
#endif
            m_EnableLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>(false);
            m_GhostComponentCollectionFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostTypeCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefab>();
            m_SnapshotFromEntity = state.GetBufferLookup<IncomingSnapshotDataStreamBuffer>();
            m_SnapshotDataBufferFromEntity = state.GetBufferLookup<SnapshotDataBuffer>();
            m_SnapshotDynamicDataFromEntity = state.GetBufferLookup<SnapshotDynamicDataBuffer>();
            m_GhostSpawnBufferFromEntity = state.GetBufferLookup<GhostSpawnBuffer>();
            m_PrespawnBaselineBufferFromEntity = state.GetBufferLookup<PrespawnGhostBaseline>(true);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;
            state.CompleteDependency(); // 确保可以访问 Ghost 映射
            m_GhostEntityMap.Dispose();
            m_SpawnedGhostEntityMap.Dispose();

            m_GhostCompletionCount.Dispose();
            m_TempDynamicData.Dispose();
#if NETCODE_DEBUG
            m_NetDebugPacket.Dispose();
#endif

        }

        [BurstCompile]
        struct ClearGhostsJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter CommandBuffer;
            [ReadOnly] public EntityTypeHandle EntitiesType;

            public void LambdaMethod(Entity entity, int index)
            {
                CommandBuffer.DestroyEntity(index, entity);
            }

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用组件类型的查询
                Assert.IsFalse(useEnabledMask);

                var entities = chunk.GetNativeArray(EntitiesType);
                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    LambdaMethod(entities[i], unfilteredChunkIndex);
                }
            }
        }

        [BurstCompile]
        struct ClearMapJob : IJob
        {
            public NativeParallelHashMap<int, Entity> GhostMap;
            public NativeParallelHashMap<SpawnedGhost, Entity> SpawnedGhostMap;

            public void Execute()
            {
                // Ghost 映射不应清除预生成 Ghost，因为客户端连接不在游戏中时不会销毁它们
                // 预生成系统负责填充这些条目，因此也应由它负责重置
                var keys = SpawnedGhostMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; ++i)
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(keys[i].ghostId))
                    {
                        GhostMap.Remove(keys[i].ghostId);
                        SpawnedGhostMap.Remove(keys[i]);
                    }
                }

                // 修复：部分 Ghost 尚未生成，但仍需从映射中移除
                // 否则会报告映射中的 Ghost 没有关联实体，客户端删除 Ghost 实体时可能出现此错误
                var ghostMapKeys = GhostMap.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < ghostMapKeys.Length; ++i)
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghostMapKeys[i]))
                    {
                        GhostMap.Remove(ghostMapKeys[i]);
                    }
                }
            }
        }

        [BurstCompile]
        struct ReadStreamJob : IJob
        {
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;

            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> m_GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> m_GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> m_GhostComponentIndex;

            public NativeList<Entity> Connections;
            public BufferLookup<IncomingSnapshotDataStreamBuffer> SnapshotFromEntity;
            public BufferLookup<SnapshotDataBuffer> SnapshotDataBufferFromEntity;
            public BufferLookup<SnapshotDynamicDataBuffer> SnapshotDynamicDataFromEntity;
            public BufferLookup<GhostSpawnBuffer> GhostSpawnBufferFromEntity;
            [ReadOnly]public BufferLookup<PrespawnGhostBaseline> PrespawnBaselineBufferFromEntity;
            public ComponentLookup<SnapshotData> SnapshotDataFromEntity;
            public ComponentLookup<NetworkSnapshotAck> SnapshotAckFromEntity;
            [ReadOnly]public ComponentLookup<NetworkId> NetworkIdFromEntity;
            [ReadOnly]public ComponentLookup<GhostOwner> GhostOwnerFromEntity;
            public NativeParallelHashMap<int, Entity> GhostEntityMap;
            public NativeHashMap<GhostType, int> PendingGhostPrefabAssignment;
            public StreamCompressionModel CompressionModel;
#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<UnsafeGhostStatsSnapshot> SnapshotStatsWriters;
#endif
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> InterpolatedDespawnQueue;
            public NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> PredictedDespawnQueue;
            public NativeQueue<OwnerSwithchingEntry> OwnerPredictedSwitchQueue;
            [ReadOnly] public ComponentLookup<PredictedGhost> PredictedFromEntity;
            public ComponentLookup<GhostInstance> GhostFromEntity;
            public byte IsThinClient;

            public EntityCommandBuffer CommandBuffer;
            public Entity GhostSpawnEntity;
            public NativeArray<int> GhostCompletionCount;
            public NativeList<byte> TempDynamicData;
            public NativeList<SubSceneWithGhostCleanup> PrespawnSceneStateArray;

            public NetDebug NetDebug;
            public FixedString128Bytes WorldName;
#if NETCODE_DEBUG
            public PacketDumpLogger NetDebugPacket;
            [ReadOnly] public ComponentLookup<PrefabDebugName> PrefabNamesFromEntity;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<EnablePacketLogging> EnableLoggingFromEntity;
            public FixedString512Bytes DebugLog;
#endif

            public void Execute()
            {
#if UNITY_EDITOR || NETCODE_DEBUG
                ref var netStatsSnapshot = ref SnapshotStatsWriters.AsSpan()[0]; // 这是 IJob，因此只会使用一个线程
#endif
#if NETCODE_DEBUG
                EnablePacketLogging.InitAndFetch(Connections[0], EnableLoggingFromEntity, NetDebugPacket);
#endif

                m_GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                m_GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                m_GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                // FIXME：应支持任意数量的连接，并为每个连接维护独立 Ghost 映射
                CheckConnectionCountIsValid();
                var snapshot = SnapshotFromEntity[Connections[0]];
                if (snapshot.Length == 0)
                    return;

                // 计算用于提取增量压缩 Buffer 元素的临时 Buffer 大小
                int maxDynamicSnapshotSize = 0;
                for (int i = 0; i < m_GhostTypeCollection.Length; ++i)
                    maxDynamicSnapshotSize = math.max(maxDynamicSnapshotSize, m_GhostTypeCollection[i].MaxBufferSnapshotSize);
                TempDynamicData.Resize(maxDynamicSnapshotSize,NativeArrayOptions.ClearMemory);

                var dataStream = snapshot.AsDataStreamReader();
                // 读取 Ghost 流
                // 查找需要生成或销毁的实体
                var serverTick = new NetworkTick{SerializedData = dataStream.ReadUInt()};
                ref var ack = ref SnapshotAckFromEntity.GetRefRW(Connections[0]).ValueRW;

                // 加载所有新 Prefab
                uint numPrefabs = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                // TODO：将 CurrentSnapshotSequenceId 直接映射到 Snapshot 数据本身，而不是在此间接获取
                if(NetDebugPacket.IsCreated)
                    DebugLog.Append((FixedString128Bytes)$"SnapshotTick:{serverTick.ToFixedString()} SSId:{ack.CurrentSnapshotSequenceId} NewPrefabs: {numPrefabs}\n");
#endif
                if (numPrefabs > 0)
                {
                    var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    // 服务器只发送尚未确认的 Ghost 类型，确认需要一个 RTT
                    // 因此需要检查服务器列表中首先包含的是哪个 Prefab
                    int firstPrefab = (int)dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" FirstPrefab: {0}\n", firstPrefab));
#endif
                    for (int i = 0; i < numPrefabs; ++i)
                    {
                        GhostType type;
                        ulong hash;
                        type.guid0 = dataStream.ReadUInt();
                        type.guid1 = dataStream.ReadUInt();
                        type.guid2 = dataStream.ReadUInt();
                        type.guid3 = dataStream.ReadUInt();
                        hash = dataStream.ReadULong();
#if NETCODE_DEBUG
                        if (NetDebugPacket.IsCreated)
                            DebugLog.Append((FixedString512Bytes)$"\t {type.guid0}-{type.guid1}-{type.guid2}-{type.guid3} Hash:{hash}");
#endif
                        if (firstPrefab+i == ghostCollection.Length)
                        {
                            // 跟踪等待分配的条目
                            PendingGhostPrefabAssignment.Add(type, ghostCollection.Length);
                            // 标记 PendingGhostPrefabAssignment 列表以及后续 Ghost 集合列表已修改
                            // TODO：也可以通过跟踪 ghostCollection 自身长度实现
                            PendingGhostPrefabAssignment[default] = 1;
                            // 此处只添加类型，Prefab 实体由 GhostCollectionSystem 填充
                            ghostCollection.Add(new GhostCollectionPrefab{GhostType = type, GhostPrefab = Entity.Null, Hash = hash, Loading = GhostCollectionPrefab.LoadingState.NotLoading});
                        }
                        else if (type != ghostCollection[firstPrefab+i].GhostType || hash != ghostCollection[firstPrefab+i].Hash)
                        {
                            LogDeserializeFailure(LogType.Error, $"Ghost list item {firstPrefab + i} was modified (Hash {ghostCollection[firstPrefab + i].Hash} -> {hash})!");
                            CommandBuffer.AddComponent(Connections[0], new NetworkStreamRequestDisconnect{Reason = NetworkStreamDisconnectReason.BadProtocolVersion});
                            return;
                        }
                    }
                }

                if (IsThinClient == 1)
                {
                    snapshot.Clear();
                    return;
                }

                uint relevantGhostCount = dataStream.ReadPackedUInt(CompressionModel);
                uint despawnLen = dataStream.ReadUShort();
                uint numEntitiesUpdated = dataStream.ReadUShort();

                // 注意：服务器在确定新 Chunk 实际包含多少相关 Ghost 前，会将其相关 Ghost 数计为 0
                // 因此要确保 relevantGhostCount 至少等于当前 Snapshot 中包含的 Ghost 数量
                // 此估算并不完美，但实现最简单
                GhostCompletionCount[0] = (int)math.max(relevantGhostCount, numEntitiesUpdated);

#if NETCODE_DEBUG
                if(NetDebugPacket.IsCreated)
                    DebugLog.Append(FixedString.Format("\t TotalGhostCount:{0} Despawns:{1} Updates:{2}\n", relevantGhostCount, despawnLen, numEntitiesUpdated));
#endif

                var data = default(DeserializeData);
#if UNITY_EDITOR || NETCODE_DEBUG
                data.StartPos = dataStream.GetBitsRead();
#endif
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated && despawnLen > 0)
                    DebugLog.Append((FixedString32Bytes)"\t[Despawn GIDs]");
#endif
                int nextExpectedGhostId = PendingGhostDespawn.k_ExpectedGhostIdDelta;
                for (var i = 0; i < despawnLen; ++i)
                {
                    var ghostId = dataStream.ReadPackedIntDelta(nextExpectedGhostId, CompressionModel);
                    nextExpectedGhostId = ghostId + PendingGhostDespawn.k_ExpectedGhostIdDelta;
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" {0}", ghostId));
#endif
                    if (!GhostEntityMap.TryGetValue(ghostId, out var ent))
                        continue;

                    GhostEntityMap.Remove(ghostId);

                    if (!GhostFromEntity.TryGetComponent(ent, out var ghostInstance))
                    {
                        NetDebug.LogError($"Trying to despawn a ghost (GID:{ghostId}, {ent.ToFixedString()}) which is in the ghost map but does not have a ghost component. This can happen if you manually delete a ghost on the client.");
                        continue;
                    }

                    if (PredictedFromEntity.HasComponent(ent))
                        PredictedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostInstance.spawnTick}, tick = serverTick});
                    else
                        InterpolatedDespawnQueue.Enqueue(new GhostDespawnSystem.DelayedDespawnGhost
                            {ghost = new SpawnedGhost{ghostId = ghostId, spawnTick = ghostInstance.spawnTick}, tick = serverTick});
                }

#if UNITY_EDITOR || NETCODE_DEBUG
                PacketDumpFlush();
                data.CurPos = dataStream.GetBitsRead();
                netStatsSnapshot.Tick = serverTick;
                netStatsSnapshot.DespawnCount = despawnLen;
                netStatsSnapshot.DestroySizeInBits = (uint) (dataStream.GetBitsRead() - data.StartPos);
                data.StartPos = data.CurPos;
                data.NetStatsSnapshot = (UnsafeGhostStatsSnapshot*)UnsafeUtility.AddressOf(ref netStatsSnapshot);
#endif

                bool dataValid = true;
                for (var i = 0; i < numEntitiesUpdated && dataValid; ++i)
                {
                    dataValid &= DeserializeEntity(serverTick, ref dataStream, ref data, i);
                }
#if UNITY_EDITOR || NETCODE_DEBUG
                if (data.StatCount > 0)
                {
                    data.CurPos = dataStream.GetBitsRead();
                    int statType = (int) data.TargetArch; // Ghost 集合中的索引，用于标识当前 Ghost 类型，即当前 Prefab
                    ref var perGhostTypeStats = ref netStatsSnapshot.PerGhostTypeStatsListRefRW.ElementAt(statType);
                    perGhostTypeStats.EntityCount += data.StatCount;
                    perGhostTypeStats.SizeInBits += (uint) (data.CurPos - data.StartPos);
                    perGhostTypeStats.UncompressedCount += data.UncompressedCount;
                }
#endif

                // 检查实际读取量是否与预期完全一致
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if(dataValid)
                {
                    dataStream.Flush(); // DataStream 只能发送完整字节，因此将位读取器对齐到下一个完整字节
                    var bitsRead = dataStream.GetBitsRead();
                    var lengthInBits = dataStream.Length * 8;
                    var delta = bitsRead - lengthInBits;
                    if (delta != 0 || dataStream.HasFailedReads)
                    {
                        dataValid = false;
                        LogDeserializeFailure(LogType.Error, $"Read {bitsRead} bits, expected {lengthInBits} bits (delta:{delta}) in received snapshot:{serverTick.ToFixedString()}!");
                    }
                }
#endif

                while (GhostEntityMap.Capacity < GhostEntityMap.Count() + data.NewGhosts)
                    GhostEntityMap.Capacity += 1024;

                snapshot.Clear();

                GhostCompletionCount[1] = GhostEntityMap.Count();

                if (!dataValid)
                {
                    // 当前 Snapshot 反序列化失败，说明状态异常，因此将之前所有 Ack 标记为无效
                    ack.ReceivedSnapshotByLocalMask = 0;
                    ack.LastReceivedSnapshotByLocal = NetworkTick.Invalid;

                    // TODO：研究是否可以只撤销无效 Snapshot 的 Ack，而不是撤销此前全部 Ack
                    // 全量撤销会导致所有相关 Ghost Chunk 必须重新发送给此客户端
                    // 例如出现 Baseline 错误时，可尝试仅撤销读到的无效 Baseline Tick
                    // 只有最坏情况下才撤销全部 Ack
                    // 当前粗暴方案的优点是服务器随后可以立即发送正确数据
                    // 若不使用该方案，就需要逐个撤销无效 Baseline Ack，耗时更长且可能产生大量错误日志
                }
            }
            struct DeserializeData
            {
                public uint TargetArch; // Ghost 类型集合中的索引，TODO：考虑用专用类型封装此整数
                public uint TargetArchLen;
                public int BaseGhostId;
                public NetworkTick BaseSpawnTick;
                public NetworkTick BaselineTick;
                public NetworkTick BaselineTick2;
                public NetworkTick BaselineTick3;
                public uint BaselineLen;
                public int NewGhosts;
#if UNITY_EDITOR || NETCODE_DEBUG
                public int StartPos;
                public int CurPos;
                public uint StatCount;
                public uint UncompressedCount;
                public UnsafeGhostStatsSnapshot* NetStatsSnapshot;
#endif
            }

            bool DeserializeEntity(NetworkTick serverTick, ref DataStreamReader dataStream, ref DeserializeData data, int ent)
            {
                if (data.TargetArchLen == 0)
                {
#if UNITY_EDITOR || NETCODE_DEBUG
                    data.CurPos = dataStream.GetBitsRead();
                    if (data.StatCount > 0)
                    {
                        int statType = (int)data.TargetArch;
                        ref var netStatsSnapshot = ref SnapshotStatsWriters.AsSpan()[0];
                        ref var perGhostTypeStats = ref netStatsSnapshot.PerGhostTypeStatsListRefRW.ElementAt(statType);
                        perGhostTypeStats.EntityCount += data.StatCount;
                        perGhostTypeStats.SizeInBits += (uint)(data.CurPos - data.StartPos);
                        perGhostTypeStats.UncompressedCount += data.UncompressedCount;
                    }

                    data.StartPos = data.CurPos;
                    data.StatCount = 0;
                    data.UncompressedCount = 0;
#endif
                    data.TargetArch = dataStream.ReadPackedUInt(CompressionModel);
                    data.TargetArchLen = dataStream.ReadPackedUInt(CompressionModel);
                    data.BaseGhostId = dataStream.ReadRawBits(1) != 0 ? unchecked((int)PrespawnHelper.PrespawnGhostIdBase) : 1;
                    data.BaseSpawnTick = serverTick;
                    data.BaseSpawnTick.Decrement();

                    if (data.TargetArch >= m_GhostTypeCollection.Length)
                    {
                        LogDeserializeFailure(LogType.Error, FixedString.Format("Received invalid GhostType from the server:{0}/{1} RelevantGhostCount:{2}!", data.TargetArch, m_GhostTypeCollection.Length, data.TargetArchLen));
                        return false;
                    }
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format("\t GhostType:{0} RelevantGhostCount:{1}\n", GetGhostTypePrefabName(data), data.TargetArchLen));
#endif
                }

                --data.TargetArchLen;

                if (data.BaselineLen == 0)
                {
                    var baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick = serverTick;
                        data.BaselineTick.Subtract(baselineDelta);
                    }
                    baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick2 = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick2 = serverTick;
                        data.BaselineTick2.Subtract(baselineDelta);
                    }
                    baselineDelta = dataStream.ReadPackedUInt(CompressionModel);
                    if (baselineDelta >= GhostSystemConstants.MaxBaselineAge)
                        data.BaselineTick3 = NetworkTick.Invalid;
                    else
                    {
                        data.BaselineTick3 = serverTick;
                        data.BaselineTick3.Subtract(baselineDelta);
                    }
                    data.BaselineLen = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if (NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format("\t\tB0:{0} B1:{1} B2:{2} Count:{3}\n", data.BaselineTick.ToFixedString(), data.BaselineTick2.ToFixedString(), data.BaselineTick3.ToFixedString(), data.BaselineLen));
#endif
                    // BaselineTick 为 NetworkTick.Invalid 只对预生成 Ghost 合法，因为 Tick 0 具有特殊含义
                    if(Hint.Unlikely(!data.BaselineTick.IsValid && (data.BaseGhostId & PrespawnHelper.PrespawnGhostIdBase) == 0))
                    {
                        LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received snapshot baseline for prespawn ghost from the server, but the ghost entity GID:{data.BaseGhostId} (GhostType:{GetGhostTypePrefabName(data)}) is not a prespawn!");
                        return false;
                    }
                    if (Hint.Unlikely(data.BaselineTick3 != serverTick && (data.BaselineTick3 == data.BaselineTick2 || data.BaselineTick2 == data.BaselineTick)))
                    {
                        LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received invalid snapshot baseline (B1:{data.BaselineTick.ToFixedString()}, B2:{data.BaselineTick2.ToFixedString()}, B3:{data.BaselineTick3.ToFixedString()} from server for GID:{data.BaseGhostId} (GhostType:{GetGhostTypePrefabName(data)})!");
                        return false;
                    }
                }

                --data.BaselineLen;
                int ghostId = dataStream.ReadPackedIntDelta(data.BaseGhostId, CompressionModel);
                data.BaseGhostId = ghostId + 1;
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                    DebugLog.Append((FixedString128Bytes)$"\t\t\tGID:{ghostId}");
#endif

                // 仅为新的非预生成 Ghost 获取生成 Tick
                var isNewGhostForClient = data.BaselineTick == serverTick;
                NetworkTick serverSpawnTick = NetworkTick.Invalid;
                if (isNewGhostForClient && !PrespawnHelper.IsPrespawnGhostId(ghostId))
                {
                    // 仅为非预生成 Ghost 获取生成 Tick
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghostId))
                    {
                        serverSpawnTick = new NetworkTick{SerializedData = dataStream.ReadPackedUIntDelta(data.BaseSpawnTick.SerializedData, CompressionModel)};
                        data.BaseSpawnTick = serverSpawnTick;
#if NETCODE_DEBUG
                        if (NetDebugPacket.IsCreated)
                            DebugLog.Append(FixedString.Format(" SpawnTick:{0}", serverSpawnTick.ToFixedString()));
#endif
                    }
                }

                // 获取数据大小
                uint ghostDataSizeInBits = 0;
                int ghostDataStreamStartBitsRead = 0;
                if(GhostSystemConstants.SnapshotHasCompressedGhostSize)
                {
                    ghostDataSizeInBits = dataStream.ReadPackedUIntDelta(0, CompressionModel);
                    ghostDataStreamStartBitsRead = dataStream.GetBitsRead();
                }

                var typeData = m_GhostTypeCollection[(int)data.TargetArch];
                int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

                int snapshotOffset;
                int snapshotSize = typeData.SnapshotSize;
                byte* baselineData = (byte*)UnsafeUtility.Malloc(snapshotSize, 16, Allocator.Temp);
                UnsafeUtility.MemClear(baselineData, snapshotSize);
                Entity gent;
                DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer;
                SnapshotData snapshotDataComponent;
                GhostOwner ghostOwner;
                byte* snapshotData;
                //
                int baselineDynamicDataIndex = -1;
                byte* snapshotDynamicDataPtr = null;
                uint snapshotDynamicDataCapacity = 0; // 动态 Snapshot 数据历史槽位中的可用空间
                byte* baselineDynamicDataPtr = null;

                bool existingGhost = GhostEntityMap.TryGetValue(ghostId, out gent);
                if (SnapshotDataBufferFromEntity.HasBuffer(gent) && GhostFromEntity[gent].ghostType < 0)
                {
                    // 预生成 Ghost 在从服务器收到正确类型前，其 Ghost 类型可以为 -1
                    var existingGhostEnt = GhostFromEntity[gent];
                    existingGhostEnt.ghostType = (int)data.TargetArch;
                    GhostFromEntity[gent] = existingGhostEnt;

                    snapshotDataBuffer = SnapshotDataBufferFromEntity[gent];
                    snapshotDataBuffer.ResizeUninitialized(snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    UnsafeUtility.MemClear(snapshotDataBuffer.GetUnsafePtr(), snapshotSize * GhostSystemConstants.SnapshotHistorySize);
                    SnapshotDataFromEntity[gent] = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                }
                if (existingGhost && SnapshotDataBufferFromEntity.HasBuffer(gent) && GhostFromEntity[gent].ghostType == data.TargetArch)
                {
                    snapshotDataBuffer = SnapshotDataBufferFromEntity[gent];
                    CheckSnapshotBufferSizeIsCorrect(snapshotDataBuffer, snapshotSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr();
                    snapshotDataComponent = SnapshotDataFromEntity[gent];
                    snapshotDataComponent.LatestIndex = (snapshotDataComponent.LatestIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    SnapshotDataFromEntity[gent] = snapshotDataComponent;
                    // 如果这是未设置 Baseline Tick 的预生成 Ghost，则使用预生成 Baseline
                    if (!data.BaselineTick.IsValid && PrespawnHelper.IsPrespawnGhostId(GhostFromEntity[gent].ghostId))
                    {
                        CheckPrespawnBaselineIsPresent(gent, ghostId);
                        var prespawnBaselineBuffer = PrespawnBaselineBufferFromEntity[gent];
                        if (prespawnBaselineBuffer.Length > 0)
                        {
                            // 此处无需 MemCpy，可以直接重新指向该数据
                            baselineData = (byte*)prespawnBaselineBuffer.GetUnsafeReadOnlyPtr();
                            NetDebug.DebugLog(FixedString.Format("Client prespawn baseline ghost id={0} serverTick={1}", GhostFromEntity[gent].ghostId, serverTick.ToFixedString()));
                            // 预生成 Baseline 略有不同，其基础偏移量从 Buffer 起点开始记录
                            // TODO：修改接收系统，使所有路径统一采用此逻辑，即偏移量通常从 DynamicHeader 大小开始
                            if (typeData.NumBuffers > 0)
                            {
                                baselineDynamicDataPtr = baselineData + snapshotSize;
                            }
                        }
                        else
                        {
                            // 此错误无法跳过，客户端必须拥有预生成 Baseline
                            LogDeserializeFailure(LogType.Error, FixedString.Format("No prespawn baseline found for {0} GID:{1} (GhostType:{2}) -- cannot deserialize snapshot.", gent.ToFixedString(), GhostFromEntity[gent].ghostId, GetGhostTypePrefabName(data)));
                            return false;
                        }
                    }
                    else if (data.BaselineTick != serverTick)
                    {
                        // 从插入索引向后搜索 Buffer，确保始终先检查最新 Snapshot
                        int numSnapshotsInBuffer = snapshotDataBuffer.Length / snapshotSize;

                        for (int snapshotCount = 0; snapshotCount < numSnapshotsInBuffer; ++snapshotCount)
                        {
                            int bi = snapshotDataComponent.GetPreviousSnapshotIndexAtOffset(snapshotCount) * snapshotSize;

                            if (*(uint*)(snapshotData+ bi) == data.BaselineTick.SerializedData)
                            {
                                UnsafeUtility.MemCpy(baselineData, snapshotData+bi, snapshotSize);
                                // 如果 Ghost 含有 Buffer，还要获取 Baseline 的动态 Snapshot Buffer
                                if(typeData.NumBuffers > 0)
                                {
                                    if (!SnapshotDynamicDataFromEntity.HasBuffer(gent))
                                        throw new InvalidOperationException($"SnapshotDynamicDataBuffer buffer not found for ghost with id {ghostId}");
                                    baselineDynamicDataIndex = bi / snapshotSize;
                                }
                                break;
                            }
                        }

                        if (Hint.Unlikely(*(uint*)baselineData == 0))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Ack desync at data.BaselineTick:{data.BaselineTick.ToFixedString()} and ServerTick:{serverTick.ToFixedString()} for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) - server sent baseline(s) we do not have!");
                            return false; // 检测到 Ack 不同步
                        }
                    }

                    if (data.BaselineTick3 != serverTick)
                    {
                        byte* baselineData2 = null;
                        byte* baselineData3 = null;

                        // 从插入索引向后搜索 Buffer，确保始终先检查最新 Snapshot
                        int numSnapshotsInBuffer = snapshotDataBuffer.Length / snapshotSize;
                        for (int snapshotCount = 0; snapshotCount < numSnapshotsInBuffer; ++snapshotCount)
                        {
                            int bi = snapshotDataComponent.GetPreviousSnapshotIndexAtOffset(snapshotCount) * snapshotSize;

                            if (*(uint*)(snapshotData+bi) == data.BaselineTick2.SerializedData)
                            {
                                baselineData2 = snapshotData+bi;
                            }

                            if (*(uint*)(snapshotData+bi) == data.BaselineTick3.SerializedData)
                            {
                                baselineData3 = snapshotData+bi;
                            }
                        }

                        if (Hint.Unlikely(baselineData2 == null || baselineData3 == null))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Ack desync for baseline B2:{baselineData2 != null} and/or B3:{baselineData3 != null} at data.BaselineTick3:{data.BaselineTick3.ToFixedString()} and serverTick:{serverTick.ToFixedString()} for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) - server sent baseline(s) we do not have!");
                            return false; // 检测到 Ack 不同步
                        }
                        snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + changeMaskUints*sizeof(uint) + enableableMaskUints*sizeof(uint));
                        var predictor = new GhostDeltaPredictor(serverTick, data.BaselineTick, data.BaselineTick2, data.BaselineTick3);

                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            // Buffer 的长度和内容不使用增量预测
                            ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
                            if (!ghostSerializer.HasGhostFields)
                                continue;
                            if (!ghostSerializer.ComponentType.IsBuffer)
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, ghostSerializer.SnapshotSize, snapshotSize);
                                ghostSerializer.PredictDelta.Invoke(
                                    (IntPtr) (baselineData + snapshotOffset),
                                    (IntPtr) (baselineData2 + snapshotOffset),
                                    (IntPtr) (baselineData3 + snapshotOffset), ref predictor);
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                            }
                            else
                            {
                                CheckOffsetLessThanSnapshotBufferSize(snapshotOffset, GhostComponentSerializer.DynamicBufferComponentSnapshotSize, snapshotSize);
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                            }
                        }
                    }
                    // Buffer：获取动态内容大小，并重新调整 Snapshot 动态历史空间
                    if (typeData.NumBuffers > 0)
                    {
                        // 对动态数据大小执行增量解压
                        var buf = SnapshotDynamicDataFromEntity[gent];
                        uint baselineDynamicDataSize = 0;
                        if (baselineDynamicDataIndex != -1)
                        {
                            var bufferPtr = (byte*)buf.GetUnsafeReadOnlyPtr();
                            baselineDynamicDataSize = ((uint*) bufferPtr)[baselineDynamicDataIndex];
                        }
                        else if (PrespawnHelper.IsPrespawnGhostId(ghostId) && PrespawnBaselineBufferFromEntity.HasBuffer(gent))
                        {
                            CheckPrespawnBaselinePtrsAreValid(data, baselineData, ghostId, baselineDynamicDataPtr);
                            baselineDynamicDataSize = ((uint*)(baselineDynamicDataPtr))[0];
                        }
                        uint dynamicDataSize = dataStream.ReadPackedUIntDelta(baselineDynamicDataSize, CompressionModel);

                        if (Hint.Unlikely(!SnapshotDynamicDataFromEntity.HasBuffer(gent)))
                            throw new InvalidOperationException($"SnapshotDynamicDataBuffer buffer not found for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)})!");

                        // 调整 Snapshot Buffer 以容纳新大小，并预留约 20% 增长空间
                        var slotCapacity = SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(), buf.Length);
                        var newCapacity = SnapshotDynamicBuffersHelper.CalculateBufferCapacity(dynamicDataSize, out var newSlotCapacity);
                        if (buf.Length < newCapacity)
                        {
                            // 性能：重新分配 Buffer 时已经复制内容，最好能避免这次复制
                            buf.ResizeUninitialized((int)newCapacity);
                            // 槽位大小变化后重新移动 Buffer 内容
                            if (slotCapacity > 0)
                            {
                                var bufferPtr = (byte*)buf.GetUnsafePtr() + SnapshotDynamicBuffersHelper.GetHeaderSize();
                                var sourcePtr = bufferPtr + GhostSystemConstants.SnapshotHistorySize*slotCapacity;
                                var destPtr = bufferPtr + GhostSystemConstants.SnapshotHistorySize*newSlotCapacity;
                                for (int i=0;i<GhostSystemConstants.SnapshotHistorySize;++i)
                                {
                                    destPtr -= newSlotCapacity;
                                    sourcePtr -= slotCapacity;
                                    UnsafeUtility.MemMove(destPtr, sourcePtr, slotCapacity);
                                }
                            }
                            slotCapacity = newSlotCapacity;
                        }
                        // 在 Snapshot 中记录收到的数据大小以供增量压缩使用，并设置动态数据指针
                        var bufPtr = (byte*)buf.GetUnsafePtr();
                        ((uint*)bufPtr)[snapshotDataComponent.LatestIndex] = dynamicDataSize;
                        // 获取动态数据指针
                        snapshotDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufPtr,snapshotDataComponent.LatestIndex, buf.Length);
                        snapshotDynamicDataCapacity = slotCapacity;
                        if (baselineDynamicDataIndex != -1)
                            baselineDynamicDataPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr(bufPtr, baselineDynamicDataIndex, buf.Length);
                    }
                }
                else
                {
                    bool isPrespawn = PrespawnHelper.IsPrespawnGhostId(ghostId);
                    if (existingGhost)
                    {
                        // Ghost 实体映射已过期，将其清理
                        GhostEntityMap.Remove(ghostId);
                        if (GhostFromEntity.HasComponent(gent) && GhostFromEntity[gent].ghostType != data.TargetArch)
                             LogDeserializeFailure(LogType.Error, ($"Received a ghost (GID:{ghostId}, {gent.ToFixedString()}) with an invalid ghost type ({GetGhostTypePrefabName(data)}, expected {GhostFromEntity[gent].ghostType})."));
                        else if (isPrespawn)
                            LogDeserializeFailure(LogType.Error, ($"Found a prespawn ghost (GID:{ghostId}, {gent.ToFixedString()}, GhostType:{GetGhostTypePrefabName(data)}) that has no entity connected to it. This can happen if you unload a scene or destroy the ghost entity on the client."));
                        else
                            LogDeserializeFailure(LogType.Error, $"Found a ghost (GID:{ghostId}, {gent.ToFixedString()}, GhostType:{GetGhostTypePrefabName(data)}) in the ghost map which does not have an entity connected to it (or an invalid entity). This can happen if you delete ghost entities on the client.");
                    }
                    int prespawnSceneIndex = -1;
                    if (isPrespawn)
                    {
                        // 收到映射中不存在的预生成对象，可能原因如下
                        // - 场景已卸载，但服务器尚未卸载，或客户端在完成某种 Ack 前先行卸载
                        // - 客户端已销毁 Ghost
                        // - 相关性发生变化

                        // 查找预生成 Ghost 所属场景
                        var prespawnId = (int)(ghostId & ~PrespawnHelper.PrespawnGhostIdBase);
                        for (int i = 0; i < PrespawnSceneStateArray.Length; ++i)
                        {
                            if (prespawnId >= PrespawnSceneStateArray[i].FirstGhostId &&
                                prespawnId < PrespawnSceneStateArray[i].FirstGhostId + PrespawnSceneStateArray[i].PrespawnCount)
                            {
                                prespawnSceneIndex = i;
                                break;
                            }
                        }
                    }
                    if (data.BaselineTick != serverTick)
                    {
                        // 如果客户端先于服务器卸载 SubScene，或服务器根本不卸载，则视为短暂的不一致
                        // 服务器很快会得知客户端已不再拥有该场景，并停止向其发送 SubScene Ghost
                        // 尝试跳过数据进行恢复，如果流中没有 Ghost 大小位，则回退为标准错误
                        if(isPrespawn && prespawnSceneIndex == -1 && (GhostSystemConstants.SnapshotHasCompressedGhostSize))
                        {
#if NETCODE_DEBUG
                            if (NetDebugPacket.IsCreated)
                            {
                                DebugLog.Append(FixedString.Format("SKIP ({0}B)", ghostDataSizeInBits));
                                PacketDumpFlush();
                            }
#endif
                            while (ghostDataSizeInBits > 32)
                            {
                                dataStream.ReadRawBits(32);
                                ghostDataSizeInBits -= 32;
                            }
                            dataStream.ReadRawBits((int)ghostDataSizeInBits);
                            // 仍将数据视为有效，不强制服务器重新同步
                            return true;
                        }
                        if(!isPrespawn || data.BaselineTick.IsValid)
                        {
                            LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Received baseline for a ghost we do not have; GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}), BaselineTick:{data.BaselineTick.ToFixedString()}, existingGhost:{existingGhost}, serverTick:{serverTick.ToFixedString()}!");
                            return false;
                        }
                        LogDeserializeFailure(LogType.Warning, (FixedString512Bytes) $"Unknown baseline mismatch error for GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}), BaselineTick:{data.BaselineTick.ToFixedString()}, existingGhost:{existingGhost}, serverTick:{serverTick.ToFixedString()}!");
                        return false;
                    }

                    ++data.NewGhosts;
                    var ghostSpawnBuffer = GhostSpawnBufferFromEntity[GhostSpawnEntity];
                    snapshotDataBuffer = SnapshotDataBufferFromEntity[GhostSpawnEntity];
                    var snapshotDataBufferOffset = snapshotDataBuffer.Length;
                    // 扩展 GhostSpawnBuffer，使其同时包含动态数据大小
                    uint dynamicDataSize = 0;
                    if (typeData.NumBuffers > 0)
                        dynamicDataSize = dataStream.ReadPackedUIntDelta(0, CompressionModel);
                    var spawnedGhost = new GhostSpawnBuffer
                    {
                        GhostType = (int) data.TargetArch,
                        GhostID = ghostId,
                        DataOffset = snapshotDataBufferOffset,
                        DynamicDataSize = dynamicDataSize,
                        ClientSpawnTick = serverTick,
                        ServerSpawnTick = serverSpawnTick,
                        PrespawnIndex = -1
                    };
                    if (isPrespawn)
                    {
                        // 预生成 Ghost 因相关性变化而重新生成时，会缺少部分由转换系统添加到实例的组件
                        // 这些组件是 SceneSection 和 PreSpawnedGhostIndex
                        // 缺少 PreSpawnedGhostIndex 时，部分查询无法正确识别该 Ghost
                        // 缺少 SceneSection 时，Ghost 所属场景卸载后不会随之销毁
                        if (Hint.Likely(prespawnSceneIndex != -1))
                        {
                            spawnedGhost.PrespawnIndex = (int) (ghostId & ~PrespawnHelper.PrespawnGhostIdBase) - PrespawnSceneStateArray[prespawnSceneIndex].FirstGhostId;
                            spawnedGhost.SceneGUID = PrespawnSceneStateArray[prespawnSceneIndex].SceneGUID;
                            spawnedGhost.SectionIndex = PrespawnSceneStateArray[prespawnSceneIndex].SectionIndex;
                        }
                        else LogDeserializeFailure(LogType.Error, $"Received a new instance of a pre-spawned ghost GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) on ServerTick:{serverTick.ToFixedString()} due to relevancy changes, but no section with a enclosing id-range has been found!");
                    }
                    ghostSpawnBuffer.Add(spawnedGhost);
                    snapshotDataBuffer.ResizeUninitialized(snapshotDataBufferOffset + snapshotSize + (int)dynamicDataSize);
                    snapshotData = (byte*)snapshotDataBuffer.GetUnsafePtr() + snapshotDataBufferOffset;
                    UnsafeUtility.MemClear(snapshotData, snapshotSize + dynamicDataSize);
                    snapshotDataComponent = new SnapshotData{SnapshotSize = snapshotSize, LatestIndex = 0};
                    // 新 Ghost 的动态内容临时数据从 Snapshot 之后开始
                    if (typeData.NumBuffers > 0)
                    {
                        snapshotDynamicDataPtr = snapshotData + snapshotSize;
                        snapshotDynamicDataCapacity = dynamicDataSize;
                    }
                }

                int maskOffset = 0;
                // dynamicBufferOffset 用于跟踪每个实体的动态内容相对于动态历史槽位起点的偏移量
                uint dynamicBufferOffset = 0;

                snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + (changeMaskUints*sizeof(uint)) + (enableableMaskUints*sizeof(uint)));
                snapshotData += snapshotSize * snapshotDataComponent.LatestIndex;
                *(uint*)(snapshotData) = serverTick.SerializedData;
                uint* changeMask = (uint*)(snapshotData+sizeof(uint));
                uint anyChangeMaskThisEntity = 0;
                for (int cm = 0; cm < changeMaskUints; ++cm)
                {
                    var changeMaskUint = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+sizeof(uint)))[cm], CompressionModel);
                    changeMask[cm] = changeMaskUint;
                    anyChangeMaskThisEntity |= changeMaskUint;
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        DebugLog.Append(FixedString.Format(" ChangeMask:{0}", NetDebug.PrintMask(changeMask[cm])));
#endif
                }

                if (typeData.EnableableBits > 0)
                {
                    uint* enableBits = (uint*)(snapshotData+sizeof(uint) + changeMaskUints * sizeof(uint));
                    for (int em = 0; em < enableableMaskUints; ++em)
                    {
                        enableBits[em] = dataStream.ReadPackedUIntDelta(((uint*)(baselineData+sizeof(uint) + changeMaskUints * sizeof(uint)))[em], CompressionModel);
                    }
                }

#if NETCODE_DEBUG
                int entityStartBit = dataStream.GetBitsRead();
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                var perComponentStats = data.NetStatsSnapshot->PerGhostTypeStatsListRefRW.ElementAt((int)data.TargetArch).PerComponentStatsList;
                if (perComponentStats.Length < typeData.NumComponents) // 组件数量不会变化，因此这里只应发生一次
                    perComponentStats.Resize(typeData.NumComponents, NativeArrayOptions.ClearMemory);
#endif
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
#if NETCODE_DEBUG
                    FixedString128Bytes componentName = default;
                    var numBits = dataStream.GetBitsRead();
                    if (NetDebugPacket.IsCreated)
                    {
                        var componentTypeIndex = ghostSerializer.ComponentType.TypeIndex;
                        componentName = NetDebug.ComponentTypeNameLookup[componentTypeIndex];
                    }
#endif
                    if (ghostSerializer.HasGhostFields)
                    {
                        if (!ghostSerializer.ComponentType.IsBuffer)
                        {
                            CheckSnaphostBufferOverflow(maskOffset, ghostSerializer.ChangeMaskBits,
                                typeData.ChangeMaskBits, snapshotOffset, ghostSerializer.SnapshotSize, snapshotSize);
                            ghostSerializer.Deserialize.Invoke((IntPtr) (snapshotData + snapshotOffset), (IntPtr) (baselineData + snapshotOffset), ref dataStream, ref CompressionModel, (IntPtr) changeMask, maskOffset);
                            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                            maskOffset += ghostSerializer.ChangeMaskBits;
                        }
                        else
                        {
                            CheckSnaphostBufferOverflow(maskOffset, GhostComponentSerializer.DynamicBufferComponentMaskBits,
                                typeData.ChangeMaskBits, snapshotOffset, GhostComponentSerializer.DynamicBufferComponentSnapshotSize, snapshotSize);
                            // 对 Buffer 长度执行增量解压
                            uint mask = GhostComponentSerializer.CopyFromChangeMask((IntPtr) changeMask, maskOffset, GhostComponentSerializer.DynamicBufferComponentMaskBits);
                            var baseLen = *(uint*) (baselineData + snapshotOffset);
                            var baseOffset = *(uint*) (baselineData + snapshotOffset + sizeof(uint));
                            var bufLen = (mask & 0x2) == 0 ? baseLen : dataStream.ReadPackedUIntDelta(baseLen, CompressionModel);
                            // 将 Buffer 信息写入 Snapshot，并记录其相对于动态历史槽位起点的当前偏移量
                            *(uint*) (snapshotData + snapshotOffset) = bufLen;
                            *(uint*) (snapshotData + snapshotOffset + sizeof(uint)) = dynamicBufferOffset;
                            snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                            maskOffset += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            // 复制 Buffer 内容，并根据 Mask 位配置使用增量压缩
                            // 00：没有变化
                            // 01：长度相同，仅内容变化，为每个元素增加额外 Mask 位
                            // 11：长度变化，需要重新发送全部内容，不包含元素 Mask 位
                            var dynamicDataSnapshotStride = (uint)ghostSerializer.SnapshotSize;
                            var contentMaskUInts = (uint)GhostComponentSerializer.ChangeMaskArraySizeInUInts((int)(ghostSerializer.ChangeMaskBits * bufLen));
                            var maskSize = GhostComponentSerializer.SnapshotSizeAligned(contentMaskUInts*4);
                            CheckDynamicSnapshotBufferOverflow(dynamicBufferOffset, maskSize, bufLen*dynamicDataSnapshotStride, snapshotDynamicDataCapacity);
                            uint* contentMask = (uint*) (snapshotDynamicDataPtr + dynamicBufferOffset);
                            dynamicBufferOffset += maskSize;
                            if ((mask & 0x3) == 0) // 没有变化，直接复制 Baseline 内容
                            {
                                UnsafeUtility.MemSet(contentMask, 0x0, maskSize);
                                UnsafeUtility.MemCpy(snapshotDynamicDataPtr + dynamicBufferOffset,
                                    baselineDynamicDataPtr + baseOffset + maskSize, bufLen * dynamicDataSnapshotStride);
                                dynamicBufferOffset += bufLen * dynamicDataSnapshotStride;
                            }
                            else if ((mask & 0x2) != 0) // 长度变化，不存在元素 Mask
                            {
                                UnsafeUtility.MemSet(contentMask, 0xFF, maskSize);
                                var contentMaskOffset = 0;
                                // 此处性能不佳，最好改为调用一个内部完成全部内容序列化的方法，以减少函数调用次数
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    ghostSerializer.Deserialize.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) TempDynamicData.GetUnsafePtr(),
                                        ref dataStream, ref CompressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += ghostSerializer.ChangeMaskBits;
                                }
                            }
                            else // 长度相同但内容变化，解码 Mask 并复制内容
                            {
                                var baselineMaskPtr = (uint*) (baselineDynamicDataPtr + baseOffset);
                                for (int cm = 0; cm < contentMaskUInts; ++cm)
                                    contentMask[cm] = dataStream.ReadPackedUIntDelta(baselineMaskPtr[cm], CompressionModel);
                                baseOffset += maskSize;
                                var contentMaskOffset = 0;
                                for (int i = 0; i < bufLen; ++i)
                                {
                                    ghostSerializer.Deserialize.Invoke(
                                        (IntPtr) (snapshotDynamicDataPtr + dynamicBufferOffset),
                                        (IntPtr) (baselineDynamicDataPtr + baseOffset),
                                        ref dataStream, ref CompressionModel, (IntPtr) contentMask, contentMaskOffset);
                                    dynamicBufferOffset += dynamicDataSnapshotStride;
                                    baseOffset += dynamicDataSnapshotStride;
                                    contentMaskOffset += ghostSerializer.ChangeMaskBits;
                                }
                            }
                            dynamicBufferOffset = GhostComponentSerializer.SnapshotSizeAligned(dynamicBufferOffset);
                        }
                    }
#if NETCODE_DEBUG
                    numBits = dataStream.GetBitsRead() - numBits;

                    if (anyChangeMaskThisEntity != 0)
                    {
                        perComponentStats.ElementAt(comp).SizeInSnapshotInBits += (uint)numBits;

                        if (NetDebugPacket.IsCreated)
                        {
                            if (DebugLog.Length > (DebugLog.Capacity >> 1))
                            {
                                DebugLog.Append((FixedString32Bytes)" CONT");
                                PacketDumpFlush();
                            }

                            DebugLog.Append(FixedString.Format(" {0}:{1} ({2}B)", componentName, ghostSerializer.PredictionErrorNames, numBits));
                        }
                    }
#endif
                }
                // 此逻辑与 GhostChunkSerializer 中的编码逻辑相对应
                // 负责将因已确认 Baseline 解码得到、但当前客户端不应接收的 Snapshot 数据重置为 0
                // TODO：后续可让服务器保留此 Baseline 信息，从而避免这些重置操作
                var networkId = NetworkIdFromEntity[Connections[0]];
                if (typeData.PartialComponents != 0 || typeData.PartialSendToOwner != 0)
                {
                    GhostSendType serializeMask = GhostSendType.AllClients;
                    var sendToOwner = SendToOwnerType.All;
                    var isOwner = networkId.Value == *(int*)(snapshotData + typeData.PredictionOwnerOffset);
                    if(typeData.PartialSendToOwner != 0)
                        sendToOwner = isOwner ? SendToOwnerType.SendToOwner : SendToOwnerType.SendToNonOwner;
                    if (typeData.PartialComponents != 0 && typeData.OwnerPredicted != 0)
                        serializeMask = isOwner ? GhostSendType.OnlyPredictedClients : GhostSendType.OnlyInterpolatedClients;
                    int snapshotDataOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) +
                        (changeMaskUints * sizeof(uint)) +
                        (enableableMaskUints * sizeof(uint)));
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = m_GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ref readonly var ghostSerializer = ref m_GhostComponentCollection.ElementAtRO(serializerIdx);
                        var componentStride = m_GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                            : ghostSerializer.SnapshotSize;
                        componentStride = GhostComponentSerializer.SnapshotSizeAligned(componentStride);
                        if (ghostSerializer.HasGhostFields)
                        {
                            if ((serializeMask & m_GhostComponentIndex[typeData.FirstComponent + comp].SendMask) == 0 ||
                                (sendToOwner & m_GhostComponentIndex[typeData.FirstComponent + comp].SendToOwner) == 0)
                            {
                                uint* componentSnapshotData = (uint*)(snapshotData + snapshotDataOffset);
                                for(int i=0;i<componentStride/4;++i)
                                    componentSnapshotData[i] = 0;
                            }
                            snapshotDataOffset += componentStride;
                        }
                    }
                }
                // 检查所有者是否相对于 GhostOwner 中上次存储的值发生变化
                // 如果发生变化，需要在下一次 GhostUpdateSystem 更新前排入所有者切换请求
                // 此时所有组件已经准备好更新，可避免客户端感知变化时再增加一帧延迟
                // 另一种方案是在内部组件中保存旧值并始终与其比较
                // 由于 GhostOwner 是公开的，用户可以任意修改，内部旧值方案会提供更强的控制和安全性
                if (typeData.OwnerPredicted != 0 && existingGhost && GhostOwnerFromEntity.HasComponent(gent))
                {
                    ghostOwner = GhostOwnerFromEntity[gent];
                    var ownerId = *(int*)(snapshotData + typeData.PredictionOwnerOffset);
                    if(ghostOwner.NetworkId > 0 && ownerId <= 0 || ghostOwner.NetworkId <= 0 && ownerId > 0)
                    {
                        // 所有者已变化，标记该 Ghost 供所有者切换系统后续处理
                        OwnerPredictedSwitchQueue.Enqueue(new OwnerSwithchingEntry
                        {
                            CurrentOwner = ghostOwner.NetworkId,
                            NewOwner = ownerId,
                            TargetEntity = gent,
                        });
                    }
                }
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                {
                    if (anyChangeMaskThisEntity != 0)
                        DebugLog.Append(FixedString.Format(" Total ({0}B)", dataStream.GetBitsRead()-entityStartBit));
                    DebugLog.Append('\n');
                    PacketDumpFlush();
                }
#endif

#if UNITY_EDITOR || NETCODE_DEBUG
                ++data.StatCount;
                if (data.BaselineTick == serverTick)
                    ++data.UncompressedCount;
#endif

                var bitsReadForGhost = dataStream.GetBitsRead()-ghostDataStreamStartBitsRead;
                if (GhostSystemConstants.SnapshotHasCompressedGhostSize && Hint.Unlikely(bitsReadForGhost != ghostDataSizeInBits))
                {
                    LogDeserializeFailure(LogType.Error, (FixedString512Bytes) $"Failed to decode ghost GID {ghostId} (GhostType:{GetGhostTypePrefabName(data)}), got {bitsReadForGhost} bits for ghost, expected {ghostDataSizeInBits} bits!");
                    return false;
                }

                if (typeData.IsGhostGroup != 0)
                {
                    var groupLen = dataStream.ReadPackedUInt(CompressionModel);
#if NETCODE_DEBUG
                    if(NetDebugPacket.IsCreated)
                        NetDebugPacket.Log(FixedString.Format("\t\t\tGhostGroup.Len:{0}\n\t\t\t[", groupLen));
#endif
                    for (var i = 0; i < groupLen; ++i)
                    {
                        var childData = default(DeserializeData);
#if NETCODE_DEBUG
                        if(NetDebugPacket.IsCreated)
                            NetDebugPacket.Log(FixedString.Format("\t\t\t\t[{0}/{1}] ", i, groupLen));
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                        childData.NetStatsSnapshot = data.NetStatsSnapshot;
#endif
                        if (!DeserializeEntity(serverTick, ref dataStream, ref childData, i))
                        {
                            LogDeserializeFailure(LogType.Warning, (FixedString512Bytes)$"Error occurred during the GhostGroup deserialization of GID:{ghostId} (GhostType:{GetGhostTypePrefabName(data)}) at child index {i} of {groupLen}!");
                            return false;
                        }
                    }
#if NETCODE_DEBUG
                    if (NetDebugPacket.IsCreated)
                        NetDebugPacket.Log("\t\t\t]\n");
#endif
                }
                return true;
            }

            private FixedString128Bytes GetGhostTypePrefabName(in DeserializeData data)
            {
                var ghostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                if (data.TargetArch < ghostCollection.Length)
                {
                    var prefab = ghostCollection[(int) data.TargetArch];
#if NETCODE_DEBUG
                    if(PrefabNamesFromEntity.TryGetComponent(prefab.GhostPrefab, out var pdn))
                        // Burst 缺陷：必须使用 FixedString.Format，否则会输出 BlobStringText
                        return FixedString.Format("{0}({1}/{2})", pdn.PrefabName, data.TargetArch, ghostCollection.Length);
#endif
                    return (FixedString128Bytes)$"{prefab.GhostType.guid0}-{prefab.GhostType.guid1}-{prefab.GhostType.guid2}-{prefab.GhostType.guid3} Hash:{prefab.Hash}({data.TargetArch}/{ghostCollection.Length})";
                }
                return (FixedString128Bytes)$"???({data.TargetArch}/{ghostCollection.Length})";
            }

            /// <summary>
            /// 输出数据包转储，并记录 Warning 或 Error 日志
            /// </summary>
            private void LogDeserializeFailure(UnityEngine.LogType type, FixedString512Bytes msg)
            {
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated)
                {
                    PacketDumpFlush();
                    NetDebugPacket.Log(msg);
                }
#endif
                switch (type)
                {
                    case LogType.Error: NetDebug.LogError($"[{WorldName}][GhostReceiveSystem] {msg}"); break;
                    case LogType.Warning: NetDebug.LogWarning($"[{WorldName}][GhostReceiveSystem] {msg}"); break;
                    default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
                }
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpFlush()
            {
#if NETCODE_DEBUG
                if (NetDebugPacket.IsCreated && !DebugLog.IsEmpty)
                {
                    NetDebugPacket.Log(DebugLog);
                    DebugLog.Clear();
                }
#endif
            }
            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckPrespawnBaselineIsPresent(Entity gent, int ghostId)
            {
                if (!PrespawnBaselineBufferFromEntity.HasBuffer(gent))
                    throw new InvalidOperationException($"Prespawn baseline for ghost with id {ghostId} not present");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckPrespawnBaselinePtrsAreValid(DeserializeData data, byte* baselineData, int ghostId,
                byte* baselineDynamicDataPtr)
            {
                if (baselineData == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.TargetArch} does not have a baseline");
                if (baselineDynamicDataPtr == null)
                    throw new InvalidOperationException(
                        $"Prespawn ghost with id {ghostId} and archetype {data.TargetArch} does not have a baseline for the dynamic buffer");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckDynamicSnapshotBufferOverflow(uint dynamicBufferOffset, uint maskSize, uint dynamicDataSize,
                uint snapshotDynamicDataCapacity)
            {
                if ((dynamicBufferOffset + maskSize + dynamicDataSize) > snapshotDynamicDataCapacity)
                    throw new InvalidOperationException($"DynamicData Snapshot buffer overflow during deserialize! dynamicBufferOffset({dynamicBufferOffset}) + maskSize({maskSize}) + dynamicDataSize({dynamicDataSize}) must be <= snapshotDynamicDataCapacity({snapshotDynamicDataCapacity})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckSnaphostBufferOverflow(int maskOffset, int maskBits, int totalMaskBits,
                int snapshotOffset, int snapshotSize, int bufferSize)
            {
                if (maskOffset + maskBits > totalMaskBits)
                    throw new InvalidOperationException($"Snapshot buffer overflow during deserialize: maskOffset({maskOffset}) + maskBits({maskBits}) must be <= totalMaskBits({totalMaskBits})!");
                var snapshotSizeAligned = GhostComponentSerializer.SnapshotSizeAligned(snapshotSize);
                if (snapshotOffset + snapshotSizeAligned > bufferSize)
                    throw new InvalidOperationException($"Snapshot buffer overflow during deserialize: snapshotOffset({snapshotOffset}) + snapshotSizeAligned({snapshotSizeAligned}) must be <= bufferSize({bufferSize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckOffsetLessThanSnapshotBufferSize(int snapshotOffset, int snapshotSize, int bufferSize)
            {
                var snapshotSizeAligned = GhostComponentSerializer.SnapshotSizeAligned(snapshotSize);
                if (snapshotOffset + snapshotSizeAligned > bufferSize)
                    throw new InvalidOperationException($"Snapshot buffer overflow during predict: snapshotOffset({snapshotOffset}) + snapshotSizeAligned({snapshotSizeAligned}) must be <= bufferSize({bufferSize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            static void CheckSnapshotBufferSizeIsCorrect(DynamicBuffer<SnapshotDataBuffer> snapshotDataBuffer, int snapshotSize)
            {
                if (snapshotDataBuffer.Length != snapshotSize * GhostSystemConstants.SnapshotHistorySize)
                    throw new InvalidOperationException($"Invalid snapshot buffer size: snapshotDataBuffer.Length({snapshotDataBuffer.Length}) must == snapshotSize({snapshotSize}) * GhostSystemConstants.SnapshotHistorySize({GhostSystemConstants.SnapshotHistorySize})!");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckConnectionCountIsValid()
            {
                if (Connections.Length > 1)
                    throw new InvalidOperationException($"Ghost receive system only supports a single connection: Connections.Length({Connections.Length})!");
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var serverTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;

#if UNITY_EDITOR || NETCODE_DEBUG
            ref var netStatsSnapshotSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
#endif

            var commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            if (m_ConnectionsQuery.IsEmptyIgnoreFilter)
            {
                m_GhostCompletionCount[0] = m_GhostCompletionCount[1] = m_GhostCompletionCount[2] = 0;
                state.CompleteDependency(); // 确保可以访问已生成 Ghost 映射
                // 如果运行时没有生成任何 Ghost，则无需清理
                if (m_GhostCleanupQuery.IsEmptyIgnoreFilter &&
                    m_SpawnedGhostEntityMap.Count() == 0 && m_GhostEntityMap.Count() == 0)
                    return;
                var clearMapJob = new ClearMapJob
                {
                    GhostMap = m_GhostEntityMap,
                    SpawnedGhostMap = m_SpawnedGhostEntityMap
                };
                k_Scheduling.Begin();
                var clearHandle = clearMapJob.Schedule(state.Dependency);
                k_Scheduling.End();
                if (!m_GhostCleanupQuery.IsEmptyIgnoreFilter)
                {
                    m_EntityTypeHandle.Update(ref state);
                    var clearJob = new ClearGhostsJob
                    {
                        EntitiesType = m_EntityTypeHandle,
                        CommandBuffer = commandBuffer.AsParallelWriter()
                    };
                    k_Scheduling.Begin();
                    state.Dependency = clearJob.ScheduleParallel(m_GhostCleanupQuery, state.Dependency);
                    k_Scheduling.End();
                }
                state.Dependency = JobHandle.CombineDependencies(state.Dependency, clearHandle);
                return;
            }

            // 进入游戏前不开始处理 Ghost Snapshot，但允许执行上面的清理代码
            if (!SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                return;
            }

#if NETCODE_DEBUG
            FixedString128Bytes timestampAndTick = default;
            if (SystemAPI.HasSingleton<EnablePacketLogging>())
            {
                NetDebugInterop.InitDebugPacketIfNotCreated(ref m_NetDebugPacket, m_LogFolder, state.WorldUnmanaged.Name, 0);
                NetDebugInterop.GetTimestampWithTick(serverTick, out timestampAndTick);
                timestampAndTick = $"█ {timestampAndTick}[GRS-RSJ] ";
            }
#endif

            var connections = m_ConnectionsQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var connectionHandle);
            var prespawnSceneStateArray =
                m_SubSceneQuery.ToComponentDataListAsync<SubSceneWithGhostCleanup>(state.WorldUpdateAllocator,
                    out var prespawnHandle);
            ref readonly var ghostDespawnQueues = ref SystemAPI.GetSingletonRW<GhostDespawnQueues>().ValueRO;
            NativeQueue<OwnerSwithchingEntry> ownerPredictedQueues;
            if (SystemAPI.TryGetSingleton<GhostOwnerPredictedSwitchingQueue>(out var ownerPredictedSwitchingQueue))
                ownerPredictedQueues = ownerPredictedSwitchingQueue.SwitchOwnerQueue;
            else
                ownerPredictedQueues = new NativeQueue<OwnerSwithchingEntry>(state.WorldUpdateAllocator);
            UpdateLookupsForReadStreamJob(ref state);
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();
            var pendingAssignment = SystemAPI.GetSingletonRW<GhostCollection>().ValueRW.PendingGhostPrefabAssignment;
            var readJob = new ReadStreamJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = m_GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = m_GhostComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                Connections = connections,
                SnapshotFromEntity = m_SnapshotFromEntity,
                SnapshotDataBufferFromEntity = m_SnapshotDataBufferFromEntity,
                SnapshotDynamicDataFromEntity = m_SnapshotDynamicDataFromEntity,
                GhostSpawnBufferFromEntity = m_GhostSpawnBufferFromEntity,
                PrespawnBaselineBufferFromEntity = m_PrespawnBaselineBufferFromEntity,
                SnapshotDataFromEntity = m_SnapshotDataFromEntity,
                SnapshotAckFromEntity = m_SnapshotAckFromEntity,
                GhostOwnerFromEntity = m_GhostOwnerFromEntity,
                NetworkIdFromEntity = m_NetworkIdFromEntity,
                GhostEntityMap = m_GhostEntityMap,
                PendingGhostPrefabAssignment = pendingAssignment,
                CompressionModel = m_CompressionModel,
#if UNITY_EDITOR || NETCODE_DEBUG
                SnapshotStatsWriters = netStatsSnapshotSingleton.allGhostStatsParallelWrites.AsArray(),
#endif
                InterpolatedDespawnQueue = ghostDespawnQueues.InterpolatedDespawnQueue,
                PredictedDespawnQueue = ghostDespawnQueues.PredictedDespawnQueue,
                OwnerPredictedSwitchQueue = ownerPredictedQueues,
                PredictedFromEntity = m_PredictedFromEntity,
                GhostFromEntity = m_GhostFromEntity,
                IsThinClient = state.WorldUnmanaged.IsThinClient() ? (byte)1u : (byte)0u,
                CommandBuffer = commandBuffer,
                GhostSpawnEntity = SystemAPI.GetSingletonEntity<GhostSpawnQueue>(),
                GhostCompletionCount = m_GhostCompletionCount,
                TempDynamicData = m_TempDynamicData,
                PrespawnSceneStateArray = prespawnSceneStateArray,
                NetDebug = SystemAPI.GetSingleton<NetDebug>(),
                WorldName = state.WorldUnmanaged.Name,
#if NETCODE_DEBUG
                NetDebugPacket = m_NetDebugPacket,
                PrefabNamesFromEntity = m_PrefabNamesFromEntity,
                EnableLoggingFromEntity = m_EnableLoggingFromEntity,
                DebugLog = timestampAndTick,
#endif
            };
            var tempDeps = new NativeArray<JobHandle>(3, Allocator.Temp);
            tempDeps[0] = state.Dependency;
            tempDeps[1] = connectionHandle;
            tempDeps[2] = prespawnHandle;
            k_Scheduling.Begin();
            state.Dependency = readJob.Schedule(JobHandle.CombineDependencies(tempDeps));
            k_Scheduling.End();
#if NETCODE_DEBUG && !USING_UNITY_LOGGING
            state.Dependency = m_NetDebugPacket.Flush(state.Dependency);
#endif
        }

        void UpdateLookupsForReadStreamJob(ref SystemState state)
        {
            m_SnapshotDataFromEntity.Update(ref state);
            m_SnapshotAckFromEntity.Update(ref state);
            m_PredictedFromEntity.Update(ref state);
            m_GhostFromEntity.Update(ref state);
            m_GhostOwnerFromEntity.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
#if NETCODE_DEBUG
            m_PrefabNamesFromEntity.Update(ref state);
#endif
            m_EnableLoggingFromEntity.Update(ref state);

            m_GhostComponentCollectionFromEntity.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostComponentIndexFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_SnapshotFromEntity.Update(ref state);
            m_SnapshotDataBufferFromEntity.Update(ref state);
            m_SnapshotDynamicDataFromEntity.Update(ref state);
            m_GhostSpawnBufferFromEntity.Update(ref state);
            m_PrespawnBaselineBufferFromEntity.Update(ref state);
        }
    }
}
