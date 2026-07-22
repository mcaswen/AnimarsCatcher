#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode.LowLevel.Unsafe
{
    /// <summary>
    /// 每条连接、每个 Ghost Chunk 独立的状态，用于存储 Snapshot 发送可靠性信息，例如 Baseline
    /// </summary>
    unsafe struct GhostChunkSerializationState
    {
        public ulong sequenceNumber;
        public int ghostType;
        // 为提高性能缓存在此处
        public ushort baseImportance;
        public ushort maxSendRateAsSimTickInterval;

        // Entity 和 Data 数组都是二维数组，大小为 Chunk Capacity * 最大 Snapshot 数
        // 从不位于 writeIndex 且已被对端 Ack 的记录中找到最大 Tick，作为 Baseline
        // 将 entity、data[writeIndex] 作为当前值，将 entity、data[baseline] 作为 Baseline 传入
        // entity[baseline] 不匹配时不使用 Delta Compression
        private byte* snapshotData;
        private int allocatedChunkCapacity;
        private int allocatedDataSize;

        /// <summary>
        /// 此 Per-Chunk 状态中频繁变化的部分
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MetaData
        {
            public NetworkTick lastUpdate;
            public int startIndex;
            public int snapshotWriteIndex;
            public uint orderChangeVersion;
            public NetworkTick firstZeroChangeTick;
            public uint firstZeroChangeVersion;
            public int numRelevant;
            public NetworkTick lastValidTick;
        }

        // Snapshot Data 的内存布局如下，所有项目都向上对齐到 16 字节
        // 频繁变化的 MetaData 属性
        // uint[GhostSystemConstants.SnapshotHistorySize] Snapshot 索引，记录每个历史位置对应的 Tick
        // uint[(GhostSystemConstants.SnapshotHistorySize + 31) / 32] Snapshot Ack 状态，每个历史位置占一位
        // 开始 array[GhostSystemConstants.SnapshotHistorySize]，以下数据交错存储，每个历史位置各有一组
        //     Entity[capacity]，此历史位置对应的 Entity，与当前 Entity 不匹配时不能使用该 Snapshot
        //     byte[capacity * snapshotSize]，Chunk 中每个 Entity 的原始 Snapshot Data
        // 结束 array
        // Ghost Archetype 包含 Buffer 时，与该 Buffer 关联的 snapshotData 包含以下一对值
        //   uint bufferLen，Buffer 长度
        //   uint bufferContentOffset，相对动态历史槽起始位置的 Offset，Buffer 元素和 Mask 存储于此，详见下文

        // 必须与 MetaData 结构体匹配
        const int MetaDataSizeInInts = 8;
        // 4 是 uint 的字节大小，Chunk 大小也以字节表示
        const int DataPerChunkSize = (4 * (MetaDataSizeInInts + GhostSystemConstants.SnapshotHistorySize + ((GhostSystemConstants.SnapshotHistorySize+31)>>5)) + 15) & (~15);

        // Buffer 具有动态特性，因此需要另一个历史容器
        // Buffer 内容存储在可按需增长的动态数组中，Snapshot 动态存储区还能处理不同的 DynamicBuffer 元素类型
        // 每个已序列化 Buffer 的内容大小 len * ComponentSnapshotSize 都按 16 字节对齐
        // 内存布局如下
        // 开始 array[GhostSystemConstants.SnapshotHistorySize]
        //     uint dynamicDataSize[capacity]，Chunk 中每个 Entity 使用的 Buffer Data 总量，按 16 字节对齐
        //     开始 array[Chunk 中当前的 Buffer]
        //         uint[Len * ChangeBitMaskUintSize]，元素 ChangeMask，按 16 字节对齐
        //         byte[Len * ComponentSnapshotSize]，所有元素的原始 Snapshot Data
        //     结束 array
        // 结束 array
        private byte* snapshotDynamicData;
        private int snapshotDynamicCapacity;


        public void AllocateSnapshotData(int serializerDataSize, int chunkCapacity)
        {
            allocatedChunkCapacity = chunkCapacity;
            allocatedDataSize = serializerDataSize;
            snapshotData = (byte*) UnsafeUtility.Malloc(
                CalculateSize(serializerDataSize, chunkCapacity), 16, Allocator.Persistent);

            // 只清理 Snapshot 索引
            UnsafeUtility.MemClear(snapshotData, DataPerChunkSize);
            snapshotDynamicData = null;
            snapshotDynamicCapacity = 0;
        }

        public void FreeSnapshotData()
        {
            UnsafeUtility.Free(snapshotData, Allocator.Persistent);
            if(snapshotDynamicData != null)
                UnsafeUtility.Free(snapshotDynamicData, Allocator.Persistent);
            snapshotData = null;
            snapshotDynamicData = null;
            snapshotDynamicCapacity = 0;
        }

        public bool IsSameSizeAndCapacity(int size, int capacity)
        {
            return size == allocatedDataSize && capacity == allocatedChunkCapacity;
        }

        public int GetNumRelevant() => ((MetaData*)snapshotData)->numRelevant;

        public bool GetAllIrrelevant() => ((MetaData*)snapshotData)->numRelevant == 0;

        public void SetNumRelevant(int numRelevant, in ArchetypeChunk chunk)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(numRelevant >= 0 && numRelevant <= chunk.Count);
#endif
            ((MetaData*)snapshotData)->numRelevant = numRelevant;
        }
        public NetworkTick GetLastUpdate()
        {
            return ((MetaData*)snapshotData)->lastUpdate;
        }
        public int GetStartIndex()
        {
            return ((MetaData*)snapshotData)->startIndex;
        }
        /// <summary>
        /// 表示发生了一次完整发送，即非部分发送
        /// </summary>
        /// <param name="tick">发生发送的 Tick</param>
        public void SetLastFullUpdate(NetworkTick tick)
        {
            ((MetaData*)snapshotData)->lastUpdate = tick;
            ((MetaData*)snapshotData)->startIndex = 0;
        }
        public void SetStartIndex(int index)
        {
            ((MetaData*)snapshotData)->startIndex = index;
        }
        public int GetSnapshotWriteIndex()
        {
            return ((MetaData*)snapshotData)->snapshotWriteIndex;
        }
        public void SetSnapshotWriteIndex(int index)
        {
            ((MetaData*)snapshotData)->snapshotWriteIndex = index;
            // 将这份新尝试发送的数据标记为尚未 Ack
            ClearAckFlag(index);
        }

        public uint GetOrderChangeVersion()
        {
            return ((MetaData*)snapshotData)->orderChangeVersion;
        }
        public void SetOrderChangeVersion(uint version)
        {
            ((MetaData*)snapshotData)->orderChangeVersion = version;
        }
        public NetworkTick GetFirstZeroChangeTick()
        {
            return ((MetaData*)snapshotData)->firstZeroChangeTick;
        }
        public uint GetFirstZeroChangeVersion()
        {
            return ((MetaData*)snapshotData)->firstZeroChangeVersion;
        }
        public void SetFirstZeroChange(NetworkTick tick, uint version)
        {
            ((MetaData*)snapshotData)->firstZeroChangeTick = tick;
            ((MetaData*)snapshotData)->firstZeroChangeVersion = version;
        }
        public NetworkTick GetLastValidTick()
        {
            return ((MetaData*)snapshotData)->lastValidTick;
        }
        public void SetLastValidTick(NetworkTick tick)
        {
            ((MetaData*)snapshotData)->lastValidTick = tick;
        }
        public bool HasAckFlag(int pos)
        {
            var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
            uint bit = 1u<<(pos&31);
            return (GetSnapshotIndex()[idx] & bit) != 0;
        }
        public void SetAckFlag(int pos)
        {
            var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
            uint bit = 1u<<(pos&31);
            GetSnapshotIndex()[idx] |= bit;
        }
        public void ClearAckFlag(int pos)
        {
            var idx = GhostSystemConstants.SnapshotHistorySize + (pos>>5);
            uint bit = 1u<<(pos&31);
            GetSnapshotIndex()[idx] &= (~bit);
        }


        private static int CalculateSize(int serializerDataSize, int chunkCapacity)
        {
            int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
            int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
            return DataPerChunkSize + GhostSystemConstants.SnapshotHistorySize * (entitySize + dataSize);
        }

        public uint* GetSnapshotIndex()
        {
            // 加上 MetaDataSizeInInts 以跳过 Change Version 和 Tick
            return ((uint*) snapshotData) + MetaDataSizeInInts;
        }

        public Entity* GetEntity(int serializerDataSize, int chunkCapacity, int historyPosition)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                throw new IndexOutOfRangeException("Reading invalid history position");
            if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
            int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
            int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
            return (Entity*) (snapshotData + DataPerChunkSize + historyPosition * (entitySize + dataSize));
        }

        public byte* GetData(int serializerDataSize, int chunkCapacity, int historyPosition)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                throw new IndexOutOfRangeException("Reading invalid history position");
            if (serializerDataSize != allocatedDataSize || chunkCapacity != allocatedChunkCapacity)
                throw new IndexOutOfRangeException("Chunk capacity or data size changed");
#endif
            int entitySize = (UnsafeUtility.SizeOf<Entity>() * chunkCapacity + 15) & (~15);
            int dataSize = (serializerDataSize * chunkCapacity + 15) & (~15);
            return (snapshotData + DataPerChunkSize + entitySize + historyPosition * (entitySize + dataSize));
        }

        /// <summary>
        /// 返回指定历史位置对应的动态 Data Snapshot 存储区指针
        /// 存储区不存在或尚未初始化时返回 null
        /// </summary>
        /// <param name="historyPosition"></param>
        /// <param name="capacity"></param>
        /// <param name="chunkCapacity"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="IndexOutOfRangeException"></exception>
        public byte* GetDynamicDataPtr(int historyPosition, int chunkCapacity, out int capacity)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (historyPosition < 0 || historyPosition >= GhostSystemConstants.SnapshotHistorySize)
                throw new IndexOutOfRangeException("Reading invalid history position");
#endif
            // Chunk 状态刚创建时，需要先收集所需容量，之后才能分配动态数据指针
            if (snapshotDynamicData == null)
            {
                capacity = 0;
                return null;
            }
            var headerSize = GetDynamicDataHeaderSize(chunkCapacity);
            var slotStride = snapshotDynamicCapacity / GhostSystemConstants.SnapshotHistorySize;
            capacity = slotStride - headerSize;
            return snapshotDynamicData + slotStride*historyPosition;
        }

        static public int GetDynamicDataHeaderSize(int chunkCapacity)
        {
            return GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) * chunkCapacity);
        }

        public void EnsureDynamicDataCapacity(int historySlotCapacity, int chunkCapacity)
        {
            // 获取能够容纳所需大小的下一个 2 的幂
            var headerSize = GetDynamicDataHeaderSize(chunkCapacity);
            var wantedSize = GhostComponentSerializer.SnapshotSizeAligned(historySlotCapacity + headerSize);
            var newCapacity = math.ceilpow2(wantedSize * GhostSystemConstants.SnapshotHistorySize);
            if (snapshotDynamicCapacity < newCapacity)
            {
                var temp = (byte*)UnsafeUtility.Malloc(newCapacity, 16, Allocator.Persistent);
                // 复制旧内容
                if (snapshotDynamicData != null)
                {
                    var slotSize = snapshotDynamicCapacity / GhostSystemConstants.SnapshotHistorySize;
                    var newSlotSize = newCapacity / GhostSystemConstants.SnapshotHistorySize;
                    var sourcePtr = snapshotDynamicData;
                    var destPtr = temp;
                    for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
                    {
                        UnsafeUtility.MemCpy(destPtr, sourcePtr,slotSize);
                        destPtr += newSlotSize;
                        sourcePtr += slotSize;
                    }
                    UnsafeUtility.Free(snapshotDynamicData, Allocator.Persistent);
                }
                snapshotDynamicCapacity = newCapacity;
                snapshotDynamicData = temp;
            }
        }

        public FixedString64Bytes ZeroChangeFixedString()
        {
            return $"ZC[{GetFirstZeroChangeTick().ToFixedString()},{GetFirstZeroChangeVersion()}]";
        }
    }

    static class ConnectionGhostStateExtensions
    {
        public static ref ConnectionStateData.GhostState GetPrespawnGhostState(ref this ConnectionStateData.GhostStateList self, in int ghostId)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(PrespawnHelper.IsPrespawnGhostId(ghostId));
#endif
            return ref self.GetGhostState(ghostId, NetworkTick.Invalid);
        }

        public static ref ConnectionStateData.GhostState GetGhostState(ref this ConnectionStateData.GhostStateList self, in GhostCleanup cleanup)
            => ref self.GetGhostState(cleanup.ghostId, cleanup.spawnTick);

        public static ref ConnectionStateData.GhostState GetGhostState(ref this ConnectionStateData.GhostStateList self, in int ghostId, NetworkTick spawnTick)
        {
            // 清除可能存在的 Prespawn 标志位，映射到正确索引
            var index = (int)(ghostId & ~PrespawnHelper.PrespawnGhostIdBase);
            var isPrespawnGhost = PrespawnHelper.IsPrespawnGhostId(ghostId);
            var list = isPrespawnGhost ? self.PrespawnList : self.List;
            ref var state = ref list.ElementAt(index);

            // Prespawn Ghost 的初始状态必须设为 Relevant，否则客户端可能收不到已销毁或被标记为 Irrelevant 的 Prespawn Despawn 消息
            // 静态优化同样要求如此，因为 Prespawn 状态发生变化前，服务器实际上不会向客户端发送任何信息
            if (state.SpawnTick != spawnTick || (state.Flags & ConnectionStateData.GhostStateFlags.Initialized) == 0)
            {
                state = new ConnectionStateData.GhostState
                {
                    SpawnTick = spawnTick,
                    Flags = isPrespawnGhost
                        ? ConnectionStateData.GhostStateFlags.Initialized | ConnectionStateData.GhostStateFlags.IsRelevant
                        : ConnectionStateData.GhostStateFlags.Initialized,
                };
            }
            return ref state;
        }
    }
    unsafe struct ConnectionStateData : IDisposable
    {
        [Flags]
        public enum GhostStateFlags
        {
            /// <summary>
            /// 仅表示此 Ghost 与该连接相关
            /// 注意：Prespawn 自动视为 Relevant，因此此标志不表示已经发送过该 Ghost 的 Snapshot，
            /// 对 Prespawn 而言该标志是隐含状态
            /// </summary>
            /// <remarks>
            /// 示例：客户端和服务器都加载了一个 Prespawn Scene
            /// 客户端通知服务器该 Scene 已加载，但相关性半径使大部分新加载的 Ghost 需要被 Despawn，
            /// 因为它们已在客户端生成，却位于相关性半径之外
            /// 因此服务器需要正确设置此标志，才能知道必须向这些 Irrelevant Ghost 发送 Despawn 消息
            /// </remarks>
            IsRelevant = 1,
            /// <summary>
            /// 表示至少曾在一份 Snapshot 中向该客户端发送过此 Ghost
            /// 即该 Ghost 可能已经在该客户端生成
            /// </summary>
            SentWithChanges = 2,
            /// <summary>
            /// Prespawn Ghost 具有特殊的 Prespawn Baseline，
            /// 但它只在该 Ghost 首次被 Despawn 前有效，因为之后客户端上的 Baseline 已被销毁
            /// </summary>
            HasBeenDespawnedAtLeastOnce = 4,
            /// <summary>
            /// 表示 NetCode 已完成此 Ghost 的初始化配置
            /// </summary>
            Initialized = 8,
            /// <summary>
            /// 表示一条或多条 Despawn 消息正在途中，即等待客户端 Ack
            /// 也表示此 Ghost 在 <see cref="ConnectionStateData.PendingDespawns"/> 集合中存在条目
            /// </summary>
            IsDespawning = 16,
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct GhostState
        {
            /// <summary>
            /// 用于判断 Ghost 是否发生变化
            /// TODO：是否确实需要此字段，需要考虑低 NetworkTickRate 场景
            /// </summary>
            public NetworkTick SpawnTick;
            /// <summary>
            /// 发生结构性变更后用于查找 Snapshot History Buffer
            /// </summary>
            public int LastIndexInChunk;
            /// <summary>
            /// 发生结构性变更后用于查找 Snapshot History Buffer
            /// </summary>
            public ArchetypeChunk LastChunk;
            /// <summary>
            /// 跟踪此 Ghost 对该连接的初始化、相关性和 Despawn 状态
            /// </summary>
            public GhostStateFlags Flags;
        }
        public struct GhostStateList : IDisposable
        {
            public ref UnsafeList<GhostState> List
            {
                get { return ref m_List[0]; }
            }
            public ref UnsafeList<GhostState> PrespawnList
            {
                get { return ref m_List[1]; }
            }
            /// <summary>
            /// 表示该客户端尚未 Ack 的最早 <see cref="GhostCleanup.despawnTick"/>
            /// 用于允许 NetCode 清理 <see cref="GhostCleanup"/> Chunk
            /// </summary>
            public NetworkTick OldestPendingDespawnTick
            {
                get
                {
                    byte* ptr = (byte*)m_List;
                    ptr += 2*UnsafeUtility.SizeOf<UnsafeList<GhostState>>();
                    return new NetworkTick{SerializedData = *(uint*)ptr};
                }
                set
                {
                    byte* ptr = (byte*)m_List;
                    ptr += 2*UnsafeUtility.SizeOf<UnsafeList<GhostState>>();
                    *(uint*)ptr = value.SerializedData;
                }
            }
            [NativeDisableUnsafePtrRestriction]
            // 此 List 由两个 UnsafeList 组成，分别存储常规 Ghost 和 Prespawn Ghost，之后紧跟一个表示 AckedDespawnTick 的 uint
            UnsafeList<GhostState>* m_List;
            Allocator m_Allocator;

            // 专门为 Prespawn 预留的空间，待确认能否移除
            public GhostStateList(int capacity, int prespawnCapacity, Allocator allocator)
            {
                m_Allocator = allocator;
                m_List = (UnsafeList<GhostState>*)UnsafeUtility.Malloc(2*UnsafeUtility.SizeOf<UnsafeList<GhostState>>() + UnsafeUtility.SizeOf<uint>(), UnsafeUtility.AlignOf<UnsafeList<GhostState>>(), allocator);
                m_List[0] = new UnsafeList<GhostState>(CalculateStateListCapacity(capacity), allocator, NativeArrayOptions.ClearMemory);
                m_List[1] = new UnsafeList<GhostState>(CalculateStateListCapacity(prespawnCapacity), allocator, NativeArrayOptions.ClearMemory);
                OldestPendingDespawnTick = NetworkTick.Invalid;
            }
            public void Dispose()
            {
                m_List[0].Dispose();
                m_List[1].Dispose();
                UnsafeUtility.Free(m_List, m_Allocator);
            }

            public static int CalculateStateListCapacity(int capacity)
            {
                return (capacity + 1023) & (~1023);
            }
        }

        public void Dispose()
        {
            var chunkStates = SerializationState->GetValueArray(Allocator.Temp);
            for (int i = 0; i < chunkStates.Length; ++i)
                chunkStates[i].FreeSnapshotData();
            SerializationState->Dispose();
            AllocatorManager.Free(Allocator.Persistent, SerializationState);
            PendingDespawns->Dispose();
            AllocatorManager.Free(Allocator.Persistent, PendingDespawns);
            AckedPrespawnSceneMap.Dispose();
            UnsafeList<PrespawnHelper.GhostIdInterval>.Destroy(m_NewLoadedPrespawnRanges);
            GhostStateData.Dispose();
            UnsafeList<PrioChunk>.Destroy(PrioChunksPtr);
#if NETCODE_DEBUG
            NetDebugPacket.Dispose();
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public ConnectionStateData Create(Entity connection)
        {
            var serializationState = AllocatorManager.Allocate<UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState>>(Allocator.Persistent);
            *serializationState = new UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState>(1024, Allocator.Persistent);
            var pendingDespawns = AllocatorManager.Allocate<UnsafeList<PendingGhostDespawn>>(Allocator.Persistent);
            *pendingDespawns = new(256, Allocator.Persistent);
            return new ConnectionStateData
            {
                Entity = connection,
                SerializationState = serializationState,
                PendingDespawns = pendingDespawns,
#if NETCODE_DEBUG
                NetDebugPacket = new PacketDumpLogger(),
#endif
                GhostStateData = new GhostStateList(1024, 1024, Allocator.Persistent),
                AckedPrespawnSceneMap = new UnsafeParallelHashMap<ulong, int>(256, Allocator.Persistent),
                m_NewLoadedPrespawnRanges = UnsafeList<PrespawnHelper.GhostIdInterval>.Create(32, Allocator.Persistent),
                PrioChunksPtr = UnsafeList<PrioChunk>.Create(256, Allocator.Persistent),
            };
        }

        public Entity Entity;
        public UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState>* SerializationState;
        public ref UnsafeList<PrioChunk> PrioChunks => ref PrioChunksPtr[0];
        public UnsafeList<PrioChunk>* PrioChunksPtr;
        public UnsafeList<PendingGhostDespawn>* PendingDespawns;
#if NETCODE_DEBUG
        public PacketDumpLogger NetDebugPacket;
#endif
        public GhostStateList GhostStateData;
        public UnsafeParallelHashMap<ulong, int> AckedPrespawnSceneMap;
        public ref UnsafeList<PrespawnHelper.GhostIdInterval> NewLoadedPrespawnRanges => ref m_NewLoadedPrespawnRanges[0];
        private UnsafeList<PrespawnHelper.GhostIdInterval>* m_NewLoadedPrespawnRanges;

        public void EnsureGhostStateCapacity(int capacity, int prespawnCapacity)
        {
            if (capacity > GhostStateData.List.Length)
                GhostStateData.List.Resize(GhostStateList.CalculateStateListCapacity(capacity), NativeArrayOptions.ClearMemory);

            if(prespawnCapacity > GhostStateData.PrespawnList.Length)
                GhostStateData.PrespawnList.Resize(GhostStateList.CalculateStateListCapacity(prespawnCapacity), NativeArrayOptions.ClearMemory);
        }
    }
}
