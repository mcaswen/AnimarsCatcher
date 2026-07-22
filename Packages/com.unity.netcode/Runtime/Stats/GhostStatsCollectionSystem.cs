#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Profiling;

// TODO 让 DGS 的 Release 构建也能使用该功能

namespace Unity.NetCode
{
    internal struct GhostStats : IComponentData
    {
        public bool IsConnected;
    }
    internal struct GhostStatsCollectionCommand : IComponentData
    {
        public NativeArray<uint> Value;
    }

    // 该类型标记为 unsafe，因为它不能作为 NativeContainer，例如它会嵌入 GhostStatsSnapshotSingleton 的 NativeList 中
    // 当前假定包含原始 unsafe 实例的读取 Buffer 只在主线程调用，若需在 Job 中安全读取，可增加 GhostStatsSnapshotReader 包装器；由于暂未向用户开放此 API，现阶段不实现
    // 该数据由 NetCode 写入，不应手动修改
    // 内部仍保留安全检查，可处理读取器用于 Job、主线程同时尝试修改读取器底层 unsafe 数据的情况，例如更新双缓冲读取器
    // 也用于释放检查
    internal unsafe struct UnsafeGhostStatsSnapshot : IDisposable
    {
        public struct PerGhostTypeStats : IDisposable
        {
            public uint EntityCount; // 旧格式索引为 statType * 3 + 4
            public uint SizeInBits; // 旧格式索引为 statType * 3 + 5
            public uint UncompressedCount; // 旧格式索引为 statType * 3 + 6
            // TODO 实际发送的数据比这里统计的更多，包括 NetCode 内部用于状态跟踪的若干 int 和 byte；Profiler 应增加通用的“元数据”区域，用包大小减去所有逐组件大小，得到“NetCode 开销 + UTP 开销”
            // TODO 原实现将该值与 UncompressedCount 放在同一位置，即第 3 个索引，需确认这是误解、内存复用还是刻意优化；即使有 500 种 Ghost，也只增加约 2 KB，Web 调试包继续使用每种 Ghost 仅 3 个 uint 的旧格式即可
            public uint ChunkCount;
            internal NativeList<PerComponentStats> PerComponentStatsList;

            internal PerGhostTypeStats(Allocator allocator)
            {
                PerComponentStatsList = new(10, allocator);
                EntityCount = 0;
                SizeInBits = 0;
                UncompressedCount = 0;
                ChunkCount = 0;
            }

            internal void IncrementWith(in PerGhostTypeStats other)
            {
                EntityCount += other.EntityCount;
                SizeInBits += other.SizeInBits;
                UncompressedCount += other.UncompressedCount;
                ChunkCount += other.ChunkCount;
                for (int i = 0; i < other.PerComponentStatsList.Length; i++)
                {
                    if (i >= PerComponentStatsList.Length)
                        PerComponentStatsList.Add(other.PerComponentStatsList[i]);
                    else
                        PerComponentStatsList.ElementAt(i).IncrementWith(other.PerComponentStatsList[i]);
                }
            }

            public void Dispose()
            {
                PerComponentStatsList.Dispose();
            }

            internal void ResetToDefault()
            {
                EntityCount = 0;
                SizeInBits = 0;
                UncompressedCount = 0;
                ChunkCount = 0;
                PerComponentStatsList.ResetToDefault();
            }

            public int GetBlittableSizeBytes()
            {
                var toReturn = 0;
                toReturn += UnsafeUtility.SizeOf<uint>(); // EntityCount 字段
                toReturn += UnsafeUtility.SizeOf<uint>(); // SizeInBits 字段
                toReturn += UnsafeUtility.SizeOf<uint>(); // UncompressedCount 字段
                toReturn += UnsafeUtility.SizeOf<uint>(); // ChunkCount 字段
                toReturn += UnsafeUtility.SizeOf<int>(); // 列表长度
                for (int i = 0; i < PerComponentStatsList.Length; i++)
                {
                    toReturn += PerComponentStatsList[i].GetBlittableSizeBytes();
                }
                return toReturn;
            }

            public void ToBlittableData(ref DataStreamWriter writer)
            {
                writer.WriteUInt(EntityCount);
                writer.WriteUInt(SizeInBits);
                writer.WriteUInt(UncompressedCount);
                writer.WriteUInt(ChunkCount);
                writer.WriteInt(PerComponentStatsList.Length);
                for (int i = 0; i < PerComponentStatsList.Length; i++)
                {
                    PerComponentStatsList[i].ToBlittableData(ref writer);
                }
            }

            public static PerGhostTypeStats FromBlittableData(ref DataStreamReader reader, Allocator allocator)
            {
                var toReturn = new PerGhostTypeStats(allocator);
                toReturn.EntityCount = reader.ReadUInt();
                toReturn.SizeInBits = reader.ReadUInt();
                toReturn.UncompressedCount = reader.ReadUInt();
                toReturn.ChunkCount = reader.ReadUInt();
                var listLength = reader.ReadInt();
                toReturn.PerComponentStatsList.Resize(listLength, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < listLength; i++)
                {
                    toReturn.PerComponentStatsList[i] = PerComponentStats.FromBlittableData(ref reader);
                }
                return toReturn;
            }
        }

        [DebuggerDisplay("Size (bits): {SizeInSnapshotInBits}")]
        public struct PerComponentStats
        {
            public uint SizeInSnapshotInBits;

            public void IncrementWith(in PerComponentStats otherPerComponentStats)
            {
                SizeInSnapshotInBits += otherPerComponentStats.SizeInSnapshotInBits;
            }

            public void ResetToDefault()
            {
                SizeInSnapshotInBits = 0;
            }

            public int GetBlittableSizeBytes()
            {
                var toReturn = 0;
                toReturn += UnsafeUtility.SizeOf<int>(); // SizeInSnapshotInBits 字段
                return toReturn;
            }

            public void ToBlittableData(ref DataStreamWriter writer)
            {
                writer.WriteUInt(SizeInSnapshotInBits);
            }

            public static PerComponentStats FromBlittableData(ref DataStreamReader reader)
            {
                var toReturn = new PerComponentStats();
                toReturn.SizeInSnapshotInBits = reader.ReadUInt();
                return toReturn;
            }
        }

        // 这些数据过去保存在单个 uint 数组中，并以步长 3 设置各计数器，下文的“旧索引”用于对应这种旧存储方式
        internal NetworkTick Tick; // 旧索引 0；客户端表示收到的 Snapshot Tick，而非当前预测 Tick；服务端表示发送 System 执行时的 Server Tick
        internal uint DespawnCount; // 旧索引 1
        internal uint DestroySizeInBits; // 旧索引 2
        internal uint PacketsCount;
        internal uint SnapshotTotalSizeInBits; // 包含 Header
        public bool Initialized;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
#endif
        Allocator m_Allocator;

        // 按 GhostType 索引，TODO 考虑用结构体封装 GhostType 索引
        internal UnsafeList<PerGhostTypeStats> m_PerGhostTypeStatsList;

        public ref UnsafeList<PerGhostTypeStats> PerGhostTypeStatsListRefRW
        {
            get
            {
                CheckWrite();
                return ref UnsafeUtility.AsRef<UnsafeList<PerGhostTypeStats>>(UnsafeUtility.AddressOf(ref m_PerGhostTypeStatsList));
            }
        }
        public readonly UnsafeList<PerGhostTypeStats> PerGhostTypeStatsListRO
        {
            get
            {
                CheckRead();
                return m_PerGhostTypeStatsList;
            }
        }

        public UnsafeGhostStatsSnapshot(int numLoadedPrefab, Allocator allocator)
        {
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            m_Allocator = allocator;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = CollectionHelper.CreateSafetyHandle(m_Allocator);
#endif
            Initialized = true;

            m_PerGhostTypeStatsList = new(numLoadedPrefab, m_Allocator);
            for (int i = 0; i < numLoadedPrefab; i++)
            {
                PerGhostTypeStatsListRefRW.Add(new(m_Allocator));
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        public void IncrementWith(in UnsafeGhostStatsSnapshot other)
        {
            CheckWrite();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(other.m_Safety);
#endif
            DespawnCount += other.DespawnCount;
            DestroySizeInBits += other.DestroySizeInBits;
            PacketsCount += other.PacketsCount;
            SnapshotTotalSizeInBits += other.SnapshotTotalSizeInBits;
            for (int i = 0; i < other.PerGhostTypeStatsListRefRW.Length; i++)
            {
                PerGhostTypeStatsListRefRW.ElementAt(i).IncrementWith((other.PerGhostTypeStatsListRO)[i]);
            }
        }

        // 将所有数据重置为默认值
        public void ResetToDefault()
        {
            CheckWrite();
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            for (int i = 0; i < PerGhostTypeStatsListRefRW.Length; i++)
            {
                PerGhostTypeStatsListRefRW.ElementAt(i).ResetToDefault();
            }
        }

        public void Reset(int numLoadedPrefab)
        {
            CheckWrite();
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            if (numLoadedPrefab < PerGhostTypeStatsListRefRW.Length)
            {
                for (int i = numLoadedPrefab; i < PerGhostTypeStatsListRefRW.Length; i++)
                {
                    PerGhostTypeStatsListRefRW.ElementAt(i).Dispose();
                }
            }
            var previousLength = PerGhostTypeStatsListRefRW.Length;
            PerGhostTypeStatsListRefRW.Resize(numLoadedPrefab, NativeArrayOptions.UninitializedMemory);
            if (previousLength < PerGhostTypeStatsListRefRW.Length)
            {
                for (int i = previousLength; i < PerGhostTypeStatsListRefRW.Length; i++)
                {
                    (PerGhostTypeStatsListRefRW)[i] = new PerGhostTypeStats(m_Allocator);
                }
            }
            ResetToDefault();
        }

        public void Dispose()
        {
            foreach (var perGhostStat in PerGhostTypeStatsListRefRW)
            {
                perGhostStat.Dispose();
            }
            PerGhostTypeStatsListRefRW.Dispose();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            CollectionHelper.DisposeSafetyHandle(ref m_Safety);
#endif
        }

        public NativeArray<byte> ToBlittableData(Allocator allocator)
        {
            var toReturn = new NativeArray<byte>(GetBlittableSizeBytes(), allocator);

            var writer = new DataStreamWriter(toReturn);
            writer.WriteUInt(this.Tick.SerializedData);
            writer.WriteUInt(this.DespawnCount);
            writer.WriteUInt(this.DestroySizeInBits);
            writer.WriteUInt(this.PacketsCount);
            writer.WriteUInt(this.SnapshotTotalSizeInBits);
            var statsList = this.PerGhostTypeStatsListRO;
            writer.WriteInt(statsList.Length);
            for (int i = 0; i < statsList.Length; i++)
            {
                statsList[i].ToBlittableData(ref writer);
            }

            return toReturn;
        }

        int GetBlittableSizeBytes()
        {
            int toReturn = 0;

            toReturn += UnsafeUtility.SizeOf<uint>(); // Tick 字段
            toReturn += UnsafeUtility.SizeOf<uint>(); // DespawnCount 字段
            toReturn += UnsafeUtility.SizeOf<uint>(); // DestroySizeInBits 字段
            toReturn += UnsafeUtility.SizeOf<uint>(); // NewPacketsCountSent 字段
            toReturn += UnsafeUtility.SizeOf<uint>(); // SnapshotTotalSizeInBits 字段

            var statsList = this.PerGhostTypeStatsListRO;
            toReturn += UnsafeUtility.SizeOf<int>(); // 统计列表长度
            for (int i = 0; i < statsList.Length; i++)
            {
                toReturn += statsList[i].GetBlittableSizeBytes();
            }

            return toReturn;
        }

        public static UnsafeGhostStatsSnapshot FromBlittableData(Allocator allocator, NativeArray<byte> data)
        {
            var toReturn = new UnsafeGhostStatsSnapshot(1, allocator);

            var reader = new DataStreamReader(data);
            var tick = new NetworkTick();
            tick.SerializedData = reader.ReadUInt();
            toReturn.Tick = tick;
            toReturn.DespawnCount = reader.ReadUInt();
            toReturn.DestroySizeInBits = reader.ReadUInt();
            toReturn.PacketsCount = reader.ReadUInt();
            toReturn.SnapshotTotalSizeInBits = reader.ReadUInt();
            var listLength = reader.ReadInt();
            toReturn.PerGhostTypeStatsListRefRW.Resize(listLength, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < listLength; i++)
            {
                (toReturn.PerGhostTypeStatsListRefRW)[i] = PerGhostTypeStats.FromBlittableData(ref reader, allocator);
            }

            return toReturn;
        }

        #region for old format compatibility
        public readonly int ByteOldSize()
        {
            CheckRead();
            return UIntOldSize() * UnsafeUtility.SizeOf<uint>();
        }

        public readonly int UIntOldSize()
        {
            CheckRead();
            return 1 + 1 + 1 + (PerGhostTypeStatsListRO.Length * 3); // Despawn 数量 + 销毁大小 + 未知字段 + 各 Ghost 类型
            // return 1 + 1 + 1 + 1 + (PerGhostTypeStats.Length * 3); // Tick + Despawn 数量 + 销毁大小 + 未知字段 + 各 Ghost 类型
        }

        // 使用旧格式更新网页，以保持现有流程兼容；移除 Profiler 统计网页后应一并删除此方法
        public NativeArray<uint> ToOldBinary(Allocator allocator, bool useReceivedStats)
        {
            CheckRead();
            // 返回网页所需的原始格式
            var requiredSize = UIntOldSize();
            var toReturn = new NativeArray<uint>(requiredSize, allocator);
            // toReturn[0] = Tick.Value.SerializedData;
            // TODO 确认是否有意不发送 Tick
            toReturn[0] = DespawnCount;
            toReturn[1] = DestroySizeInBits;
            toReturn[2] = 0; // 该位置似乎从未使用且始终为 0，可能是预留字段
            for (int i = 0; i < PerGhostTypeStatsListRefRW.Length; i++)
            {
                toReturn[i * 3 + 3] = PerGhostTypeStatsListRefRW.ElementAt(i).EntityCount;
                toReturn[i * 3 + 4] = PerGhostTypeStatsListRefRW.ElementAt(i).SizeInBits;
                if (useReceivedStats)
                    toReturn[i * 3 + 5] = PerGhostTypeStatsListRefRW.ElementAt(i).UncompressedCount;
                else
                    toReturn[i * 3 + 5] = PerGhostTypeStatsListRefRW.ElementAt(i).ChunkCount;
            }
            return toReturn;
        }

        #endregion
    }

    // Snapshot 统计的主要访问入口
    // 流程如下：n 个工作线程从 GhostSendSystem 的 Job 并行收集统计，下一帧将这些工作线程统计合并到首个主统计，再复制到读取统计供 Metrics 和网页读取
    // 客户端只有一个线程，因此 GhostReceiveSystem 只使用首个写入统计，不需要 n 个 Writer
    // 使用 NativeList 保证并行写入访问安全，其底层内容均为 unsafe
    internal struct GhostStatsSnapshotSingleton : IComponentData, IDisposable
    {
        internal NativeList<UnsafeGhostStatsSnapshot> allGhostStatsParallelWrites;

        internal ref UnsafeGhostStatsSnapshot MainStatsWrite => ref allGhostStatsParallelWrites.ElementAt(0); // NativeList 可保证写入列表访问安全，但不能保证内部元素安全，必须确保每个实例始终由同一线程访问

        internal UnsafeGhostStatsSnapshot UnsafeMainStatsRead; // 只能在主线程访问

        static int MaxThreadCount
        {
            get
            {
#if UNITY_2022_2_14F1_OR_NEWER
                int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
                int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
                return maxThreadCount;
            }
        }

        public GhostStatsSnapshotSingleton(int initializeStatsSize, Allocator allocator)
        {
            allGhostStatsParallelWrites = new(MaxThreadCount, allocator);
            UnsafeMainStatsRead = new (initializeStatsSize, allocator);
        }


        // 用户读取统计的主要入口，用于取得安全的只读副本，应替代对 GhostMetrics 的直接访问
        // 这是 Job 正在写入的主统计副本，可从任意位置访问
        // public unsafe GhostStatsSnapshotReader GetAsyncStatsReader()
        // {
        //     UnsafeMainStatsRead.CheckRead();
        //     return new GhostStatsSnapshotReader((UnsafeGhostStatsSnapshot*)UnsafeUtility.AddressOf(ref this.UnsafeMainStatsRead));
        // }

        public unsafe UnsafeGhostStatsSnapshot GetAsyncStatsReader()
        {
            UnsafeMainStatsRead.CheckRead();
            return UnsafeMainStatsRead;
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        internal unsafe void ResetWriter(int numLoadedPrefabs)
        {
            allGhostStatsParallelWrites.Resize(MaxThreadCount, NativeArrayOptions.ClearMemory);

            for (int i = 0; i < MaxThreadCount; i++)
            {
                ref var statsSnapshotWriter = ref allGhostStatsParallelWrites.ElementAt(i);
                if (!statsSnapshotWriter.Initialized)
                    allGhostStatsParallelWrites[i] = new UnsafeGhostStatsSnapshot(numLoadedPrefabs, Allocator.Persistent);
                else
                    statsSnapshotWriter.Reset(numLoadedPrefabs);
            }
            MainStatsWrite.Tick = NetworkTick.Invalid;
        }
#endif

        /// <summary>
        /// 将指定 Tick 的 Snapshot Prefab 统计追加到集合中，由 <see cref="GhostSendSystem"/> 填充和使用
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        internal unsafe void UpdateDoubleBufferReadStats(in GhostStatsCollectionData collectionData, int snapshotCount, bool hasMonitor)
        {
            var statsTick = MainStatsWrite.Tick;
            if (!statsTick.IsValid || UnsafeMainStatsRead.PerGhostTypeStatsListRO.Length < MainStatsWrite.PerGhostTypeStatsListRO.Length-1 || snapshotCount >= 255 || (!hasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;

            // TODO 考虑直接交换指针以避免复制
            UnsafeMainStatsRead.Tick = MainStatsWrite.Tick;
            UnsafeMainStatsRead.IncrementWith(MainStatsWrite); // 可能在没有新统计时多次调用，因此不能覆盖现有值，只能累加
        }

        public unsafe void Dispose()
        {
            foreach (var statsCollectionSnapshot in allGhostStatsParallelWrites)
            {
                statsCollectionSnapshot.Dispose();
            }

            allGhostStatsParallelWrites.Dispose();
            UnsafeMainStatsRead.Dispose();
        }
    }

    internal struct GhostStatsCollectionPredictionError : IComponentData
    {
        public NativeList<float> Data;
    }
    internal struct GhostStatsCollectionMinMaxTick : IComponentData
    {
        public NativeArray<NetworkTick> Value;
    }

    /// <summary>
    /// GhostStatsCollectionSystem 负责保存客户端和服务端所有已发送及已接收的 Snapshot 统计
    /// 帧结束时，若调试器已连接，<see cref="GhostStatsConnection"/> 会把收集到的统计发送到 Network Debugger 工具进行可视化
    /// </summary>
    // 该 System 在接收 System Group 中最先更新，确保每帧都最先执行统计收集
    // 原因是它负责为统计设置当前 Tick
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation)]
    [BurstCompile]
    unsafe internal partial struct GhostStatsCollectionSystem : ISystem
    {
        private static GhostStatsConnection _sGhostStatsConnection;
        private uint m_UpdateId;
        private bool m_HasMonitor;

        /// <summary>
        /// 将指定 Tick 的命令发送和接收统计追加到集合中，由 <see cref="NetworkStreamReceiveSystem"/>
        /// 与 <see cref="CommandSendPacketSystem"/> 使用
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddCommandStats(NativeArray<uint> stats, in GhostStatsCollectionData collectionData)
        {
            var statsTick = new NetworkTick{SerializedData = stats[0]};
            if (!statsTick.IsValid || m_CommandTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;
            m_CommandStats += stats[1];
            if (m_CommandTicks.Length == 0 || m_CommandTicks[m_CommandTicks.Length-1] != stats[0])
                m_CommandTicks.Add(statsTick.TickIndexForValidTick);
        }
        /// <summary>
        /// 将 <see cref="GhostPredictionDebugSystem"/> 为指定 Tick 计算的预测误差追加到集合中
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddPredictionErrorStats(NativeArray<float> stats, in GhostStatsCollectionData collectionData)
        {
            if (m_SnapshotTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;
            for (int i = 0; i < stats.Length; ++i)
                m_PredictionErrors[i] = math.max(stats[i], m_PredictionErrors[i]);
        }

        /// <summary>
        /// 将丢弃的 Snapshot 或 Command 数量追加到集合中，二者分别由客户端和服务端接收
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddDiscardedPackets(uint stats, in GhostStatsCollectionData collectionData)
        {
            if (m_SnapshotTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;

            m_DiscardedPackets += stats;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_SnapshotTicks = new NativeList<NetworkTick>(16, Allocator.Persistent);
            m_PredictionErrors = new NativeList<float>(0, Allocator.Persistent);
            m_TimeSamples = new NativeList<TimeSample>(16, Allocator.Persistent);
            m_CommandTicks = new NativeList<uint>(16, Allocator.Persistent);

            m_PacketQueue = new NativeList<Packet>(16, Allocator.Persistent);
            m_PacketPool = new NativeList<byte>(4096, Allocator.Persistent);
            m_PacketPool.Resize(m_PacketPool.Capacity, NativeArrayOptions.UninitializedMemory);

            m_LastNameAndErrorArray = new NativeText(4096, Allocator.Persistent);

            m_CommandStatsData = new NativeArray<uint>(3, Allocator.Persistent);
            var typeList = new NativeArray<ComponentType>(6, Allocator.Temp);
            typeList[0] = ComponentType.ReadWrite<GhostStats>();
            typeList[1] = ComponentType.ReadWrite<GhostStatsCollectionCommand>();
            typeList[2] = ComponentType.ReadWrite<GhostStatsSnapshotSingleton>();
            typeList[3] = ComponentType.ReadWrite<GhostStatsCollectionPredictionError>();
            typeList[4] = ComponentType.ReadWrite<GhostStatsCollectionMinMaxTick>();
            typeList[5] = ComponentType.ReadWrite<GhostStatsCollectionData>();
            var statEnt = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(typeList));
            FixedString64Bytes singletonName = "GhostStatsCollectionSingleton";
            state.EntityManager.SetName(statEnt, singletonName);

            SystemAPI.SetSingleton(new GhostStatsCollectionCommand{Value = m_CommandStatsData});

            const int initialStatsSize = 128;
            SystemAPI.SetSingleton(new GhostStatsSnapshotSingleton(initialStatsSize, Allocator.Persistent));
            m_PredictionErrorStatsData = new NativeList<float>(initialStatsSize, Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostStatsCollectionPredictionError{Data = m_PredictionErrorStatsData});

#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            m_MinMaxTickStatsData = new NativeArray<NetworkTick>(maxThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostStatsCollectionMinMaxTick{Value = m_MinMaxTickStatsData});

            var ghostcollectionData = new GhostStatsCollectionData
            {
                m_PacketPool = m_PacketPool,
                m_PacketQueue = m_PacketQueue,
                m_LastNameAndErrorArray = m_LastNameAndErrorArray,
                m_PredictionErrors = m_PredictionErrors,
                m_StatIndex = -1,
                m_UsedPacketPoolSize = 0
            };
            ghostcollectionData.UpdateMaxPacketSize(initialStatsSize, m_PredictionErrors.Length);
            SystemAPI.SetSingleton(ghostcollectionData);

            m_Recorders = new NativeList<ProfilerRecorder>(Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_LastNameAndErrorArray.Dispose();
            m_PacketQueue.Dispose();
            m_CommandTicks.Dispose();
            m_SnapshotTicks.Dispose();
            m_TimeSamples.Dispose();
            m_CommandStatsData.Dispose();
            SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().Dispose();
            m_PredictionErrorStatsData.Dispose();
            m_MinMaxTickStatsData.Dispose();
            m_PacketPool.Dispose();
            m_PredictionErrors.Dispose();
            if (m_Recorders.IsCreated)
            {
                foreach (var recorder in m_Recorders)
                {
                    recorder.Dispose();
                }
                m_Recorders.Dispose();
            }
        }

        void UpdateSnapshotPacketCount(ref SystemState state, ref GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            if (SystemAPI.TryGetSingleton(out NetworkStreamDriver networkStreamDriver))
            {
                snapshotStatsSingleton.UnsafeMainStatsRead.PacketsCount = 0;

                var totalSnapshotSize = 0f;
                foreach (var stat in snapshotStatsSingleton.UnsafeMainStatsRead.PerGhostTypeStatsListRO)
                {
                    totalSnapshotSize += stat.SizeInBits / 8f;
                }

                if (totalSnapshotSize == 0) return;

                foreach (var networkStreamConnection in SystemAPI.Query<RefRO<NetworkStreamConnection>>())
                {
                    ref var driverStore = ref networkStreamDriver.DriverStore;
                    var connection = networkStreamConnection.ValueRO.Value;
                    for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; i++)
                    {
                        var networkDriver = driverStore.GetDriverRO(i); // 每个 Driver 都应配置相同的 Pipeline
                        var pipeline = driverStore.GetDriverInstanceRO(i).unreliablePipeline;

                        var headerSize = networkDriver.MaxHeaderSize(pipeline);
                        if (networkDriver.GetMaxSupportedMessageSize(connection) < 0)
                        {
                            // 很可能是 IPC，直接跳过
                            continue;
                        }
                        // 优先从非分片 Pipeline 获取 Header
                        var payloadMaxSize = networkDriver.GetMaxSupportedMessageSize(connection) - headerSize;
                        if (totalSnapshotSize > payloadMaxSize)
                        {
                            // 当前使用分片 Pipeline，改为获取其 Header
                            headerSize = networkDriver.MaxHeaderSize(driverStore.GetDriverInstanceRO(i).unreliableFragmentedPipeline);
                            payloadMaxSize = networkDriver.GetMaxSupportedMessageSize(connection) - headerSize;
                        }
                        snapshotStatsSingleton.UnsafeMainStatsRead.PacketsCount += (uint)math.ceil(totalSnapshotSize / payloadMaxSize);

                        break; // TODO 当前只统计全局 Snapshot 大小，未按连接区分，并假定所有连接及非 IPC Driver 的最大 Payload 大小一致；实际情况可能不同，后续应改为逐连接统计，现阶段先退出循环
                    }

                    break;
                }
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_HasMonitor = SystemAPI.TryGetSingleton<GhostMetricsMonitor>(out var monitorComponent);

            ref var collectionData = ref SystemAPI.GetSingletonRW<GhostStatsCollectionData>().ValueRW;

            SystemAPI.SetSingleton(new GhostStats{IsConnected = collectionData.m_StatIndex >= 0});

            if ((!m_HasMonitor && collectionData.m_StatIndex < 0) || state.WorldUnmanaged.IsThinClient())
                return;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var currentTick = networkTime.ServerTick;
            if (currentTick != collectionData.m_CollectionTick)
            {
                UpdateMetrics(ref state, currentTick);
                BeginCollection(ref state, currentTick, ref collectionData);
            }

            state.CompleteDependency(); // 必须完成依赖，因为 NetworkStreamReceiveSystem 中的 Job 会写入 NetworkSnapshotAck
            AddCommandStats(m_CommandStatsData, collectionData);
            AddDiscardedPackets(m_CommandStatsData[2], collectionData);
            m_CommandStatsData[0] = 0;
            m_CommandStatsData[1] = 0;
            m_CommandStatsData[2] = 0;

            // 合并当前帧中不同线程产生的统计
            ref var snapshotStatsSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
            if (snapshotStatsSingleton.allGhostStatsParallelWrites.Length > 0 && snapshotStatsSingleton.MainStatsWrite.Tick.SerializedData != 0)
            {
                ref var mainStats = ref snapshotStatsSingleton.MainStatsWrite;
                // 将各工作线程 Writer 的统计累加到主 Writer
                for (int worker = 1; worker < snapshotStatsSingleton.allGhostStatsParallelWrites.Length; worker++)
                {
                    ref var currentWorkerWriteStats = ref snapshotStatsSingleton.allGhostStatsParallelWrites.ElementAt(worker);
                    mainStats.IncrementWith(currentWorkerWriteStats);
                    currentWorkerWriteStats.ResetToDefault();
                }

                // 更新读取统计
                snapshotStatsSingleton.UpdateDoubleBufferReadStats(collectionData, m_SnapshotTicks.Length, m_HasMonitor);
                m_SnapshotTicks.Add(snapshotStatsSingleton.MainStatsWrite.Tick);
                // 统计已保存到 Reader，重置主 Writer
                snapshotStatsSingleton.MainStatsWrite.ResetToDefault();
            }
            UpdateSnapshotPacketCount(ref state, ref snapshotStatsSingleton);

            if (m_PredictionErrorStatsData.Length > 0)
            {
                AddPredictionErrorStats(m_PredictionErrorStatsData.AsArray(), collectionData);
                m_PredictionErrorStatsData.Clear();
            }

            m_SnapshotTickMin = m_MinMaxTickStatsData[0];
            m_SnapshotTickMax = m_MinMaxTickStatsData[1];
            m_MinMaxTickStatsData[0] = NetworkTick.Invalid;
            m_MinMaxTickStatsData[1] = NetworkTick.Invalid;

            // 汇总最小和最大 Age 统计
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            for (int i = 1; i < maxThreadCount; ++i)
            {
                if (m_MinMaxTickStatsData[intsPerCacheLine*i].IsValid &&
                    (!m_SnapshotTickMin.IsValid ||
                    m_SnapshotTickMin.IsNewerThan(m_MinMaxTickStatsData[intsPerCacheLine*i])))
                    m_SnapshotTickMin = m_MinMaxTickStatsData[intsPerCacheLine*i];
                if (m_MinMaxTickStatsData[intsPerCacheLine*i+1].IsValid &&
                    (!m_SnapshotTickMax.IsValid ||
                    m_MinMaxTickStatsData[intsPerCacheLine*i+1].IsNewerThan(m_SnapshotTickMax)))
                    m_SnapshotTickMax = m_MinMaxTickStatsData[intsPerCacheLine*i+1];
                m_MinMaxTickStatsData[intsPerCacheLine*i] = NetworkTick.Invalid;
                m_MinMaxTickStatsData[intsPerCacheLine*i+1] = NetworkTick.Invalid;
            }

            if (!setupRecorders && SystemAPI.TryGetSingletonEntity<GhostMetricsMonitor>(out var entity))
            {
                if (state.EntityManager.HasComponent<GhostNames>(entity) &&
                    state.EntityManager.HasComponent<GhostSerializationMetrics>(entity))
                {
                    var ghostNames = SystemAPI.GetSingletonBuffer<GhostNames>();
                    if (ghostNames.Length > 0)
                    {
                        var job = new ProfilerRecorderJob
                        {
                            names = ghostNames,
                            recorders = m_Recorders
                        };
                        state.Dependency = job.Schedule(state.Dependency);
                        setupRecorders = true;
                    }
                }
            }

            if (!SystemAPI.HasSingleton<UnscaledClientTime>() || !SystemAPI.HasSingleton<NetworkSnapshotAck>())
                return;

            var ack = SystemAPI.GetSingleton<NetworkSnapshotAck>();
            var networkTimeSystemStats = SystemAPI.GetSingleton<NetworkTimeSystemStats>();
            int minAge = m_SnapshotTickMax.IsValid?currentTick.TicksSince(m_SnapshotTickMax):0;
            int maxAge = m_SnapshotTickMin.IsValid?currentTick.TicksSince(m_SnapshotTickMin):0;
            var timeSample = new TimeSample
            {
                sampleFraction = networkTime.ServerTickFraction,
                timeScale = networkTimeSystemStats.GetAverageTimeScale(),
                interpolationScale = networkTimeSystemStats.GetAverageIterpTimeScale(),
                interpolationOffset = networkTimeSystemStats.currentInterpolationFrames,
                commandAge = ack.ServerCommandAge / 256f,
                rtt = ack.EstimatedRTT,
                jitter = ack.DeviationRTT,
                snapshotAgeMin = minAge,
                snapshotAgeMax = maxAge,
            };
            if (m_TimeSamples.Length < 255)
                m_TimeSamples.Add(timeSample);
        }

        void BeginCollection(ref SystemState state, NetworkTick currentTick, ref GhostStatsCollectionData collectionData)
        {
            if (collectionData.m_StatIndex >= 0 && collectionData.m_CollectionTick.IsValid)
                BuildPacket(ref state, ref collectionData);

            collectionData.m_CollectionTick = currentTick;
            m_SnapshotTicks.Clear();
            m_TimeSamples.Clear();
            SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW.UnsafeMainStatsRead.ResetToDefault();
            for (int i = 0; i < m_PredictionErrors.Length; ++i)
            {
                m_PredictionErrors[i] = 0;
            }

            m_CommandTicks.Clear();
            m_CommandStats = 0;
            m_DiscardedPackets = 0;
        }

        void BuildPacket(ref SystemState state, ref GhostStatsCollectionData statsData)
        {
            statsData.EnsurePoolSize(statsData.m_MaxPacketSize);
            int binarySize = 0;
            var binaryData = ((byte*)statsData.m_PacketPool.GetUnsafePtr()) + statsData.m_UsedPacketPoolSize;
            *(uint*) binaryData = statsData.m_CollectionTick.TickIndexForValidTick;
            binarySize += 4;
            binaryData[binarySize++] = (byte) statsData.m_StatIndex;
            binaryData[binarySize++] = (byte) m_TimeSamples.Length;
            binaryData[binarySize++] = (byte) m_SnapshotTicks.Length;
            binaryData[binarySize++] = (byte) m_CommandTicks.Length;
            binaryData[binarySize++] = 0; // RPC
            binaryData[binarySize++] = (byte)m_DiscardedPackets;
            binaryData[binarySize++] = 0; // 未使用
            binaryData[binarySize++] = 0; // 未使用

            for (int i = 0; i < m_TimeSamples.Length; ++i)
            {
                float* timeSample = (float*) (binaryData + binarySize);
                timeSample[0] = m_TimeSamples[i].sampleFraction;
                timeSample[1] = m_TimeSamples[i].timeScale;
                timeSample[2] = m_TimeSamples[i].interpolationOffset;
                timeSample[3] = m_TimeSamples[i].interpolationScale;
                timeSample[4] = m_TimeSamples[i].commandAge;
                timeSample[5] = m_TimeSamples[i].rtt;
                timeSample[6] = m_TimeSamples[i].jitter;
                timeSample[7] = m_TimeSamples[i].snapshotAgeMin;
                timeSample[8] = m_TimeSamples[i].snapshotAgeMax;
                binarySize += 36;
            }
            // 写入 Snapshot
            for (int i = 0; i < m_SnapshotTicks.Length; ++i)
            {
                *(uint*) (binaryData + binarySize) = m_SnapshotTicks[i].TickIndexForValidTick;
                binarySize += 4;
            }

            var statsSingleton = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>();

            using var bytes = statsSingleton.UnsafeMainStatsRead.ToOldBinary(Allocator.Temp, state.WorldUnmanaged.IsClient()).Reinterpret<byte>(UnsafeUtility.SizeOf<uint>());
            UnsafeUtility.MemCpy(binaryData + binarySize, bytes.GetUnsafePtr(), bytes.Length);
            binarySize += bytes.Length;
            // 写入预测误差
            for (int i = 0; i < m_PredictionErrors.Length; ++i)
            {
                *(float*) (binaryData + binarySize) = m_PredictionErrors[i];
                binarySize += 4;
            }
            // 写入 Command
            for (int i = 0; i < m_CommandTicks.Length; ++i)
            {
                *(uint*) (binaryData + binarySize) = m_CommandTicks[i];
                binarySize += 4;
            }
            *(uint*) (binaryData + binarySize) = m_CommandStats;
            binarySize += 4;

            statsData.m_PacketQueue.Add(new Packet
            {
                dataSize = binarySize,
                dataOffset = statsData.m_UsedPacketPoolSize
            });
            statsData.m_UsedPacketPoolSize += binarySize;
        }


        internal struct Packet
        {
            public int dataSize;
            public int dataOffset;
            public bool isString;
        }

        private bool setupRecorders;

        private NativeList<Packet> m_PacketQueue;
        private NativeList<byte> m_PacketPool;

        private NativeList<ProfilerRecorder> m_Recorders;
        private NetworkTick m_SnapshotTickMin;
        private NetworkTick m_SnapshotTickMax;
        private NativeList<TimeSample> m_TimeSamples;
        private NativeList<NetworkTick> m_SnapshotTicks; // TODO 确认该语义是否合理：这里保存消费前若干帧内收到的各个 Snapshot Tick；接收按帧率运行，而 Server Tick 按 Tick Rate 运行，因此可能连续多帧分别收到不同 Snapshot
        private NativeList<float> m_PredictionErrors;
        private uint m_CommandStats;
        private uint m_DiscardedPackets;
        private NativeList<uint> m_CommandTicks;

        private NativeText m_LastNameAndErrorArray;
        private NativeArray<uint> m_CommandStatsData;
        private NativeList<float> m_PredictionErrorStatsData;
        private NativeArray<NetworkTick> m_MinMaxTickStatsData;

        struct TimeSample
        {
            public float sampleFraction;
            public float timeScale;
            public float interpolationOffset;
            public float interpolationScale;
            public float commandAge;
            public float rtt;
            public float jitter;
            public float snapshotAgeMin;
            public float snapshotAgeMax;
        }
        // TODO 移到 GhostMetrics
        // 使用上一帧的读取 Buffer 更新 GhostMetrics
        void UpdateMetrics(ref SystemState state, NetworkTick currentTick)
        {
            var hasTimeSamples = m_TimeSamples.Length > 0;
            var hasSnapshotSamples = m_SnapshotTicks.Length > 0;
            var readStats = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().GetAsyncStatsReader();
            var ghostTypeStats = readStats.PerGhostTypeStatsListRO;
            var hasSnapshotStats = ghostTypeStats.IsCreated && ghostTypeStats.Length > 0;
            var hasPredictionErrors = m_PredictionErrors.Length > 0;

            uint totalSize = 0;
            uint totalCount = 0;

            if (SystemAPI.TryGetSingletonEntity<GhostMetricsMonitor>(out var entity))
            {
                ref var simulationMetrics = ref SystemAPI.GetSingletonRW<GhostMetricsMonitor>().ValueRW;
                simulationMetrics.CapturedTick = currentTick;

                if (hasTimeSamples && state.EntityManager.HasComponent<NetworkMetrics>(entity))
                {
                    ref var networkMetrics = ref SystemAPI.GetSingletonRW<NetworkMetrics>().ValueRW;

                    networkMetrics.SampleFraction = m_TimeSamples[0].sampleFraction;
                    networkMetrics.TimeScale = m_TimeSamples[0].timeScale;
                    networkMetrics.InterpolationOffset = m_TimeSamples[0].interpolationOffset;
                    networkMetrics.InterpolationScale = m_TimeSamples[0].interpolationScale;
                    networkMetrics.CommandAge = m_TimeSamples[0].commandAge;
                    networkMetrics.Rtt = m_TimeSamples[0].rtt;
                    networkMetrics.Jitter = m_TimeSamples[0].jitter;
                    networkMetrics.SnapshotAgeMin = m_TimeSamples[0].snapshotAgeMin;
                    networkMetrics.SnapshotAgeMax = m_TimeSamples[0].snapshotAgeMax;
                }
                if (hasPredictionErrors && state.EntityManager.HasComponent<PredictionErrorMetrics>(entity))
                {
                    if (SystemAPI.TryGetSingletonBuffer<PredictionErrorMetrics>(out var predictionErrorMetrics))
                    {
                        predictionErrorMetrics.Clear();
                        var count = m_PredictionErrors.Length;

                        for (int i = 0; i < count; i++)
                        {
                            predictionErrorMetrics.Add(new PredictionErrorMetrics
                            {
                                Value = m_PredictionErrors[i]
                            });
                        }
                    }
                }

                if (hasSnapshotStats && state.EntityManager.HasComponent<GhostMetrics>(entity))
                {
                    if (SystemAPI.TryGetSingletonBuffer<GhostMetrics>(out var ghostMetrics))
                    {
                        ghostMetrics.Clear();
                        var perGhostTypeStats = ghostTypeStats;
                        for (int ghostTypeIndex = 0; ghostTypeIndex < perGhostTypeStats.Length; ghostTypeIndex++)
                        {
                            var perTypeStat = perGhostTypeStats[ghostTypeIndex];
                            ghostMetrics.Add(new GhostMetrics
                            {
                                InstanceCount = perTypeStat.EntityCount,
                                SizeInBits = perTypeStat.SizeInBits,
                                ChunkCount = perTypeStat.ChunkCount,
                                Uncompressed = perTypeStat.UncompressedCount,
                            });
                            totalSize += perTypeStat.SizeInBits;
                            totalCount += perTypeStat.EntityCount;
                            }
                    }
                }
                if (hasSnapshotSamples && state.EntityManager.HasComponent<SnapshotMetrics>(entity))
                {
                    ref var snapshotMetrics = ref SystemAPI.GetSingletonRW<SnapshotMetrics>().ValueRW;

                    snapshotMetrics.SnapshotTick = readStats.Tick.SerializedData;
                    snapshotMetrics.TotalSizeInBits = totalSize;
                    snapshotMetrics.TotalGhostCount = totalCount;
                    snapshotMetrics.DestroyInstanceCount = hasSnapshotStats ? readStats.DespawnCount : 0;
                    snapshotMetrics.DestroySizeInBits = hasSnapshotStats ? readStats.DestroySizeInBits : 0;
                }
            }

            if (m_Recorders.IsCreated && SystemAPI.TryGetSingletonBuffer<GhostSerializationMetrics>(out var serializationMetrics))
            {
                serializationMetrics.Clear();
                var count = m_Recorders.Length;

                for (int i = 0; i < count; i++)
                {
                    serializationMetrics.Add(new GhostSerializationMetrics
                    {
                        LastRecordedValue = m_Recorders[i].LastValue
                    });
                }
            }
        }

        struct ProfilerRecorderJob : IJob
        {
            public DynamicBuffer<GhostNames> names;
            public NativeList<ProfilerRecorder> recorders;
            public void Execute()
            {
                for (int i = 0; i < names.Length; i++)
                {
                    recorders.Add(ProfilerRecorder.StartNew(new ProfilerCategory("GhostSendSystem"),
                        names[i].Name.Value));
                }
            }
        }
    }

    // 将数据收集为待发送到网页的二进制包
    internal struct GhostStatsCollectionData : IComponentData
    {
        public NativeList<byte> m_PacketPool; // 发送到 Profiler 网页的 WebSocket 数据包
        public NativeList<GhostStatsCollectionSystem.Packet> m_PacketQueue;
        public NativeText m_LastNameAndErrorArray;
        public NativeList<float> m_PredictionErrors;
        public int m_StatIndex;
        public int m_UsedPacketPoolSize;
        public int m_MaxPacketSize;
        public NetworkTick m_CollectionTick;

        public void EnsurePoolSize(int packetSize)
        {
            if (m_UsedPacketPoolSize + packetSize > m_PacketPool.Length)
            {
                int newLen = m_PacketPool.Length*2;
                while (m_UsedPacketPoolSize + packetSize > newLen)
                    newLen *= 2;
                m_PacketPool.Resize(newLen, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void UpdateMaxPacketSize(int snapshotStatsLength, int predictionErrorsLength)
        {
            // 计算新的最大数据包大小
            var packetSize = 8 + 20 * 255 + 4 * snapshotStatsLength + 4 * predictionErrorsLength + 4 * 255;
            if (packetSize == m_MaxPacketSize)
                return;
            m_MaxPacketSize = packetSize;

            // 丢弃所有尚未进入队列的待处理数据包
            m_CollectionTick = NetworkTick.Invalid;
        }

        /// <summary>
        /// 设置 Ghost Prefab 与误差名称，供 NetworkDebugger 工具使用
        /// 在 <see cref="GhostCollectionSystem"/> 处理完 Prefab 集合后调用
        /// </summary>
        /// <param name="nameList"></param>
        /// <param name="errorList"></param>
        /// <param name="worldName"></param>
        public void SetGhostNames(in FixedString128Bytes worldName,
            NativeList<FixedString64Bytes> nameList, NativeList<PredictionErrorNames> errorList,
            int predictedErrorCount, ref GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            // 使用新的名称列表添加待处理数据包
            m_LastNameAndErrorArray.Clear();
            m_LastNameAndErrorArray.Append((FixedString32Bytes)"\"name\":\"");
            m_LastNameAndErrorArray.Append(worldName);
            m_LastNameAndErrorArray.Append((FixedString32Bytes)"\",\"ghosts\":[\"Destroy\"");
            for (int i = 0; i < nameList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(nameList[i]);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append((FixedString32Bytes)"], \"errors\":[");
            if (errorList.Length > 0)
            {
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[0].Name);
                m_LastNameAndErrorArray.Append('"');
            }
            for (int i = 1; i < errorList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[i].Name);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append(']');

            // Ghost 集合更新时调用，此时可获知新增或移除的 Ghost 类型，并相应调整统计列表大小
            snapshotStatsSingleton.UnsafeMainStatsRead.Reset(nameList.Length);

            // 调整大小前先清空，否则 Resize 会复制旧值
            if (m_PredictionErrors.Length != predictedErrorCount)
            {
                m_PredictionErrors.Clear();
                m_PredictionErrors.ResizeUninitialized(predictedErrorCount);
            }

            if (m_StatIndex < 0)
                return;

            AppendNamePacket(snapshotStatsSingleton);
        }
        public unsafe void AppendNamePacket(in GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            FixedString64Bytes header = "{\"index\":";
            header.Append(m_StatIndex);
            header.Append(',');
            FixedString32Bytes footer = "}";

            var totalLen = header.Length + m_LastNameAndErrorArray.Length + footer.Length;
            EnsurePoolSize(totalLen);

            var binaryData = ((byte*)m_PacketPool.GetUnsafePtr()) + m_UsedPacketPoolSize;
            UnsafeUtility.MemCpy(binaryData, header.GetUnsafePtr(), header.Length);
            UnsafeUtility.MemCpy(binaryData + header.Length, m_LastNameAndErrorArray.GetUnsafePtr(), m_LastNameAndErrorArray.Length);
            UnsafeUtility.MemCpy(binaryData + header.Length + m_LastNameAndErrorArray.Length, footer.GetUnsafePtr(), footer.Length);

            m_PacketQueue.Add(new GhostStatsCollectionSystem.Packet
            {
                dataSize = totalLen,
                dataOffset = m_UsedPacketPoolSize,
                isString = true
            });
            m_UsedPacketPoolSize += totalLen;
            // 确保数据包大小足以容纳新的 Snapshot 统计
            UpdateMaxPacketSize(snapshotStatsSingleton.UnsafeMainStatsRead.ByteOldSize(), m_PredictionErrors.Length);
        }
    }
}
#endif
