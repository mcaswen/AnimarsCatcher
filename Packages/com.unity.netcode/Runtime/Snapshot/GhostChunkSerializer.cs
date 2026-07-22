#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Entities;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    internal enum SerializeEnitiesResult
    {
        Unknown = 0,
        Ok,
        /// <summary>
        /// 很可能是因为数据包已经填满
        /// </summary>
        Failed,
        /// <summary>
        /// 发生致命异常
        /// </summary>
        Abort,
    }
    internal unsafe struct GhostChunkSerializer
    {
        public DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
        public DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
        public DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
        public ComponentTypeHandle<PreSpawnedGhostIndex> PrespawnIndexType;
        public EntityStorageInfoLookup childEntityLookup;
        public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
        public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaselineTypeHandle;
        public EntityTypeHandle entityType;
        public ComponentTypeHandle<GhostInstance> ghostComponentType;
        public ComponentTypeHandle<GhostCleanup> ghostSystemStateType;
        public ComponentTypeHandle<PreSerializedGhost> preSerializedGhostType;
        public ComponentTypeHandle<GhostChildEntity> ghostChildEntityComponentType;
        public BufferTypeHandle<GhostGroup> ghostGroupType;
        public NetworkSnapshotAck snapshotAck;
        public UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState> chunkSerializationData;
        public DynamicComponentTypeHandle* ghostChunkComponentTypesPtr;
        public int ghostChunkComponentTypesLength;
        public NetworkTick currentTick;
        public int expectedSnapshotRttInSimTicks;
        public StreamCompressionModel compressionModel;
        public GhostSerializerState serializerState;
        public int NetworkId;
        public NativeParallelHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
        public GhostRelevancyMode relevancyMode;
        public EntityQueryMask userGlobalRelevantMask;
        public EntityQueryMask internalGlobalRelevantMask;
        public UnsafeList<PendingGhostDespawn>* pendingDespawns;
        public ConnectionStateData.GhostStateList ghostStateData;
        public uint CurrentSystemVersion;
        public NetDebug netDebug;
#if NETCODE_DEBUG
        public PacketDumpLogger netDebugPacket;
        public FixedString512Bytes netDebugPacketDebug;
        public FixedString128Bytes netDebugPacketResult;
        public FixedString64Bytes ghostTypeName;
        public NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> componentStats;
#endif

        [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotPreSerializeData;
        public GhostSendSystemData systemData;

        private NativeArray<byte> tempRelevancyPerEntity;
        private NativeList<SnapshotBaseline> tempAvailableBaselines;

        private byte** tempBaselinesPerEntity;
        private byte** tempComponentDataPerEntity;
        private int* tempComponentDataLenPerEntity;
        private int* tempDynamicDataLenPerEntity;
        private int* tempSameBaselinePerEntity;
        private DataStreamWriter tempWriter;
        private int* tempEntityStartBit;
        private byte* tempZeroBaseline;

        struct CurrentSnapshotState
        {
            public Entity* SnapshotEntity;
            public void* SnapshotData;
            // 在以下特定条件下可以为 null
            // GhostGroup，临时情况
            // 生成阶段的 Chunk
            public byte* SnapshotDynamicData;
            public int SnapshotDynamicDataCapacity;
            // 要序列化的整个 Chunk 动态 Buffer Data 总量
            // currentDynamicDataCapacity 与 snapshotDynamicDataSize 可以不同，前者通常更大
            // Spawn Chunk 不分配完整历史缓冲区，因此 currentDynamicDataCapacity 等于 0，改为创建临时 Data Buffer
            public int SnapshotDynamicDataSize;

            public NativeList<SnapshotBaseline> AvailableBaselines;
            public byte NumInFlightBaselines;
            public byte* relevancyData;
            public byte AlreadyUsedChunk;
        }
        unsafe struct SnapshotBaseline
        {
            public uint tick;
            public byte* snapshot;
            public Entity* entity;
            // 与 Snapshot 关联的动态 Buffer Data 存储区
            public byte *dynamicData;
        }

        public void AllocateTempData(int maxGhostsPerChunk, int dataStreamCapacity)
        {
            tempAvailableBaselines =
                new NativeList<GhostChunkSerializer.SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
            tempRelevancyPerEntity = new NativeArray<byte>(maxGhostsPerChunk, Allocator.Temp);

            int maxComponentCount = 0;
            int maxSnapshotSize = 0;
            for (int i = 0; i < GhostTypeCollection.Length; ++i)
            {
                ref readonly var ghostCollectionPrefabSerializer = ref GhostTypeCollection.ElementAtRO(i);
                maxComponentCount = math.max(maxComponentCount, ghostCollectionPrefabSerializer.NumComponents);
                maxSnapshotSize = math.max(maxSnapshotSize, GhostComponentSerializer.SnapshotSizeAligned(ghostCollectionPrefabSerializer.SnapshotSize));
            }

            tempBaselinesPerEntity = (byte**)UnsafeUtility.Malloc(maxGhostsPerChunk*4*UnsafeUtility.SizeOf<IntPtr>(), 16, Allocator.Temp);
            tempComponentDataPerEntity = (byte**)UnsafeUtility.Malloc(maxGhostsPerChunk*UnsafeUtility.SizeOf<IntPtr>(), 16, Allocator.Temp);
            tempComponentDataLenPerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempDynamicDataLenPerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempSameBaselinePerEntity = (int*)UnsafeUtility.Malloc(maxGhostsPerChunk*4, 16, Allocator.Temp);
            tempWriter = new DataStreamWriter(math.max(dataStreamCapacity, 1024), Allocator.Temp);
            tempEntityStartBit = (int*)UnsafeUtility.Malloc(8*maxGhostsPerChunk+8*maxGhostsPerChunk*maxComponentCount, 16, Allocator.Temp);
            tempZeroBaseline = (byte*)UnsafeUtility.Malloc(maxSnapshotSize, 16, Allocator.Temp);
            UnsafeUtility.MemSet(tempZeroBaseline, 0, maxSnapshotSize);
        }

        private void SetupDataAndAvailableBaselines(ref CurrentSnapshotState currentSnapshot, ref GhostChunkSerializationState chunkState, ArchetypeChunk chunk, int snapshotSize, int writeIndex, uint* snapshotIndex)
        {
            // 查找用于计算 Delta 的已 Ack Snapshot，并设置当前和历史 Entity* 与 Data* 指针
            // 完成后记得推进 writeIndex
            currentSnapshot.SnapshotData = chunkState.GetData(snapshotSize, chunk.Capacity, writeIndex);
            currentSnapshot.SnapshotEntity = chunkState.GetEntity(snapshotSize, chunk.Capacity, writeIndex);
            currentSnapshot.SnapshotDynamicData = chunkState.GetDynamicDataPtr(writeIndex, chunk.Capacity, out currentSnapshot.SnapshotDynamicDataCapacity);
            // 调整 Snapshot 动态数据存储区大小，使其能够容纳 Chunk Buffer 内容
            if (currentSnapshot.SnapshotDynamicData == null || (currentSnapshot.SnapshotDynamicDataSize > currentSnapshot.SnapshotDynamicDataCapacity))
            {
                chunkState.EnsureDynamicDataCapacity(currentSnapshot.SnapshotDynamicDataSize, chunk.Capacity);
                // 更新 Chunk 状态
                chunkSerializationData[chunk] = chunkState;
                currentSnapshot.SnapshotDynamicData = chunkState.GetDynamicDataPtr(writeIndex, chunk.Capacity, out currentSnapshot.SnapshotDynamicDataCapacity);
                if(currentSnapshot.SnapshotDynamicData == null)
                    throw new InvalidOperationException("failed to create history snapshot storage for dynamic data buffer");
            }

#if NETCODE_DEBUG
            NetworkTick ackedExceededMbr = default;
            byte numExceededMbr = 0;
            UnityEngine.Debug.Assert(currentSnapshot.AvailableBaselines.IsCreated && currentSnapshot.AvailableBaselines.IsEmpty);
#endif
            // 避免 `ackTick` 触发无效 Tick 异常
            var ackTick = snapshotAck.LastReceivedSnapshotByRemote.IsValid ? snapshotAck.LastReceivedSnapshotByRemote : currentTick;

            currentSnapshot.NumInFlightBaselines = 0;
            int baseline = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;

            while (baseline != writeIndex)
            {
                var baselineTick = new NetworkTick {SerializedData = snapshotIndex[baseline]};
                if (baselineTick.IsValid)
                {
                    if (Hint.Unlikely(currentTick.TicksSince(baselineTick) >= GhostSystemConstants.MaxBaselineAge))
                    {
                        // 此处无需清除 Ack Mask，只要不将其加入可用 Baseline 列表即可
                        // `CanUseStaticOptimization` 仍然可能通过
#if NETCODE_DEBUG
                        if (chunkState.HasAckFlag(baseline))
                            ackedExceededMbr = baselineTick;
                        numExceededMbr++;
#endif
                    }
                    else
                    {
                        TryAck(ref chunkState, baseline, baselineTick);
                        if (chunkState.HasAckFlag(baseline))
                        {
                            currentSnapshot.AvailableBaselines.Add(new SnapshotBaseline
                            {
                                tick = snapshotIndex[baseline],
                                snapshot = chunkState.GetData(snapshotSize, chunk.Capacity, baseline),
                                entity = chunkState.GetEntity(snapshotSize, chunk.Capacity, baseline),
                                dynamicData = chunkState.GetDynamicDataPtr(baseline, chunk.Capacity, out var _),
                            });
                        }
                        else currentSnapshot.NumInFlightBaselines += (byte)math.select(0, 1, baselineTick.IsNewerThan(ackTick));
                    }
                }

                baseline = (GhostSystemConstants.SnapshotHistorySize + baseline - 1) %
                           GhostSystemConstants.SnapshotHistorySize;
            }

#if NETCODE_DEBUG
            if (Hint.Unlikely(numExceededMbr > 0))
                PacketDumpExceededMaxBaselineAge(in chunk, ackedExceededMbr, numExceededMbr, currentSnapshot.AvailableBaselines.Length);
#endif
        }

        /// <summary>
        /// 假定此 Chunk 尚未被 Ack，并尝试进行 Ack
        /// </summary>
        /// <remarks>
        /// 注意：客户端报告错误时，还必须撤销 Chunk 的 Ack，
        /// 方法是清除其 <see cref="NetworkSnapshotAck.IsReceivedByRemote"/> Ack 历史
        /// </remarks>
        private void TryAck(ref GhostChunkSerializationState chunkState, int baseline, in NetworkTick baselineTick)
        {
            var wasAcked = chunkState.HasAckFlag(baseline);
            var isNowAcked = snapshotAck.IsReceivedByRemote(baselineTick, backupValue: wasAcked);
            if (wasAcked != isNowAcked)
            {
                if (Hint.Likely(isNowAcked))
                {
                    chunkState.SetAckFlag(baseline);
                    PacketDumpAckedChunk(baselineTick);
                }
                else
                {
                    chunkState.ClearAckFlag(baseline);
                    PacketDumpClearedAckChunk(baselineTick);
                }
            }
        }

        private void FindBaselines(int entIdx, Entity ent, in CurrentSnapshotState currentSnapshot, ref int baseline0, ref int baseline1, ref int baseline2, bool useSingleBaseline)
        {
            int numAvailableBaselines = currentSnapshot.AvailableBaselines.Length;
            var availableBaselines = (SnapshotBaseline*)currentSnapshot.AvailableBaselines.GetUnsafeReadOnlyPtr();
            baseline0 = 0;
            while (baseline0 < numAvailableBaselines && availableBaselines[baseline0].entity[entIdx] != ent)
                ++baseline0;
            if (useSingleBaseline)
                return;
            baseline1 = baseline0+1;
            while (baseline1 < numAvailableBaselines && availableBaselines[baseline1].entity[entIdx] != ent)
                ++baseline1;
            baseline2 = baseline1+1;
            while (baseline2 < numAvailableBaselines && availableBaselines[baseline2].entity[entIdx] != ent)
                ++baseline2;
            if (baseline2 >= numAvailableBaselines)
            {
                baseline1 = numAvailableBaselines;
                baseline2 = numAvailableBaselines;
            }
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpStructuralChange(in PrioChunk ghostType)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString512Bytes)$", structural change detected (new count:{ghostType.chunk.Count})");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpExceededMaxBaselineAge(in ArchetypeChunk chunk, NetworkTick ackedExceededMbr, byte numExceededMbr, int availableBaselinesLength)
        {
#if NETCODE_DEBUG
            // 只有在实际导致最近已 Ack Baseline 失效时才发出警告
            if (Hint.Likely(!ackedExceededMbr.IsValid || availableBaselinesLength != 0)) return;
            netDebug.LogWarning((FixedString512Bytes)$"[GCS] Ghost chunk {chunk.SequenceNumber} - sending to NID[{NetworkId}] - lost it's acked baseline:{ackedExceededMbr.ToFixedString()} as was older than MaxBaselineAge ticks from currentTick:{currentTick.ToFixedString()}!");
            if (Hint.Unlikely(netDebugPacket.IsCreated))
                netDebugPacketDebug.Append((FixedString64Bytes) $"\tWARN: B:{numExceededMbr}>MaxBaselineAge:{ackedExceededMbr.ToFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGhostCount(int ghostType, int relevantGhostCount)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format(", RelevantGhostCount:{2}", ghostTypeName, ghostType, relevantGhostCount));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBegin(ref ArchetypeChunk chunk, int start, int end)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString64Bytes)$"\n\t\t[SerializeEntities:{chunk.SequenceNumber}|{start}->{end}]");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBaseline(int entOffset, NetworkTick base0, NetworkTick base1, NetworkTick base2, int sameBaselineCount, bool useSingleBaseline, int numAvailableBaselines, int numPrespawnBaselines, byte numInFlightSnapshots)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (entOffset != 0) netDebugPacketDebug.Append((FixedString32Bytes)"\n\t\t");
                netDebugPacketDebug.Append((FixedString512Bytes)$" B0:{base0.ToFixedString()} B1:{base1.ToFixedString()} B2:{base2.ToFixedString()} SameBL:{sameBaselineCount}, UseSingleBL:{useSingleBaseline}, AvailBLs:{numAvailableBaselines}, PrespawnBLs:{numPrespawnBaselines}, inFl:{numInFlightSnapshots}/{GhostSystemConstants.SnapshotHistorySize}");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpForceResendPrespawn(bool forceResendExisting)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated && forceResendExisting)
                netDebugPacketDebug.Append((FixedString32Bytes) " FRP!");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpMovedAckHistory(NetworkTick networkTick, bool didHaveAck, bool hasAck)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                var didHaveAckC = (didHaveAck ? '1' : '0');
                var hasAckIntC = (hasAck ? '1' : '0');
                netDebugPacketDebug.Append((FixedString32Bytes)$", MOVE:{networkTick.ToFixedString()}:{didHaveAckC}-{hasAckIntC}");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGhostID(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes) $"\n\t\t\tGID:{ghostId}");
#endif
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSpawnTick(NetworkTick spawnTick)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format(" SpawnTick:{0}", spawnTick.ToFixedString()));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpChangeMasks(uint* changeMaskUints, int numChangeMaskUints)
        {
#if NETCODE_DEBUG
            if (!netDebugPacket.IsCreated)
                return;
            for (int i = 0; i < numChangeMaskUints; ++i)
                netDebugPacketDebug.Append(FixedString.Format(" ChangeMask:{0}", NetDebug.PrintMask(changeMaskUints[i])));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpHasRelevancySpawns()
        {
#if NETCODE_DEBUG
            netDebugPacketDebug.Append((FixedString32Bytes)", HasRelevancySpawns");
#endif
        }
#if NETCODE_DEBUG
        private void PacketDumpComponentSize(in GhostCollectionPrefabSerializer typeData, int* entityStartBit, int bitCountsPerComponent, int entOffset, ref NativeList<UnsafeGhostStatsSnapshot.PerComponentStats> tempComponentStats)
        {
            var packetDumpEnabled = netDebugPacket.IsCreated;

            uint total = 0;

            for (int comp = 0; comp < typeData.NumComponents; ++comp)
            {
                uint numBits = (uint)entityStartBit[(bitCountsPerComponent*comp + entOffset)*2+1];
                tempComponentStats.ElementAt(comp).SizeInSnapshotInBits += numBits;
                if (packetDumpEnabled)
                {
                    if (netDebugPacketDebug.Length > (netDebugPacketDebug.Capacity >> 1))
                    {
                        netDebugPacketDebug.Append((FixedString32Bytes)" CONT");
                        PacketDumpFlush();
                    }

                    total += numBits;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    var ghostComponent = GhostComponentCollection[serializerIdx];
                    var typeName = netDebug.ComponentTypeNameLookup[ghostComponent.ComponentType.TypeIndex];
                    netDebugPacketDebug.Append(FixedString.Format(" {0}:{1} ({2}b)", typeName, ghostComponent.PredictionErrorNames, numBits));
                }
            }
            if (packetDumpEnabled)
                netDebugPacketDebug.Append(FixedString.Format(" Total ({0}b)", total));
            // TODO：之后某些条件会取消操作并重置 Data Stream，此时也应取消统计数据收集
        }
#endif
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSkipInvalidGroup(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("Skip invalid GhostGroup GID:{0}\n", ghostId));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpBeginGroup(int grpLen)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("\n\t\t\tGhostGroup.Len:{0}\n\t\t\t[", grpLen));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpEndGroup(bool success)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if(success)
                    netDebugPacketDebug.Append((FixedString32Bytes) "\t\t\t] Ok!");
                else netDebugPacketDebug.Append((FixedString32Bytes) "\t\t\t] Failed!");
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpGroupItem(int ghostChildIdx, int grpLen, int ghostType)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append(FixedString.Format("\n\t\t\t\t[{0}/{1}] GhostType:{2}", ghostChildIdx, grpLen, ghostType));
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        internal void PacketDumpFlush()
        {
#if NETCODE_DEBUG
            if (!netDebugPacket.IsCreated || netDebugPacketDebug.IsEmpty)
                return;
            netDebugPacket.Log(netDebugPacketDebug);
            netDebugPacketDebug.Clear();
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_ZeroChangeOptimizedChunk(ref DataStreamWriter dataStream, ref DataStreamWriter oldStream, ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = $"Undid write of static as allowed to, saving {(dataStream.LengthInBits-oldStream.LengthInBits)}b! Set {chunkState.ZeroChangeFixedString()} LU:{chunkState.GetLastUpdate()}";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_WriterFullBeforeSerialize()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = "DidFillPacket before serialize!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpSerializeChunk(in ArchetypeChunk chunk, int ghostType)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString512Bytes)$"\n\t[SerializeChunk:{chunk.SequenceNumber}] {ghostTypeName}({ghostType}), Count:{chunk.Count}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONotStatic(in GhostCollectionPrefabSerializer typeData)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (typeData.StaticOptimization == 0)
                    netDebugPacketDebug.Append((FixedString32Bytes) ", CUSO=false as dynamic");
                else if (typeData.IsGhostGroup != 0)
                    netDebugPacketDebug.Append((FixedString32Bytes) ", CUSO=false as GhostGroup");
                else
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    UnityEngine.Debug.Assert(typeData.NumChildComponents > 0);
#endif
                    netDebugPacketDebug.Append((FixedString64Bytes) $", CUSO=false as has {typeData.NumChildComponents} replicated child comps");
                }
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_CUSOSuccess(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString128Bytes)$", CUSO=true as acked {chunkState.ZeroChangeFixedString()}]");
                netDebugPacketResult = "CUSO early out!";
            }
#endif
        }

        private void PacketDumpResult_SnapshotHistorySaturated(int ghostType, in ArchetypeChunk chunk, byte numInFlightBaselines, int ticksSinceLastReceive, bool bypassSnapshotHistoryFull)
        {
            if(Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                netDebug.LogWarning($"PERFORMANCE: Snapshot history is saturated for ghost chunk:{chunk.SequenceNumber}, ghostType:{ghostType}, {numInFlightBaselines}/{GhostSystemConstants.SnapshotHistorySize} in-flight (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks}), sent anyway:{bypassSnapshotHistoryFull}!");
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
            {
                if (!bypassSnapshotHistoryFull)
                    netDebugPacketResult = $"{numInFlightBaselines}/{GhostSystemConstants.SnapshotHistorySize} in-flight (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks}), cancelled send!";
                else
                    netDebugPacketDebug.Append((FixedString512Bytes) $", bypassing in-flight cap during lag spike (TSLR:{ticksSinceLastReceive}<={expectedSnapshotRttInSimTicks})");
            }
#endif
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOAnyGhostComponentChanged(int compIdx)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
            {
                netDebugPacketDebug.Append((FixedString512Bytes)$", CUSO=false as chunk.DidChange on ");
                netDebugPacketDebug.Append(ghostChunkComponentTypesPtr[compIdx].ToFixedString());
            }
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONoZC()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)", CUSO=false as no ZC");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSONotYetAckedZC()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", CUSO=false as not yet acked any ZC!");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOHasAckedZC(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", acked{chunkState.ZeroChangeFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOImplicitAck(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$", implctAcked{chunkState.ZeroChangeFixedString()}! ");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOHasRelevancyChanges()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes)", CUSO=false as relevancy changes");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpCUSOOrderChanged()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString64Bytes)", CUSO=false as chunk.DidOrderChange");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_DynamicFullSend()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Full & dynamic send, so reset ZC!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_StaticFullSend(ref GhostChunkSerializationState chunkState)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = $"Full & static send as has changes since last acked baseline, so set {chunkState.ZeroChangeFixedString()}!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_PartialSend()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Partial send, so reset ZC!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_PacketFullBeforeOneEntity()
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketResult = "Packet full before writing any ghosts in this chunk!";
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpResult_NoRelevantGhostsInChunk()
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketResult = "Skipped chunk as AllIrrelevant!";
#endif
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateGhostComponentIndex(int compIdx)
        {
            if (compIdx >= ghostChunkComponentTypesLength)
                throw new InvalidOperationException("Component index out of range");
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateNoNestedGhostGroups(int isGhostGroup)
        {
            if (isGhostGroup != 0)
                throw new InvalidOperationException("Nested ghost groups are not supported, non-root members of a group cannot be roots for their own groups.");
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidateGhostType(int entityGhostType, int ghostType)
        {
            if (entityGhostType != ghostType && entityGhostType >= 0)
            {
                // FIXME：需要怎样才能支持此情况，是否应将其视为重新生成
                throw new InvalidOperationException(
                    "A ghost changed type, ghost must keep the same serializer type throughout their lifetime");
            }
        }
        [Conditional("NETCODE_DEBUG")]
        private void ComponentScopeBegin(int serializerIdx)
        {
            #if NETCODE_DEBUG
            if (systemData.EnablePerComponentProfiling)
                GhostComponentCollection[serializerIdx].ProfilerMarker.Begin();
            #endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void ComponentScopeEnd(int serializerIdx)
        {
            #if NETCODE_DEBUG
            if (systemData.EnablePerComponentProfiling)
                GhostComponentCollection[serializerIdx].ProfilerMarker.End();
            #endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpAckedChunk(NetworkTick baselineTick)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString32Bytes)$", ACKED:{baselineTick.ToFixedString()}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpClearedAckChunk(NetworkTick baselineTick)
        {
#if NETCODE_DEBUG
            if(netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString32Bytes)$", CLEAR-ACKED:{baselineTick.ToFixedString()}");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpHasFailedWritesDuringSerializeEntities(int ent)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tFilled packet writer, undoing write of ent:{ent}!\n");
#endif
        }
        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpIrrelevant(int ghostId)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tGID:{ghostId} -- Irrelevant!\n");
#endif
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidatePrespawnBaseline(Entity ghost, int ghostId, int ent, int baselinesCount)
        {
            if(!PrespawnHelper.IsPrespawnGhostId(ghostId))
                throw new InvalidOperationException("Invalid prespawn ghost id. All prespawn ghost ids must be < 0");
            if (baselinesCount <= ent)
                throw new InvalidOperationException($"Could not find prespawn baseline data for entity {ghost.Index}:{ghost.Version}.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void ValidatePrespawnSpaceForDynamicData(int prespawnBaselineLength, int prespawnSnapshotSize)
        {
            if (prespawnBaselineLength == prespawnSnapshotSize)
                throw new InvalidOperationException("Prespawn baseline does not have have space for dynamic buffer data");
        }

        /// <summary>
        /// - 将预测 Component Data 写入 Snapshot
        /// - 将 Snapshot 写入 DataStream Writer
        /// </summary>
        /// <remarks>遍历 Ghost Group 时会递归调用</remarks>
        /// <param name="dataStream">用于写入经过预测压缩 Snapshot 的 Transport 写入流</param>
        /// <param name="skippedEntityCount"></param>
        /// <param name="anyChangeMask"></param>
        /// <param name="ghostType"></param>
        /// <param name="chunk">包含这些 Ghost 及其 Component 的 Chunk</param>
        /// <param name="startIndex">第一个待处理 Entity 的索引</param>
        /// <param name="endIndex">最后一个待处理 Entity 之后的下一个 Entity 索引，不包含该索引</param>
        /// <param name="useSingleBaseline"></param>
        /// <param name="currentSnapshot"></param>
        /// <param name="baselinesPerEntity"></param>
        /// <param name="sameBaselinePerEntity"></param>
        /// <param name="dynamicDataLenPerEntity"></param>
        /// <param name="entityStartBit">每个 Entity 的每个 Component 存储两个 int，第一个是 Writer 中该 Component 写入起点的位 Offset，第二个是该 Component 写入的位数</param>
        /// <returns>处理结束时所在的 Entity 索引</returns>
        /// <exception cref="InvalidOperationException"></exception>
        private int SerializeEntities(ref DataStreamWriter dataStream, out int skippedEntityCount, out uint anyChangeMask,
            int ghostType, ArchetypeChunk chunk, int startIndex, int endIndex, bool useSingleBaseline, in CurrentSnapshotState currentSnapshot,
            byte** baselinesPerEntity = null, int* sameBaselinePerEntity = null, int* dynamicDataLenPerEntity = null, int* entityStartBit = null)
        {
            PacketDumpBegin(ref chunk, startIndex, endIndex);
#if NETCODE_DEBUG
            using var tempComponentStats = new NativeList<UnsafeGhostStatsSnapshot.PerComponentStats>(20, Allocator.Temp); // SerializeEntities 的每次递归调用都需要独立临时缓冲区
#endif
            skippedEntityCount = 0;
            anyChangeMask = 0;

            var realStartIndex = startIndex;

            if (currentSnapshot.relevancyData != null)
            {
                // 跳过 Chunk 开头的 Irrelevant Entity，不对其进行序列化
                while (currentSnapshot.relevancyData[startIndex] == 0)
                {
                    currentSnapshot.SnapshotEntity[startIndex] = Entity.Null;
                    ++startIndex;
                    ++skippedEntityCount;
                }
                // 全部 Entity 都 Irrelevant 时无需处理
                if (startIndex >= endIndex)
                    return endIndex;
            }

            var typeData = GhostTypeCollection[ghostType];
            int snapshotSize = typeData.SnapshotSize;
            int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
            int enableableMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

            var ghostEntities = chunk.GetNativeArray(entityType);
            var ghosts = chunk.GetNativeArray(ref ghostComponentType);
            NativeArray<GhostCleanup> ghostSystemState = default;
            if (currentSnapshot.SnapshotData != null)
                ghostSystemState = chunk.GetNativeArray(ref ghostSystemStateType);

            byte* snapshot;
            if (currentSnapshot.SnapshotData == null)
                snapshot = (byte*)UnsafeUtility.Malloc(snapshotSize * (endIndex-startIndex), 16, Allocator.Temp);
            else
            {
                snapshot = (byte*) currentSnapshot.SnapshotData;
                snapshot += startIndex * snapshotSize;
            }

            // 设置每个 Entity 使用的 Baseline 指针，并计算连续使用同一组 Baseline 的 Entity 数量
            var numAvailableBaselines = currentSnapshot.AvailableBaselines.Length;
            if (baselinesPerEntity == null)
                baselinesPerEntity = tempBaselinesPerEntity;
            if (dynamicDataLenPerEntity == null)
                dynamicDataLenPerEntity = tempDynamicDataLenPerEntity;
            if (sameBaselinePerEntity == null)
                sameBaselinePerEntity = tempSameBaselinePerEntity;
            if (entityStartBit == null)
                entityStartBit = tempEntityStartBit;

            int baseline0 = numAvailableBaselines;
            int baseline1 = numAvailableBaselines;
            int baseline2 = numAvailableBaselines;
            int sameBaseline0 = -1;
            int sameBaseline1 = -1;
            int sameBaseline2 = -1;
            int sameBaselineIndex = 0;
            int lastRelevantEntity = startIndex+1;
            int baseGhostId = chunk.Has(ref PrespawnIndexType) ? unchecked((int)PrespawnHelper.PrespawnGhostIdBase) : 1;
            var baseSpawnTick = currentTick; // 压缩优化：假定 Ghost 的 SpawnTick 更接近 currentTick - 1，而不是 0
            baseSpawnTick.Decrement();
            var isPrespawn = chunk.Has(ref prespawnBaselineTypeHandle);
            int numPrespawnBaselines = 0;
            for (int ent = startIndex; ent < endIndex; ++ent)
            {
                var baselineIndex = ent - startIndex;
                dynamicDataLenPerEntity[baselineIndex] = 0;
                // 确保设置此 Snapshot 的 Tick，使序列化代码能从当前 Snapshot 和 Baseline 中读取它
                *(uint*)(snapshot + snapshotSize * (baselineIndex)) = currentTick.SerializedData;

                int offset = baselineIndex*4;
                baselinesPerEntity[offset] = null;
                baselinesPerEntity[offset+1] = null;
                baselinesPerEntity[offset+2] = null;
                baselinesPerEntity[offset+3] = null;

                if (currentSnapshot.relevancyData != null && currentSnapshot.relevancyData[ent] == 0)
                {
                    currentSnapshot.SnapshotEntity[ent] = Entity.Null;
                    // FIXME：如果能够提升性能，也应跳过 Chunk 中间 Irrelevant Ghost 的序列化代码
                    sameBaselinePerEntity[ent-startIndex] = -1;
                    continue;
                }
                lastRelevantEntity = ent+1;

                FindBaselines(ent, ghostEntities[ent], currentSnapshot, ref baseline0, ref baseline1, ref baseline2, useSingleBaseline);

                // 计算每个 Entity 的相同 Baseline 数量，值为 0 表示它属于前一连续区段
                if (baseline0 == sameBaseline0 && baseline1 == sameBaseline1 && baseline2 == sameBaseline2)
                {
                    // 与当前连续区段使用同一组 Baseline，更新区段长度
                    sameBaselinePerEntity[sameBaselineIndex] = sameBaselinePerEntity[sameBaselineIndex] + 1;
                    sameBaselinePerEntity[baselineIndex] = 0;
                }
                else
                {
                    // 使用不同的 Baseline 组，开始新的连续区段
                    sameBaselineIndex = baselineIndex;
                    sameBaselinePerEntity[sameBaselineIndex] = 1;

                    sameBaseline0 = baseline0;
                    sameBaseline1 = baseline1;
                    sameBaseline2 = baseline2;
                }
                if (baseline0 < numAvailableBaselines)
                {
                    baselinesPerEntity[offset] = (currentSnapshot.AvailableBaselines[baseline0].snapshot) + ent*snapshotSize;
                    baselinesPerEntity[offset+3] = (currentSnapshot.AvailableBaselines[baseline0].dynamicData);
                }
                if (baseline2 < numAvailableBaselines)
                {
                    baselinesPerEntity[offset+1] = (currentSnapshot.AvailableBaselines[baseline1].snapshot) + ent*snapshotSize;
                    baselinesPerEntity[offset+2] = (currentSnapshot.AvailableBaselines[baseline2].snapshot) + ent*snapshotSize;
                }

                if (baseline0 == numAvailableBaselines && isPrespawn && chunk.Has(ref PrespawnIndexType) &&
                    (ghostStateData.GetGhostState(ghostSystemState[ent]).Flags & ConnectionStateData.GhostStateFlags.HasBeenDespawnedAtLeastOnce) == 0)
                {
                    var prespawnBaselines = chunk.GetBufferAccessor(ref prespawnBaselineTypeHandle);
                    ValidatePrespawnBaseline(ghostEntities[ent], ghosts[ent].ghostId,ent,prespawnBaselines.Length);
                    if (prespawnBaselines[ent].Length > 0)
                    {
                        numPrespawnBaselines++;
                        var baselinePtr = (byte*)prespawnBaselines[ent].GetUnsafeReadOnlyPtr();
                        baselinesPerEntity[offset] = baselinePtr;
                        if (typeData.NumBuffers > 0)
                        {
                            ValidatePrespawnSpaceForDynamicData(prespawnBaselines[ent].Length, snapshotSize);
                            baselinesPerEntity[offset + 3] = baselinePtr + snapshotSize;
                        }
                    }
                }
            }
            // 更新结束索引，跳过 Chunk 末尾的 Irrelevant Entity
            int realEndIndex = endIndex;
            endIndex = lastRelevantEntity;
            int entityOffset = endIndex-startIndex;
            int snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) +
                                                                           (changeMaskUints * sizeof(uint)) +
                                                                           (enableableMaskUints * sizeof(uint)));
            int snapshotMaskOffsetInBits = 0;

            int dynamicDataHeaderSize = GhostChunkSerializationState.GetDynamicDataHeaderSize(chunk.Capacity);
            int snapshotDynamicDataOffset = dynamicDataHeaderSize;
            int dynamicSnapshotDataCapacity = currentSnapshot.SnapshotDynamicDataCapacity;

            byte* snapshotDynamicDataPtr = currentSnapshot.SnapshotDynamicData;
            // 生成新 Entity 并首次发送该 Chunk 时可能满足此条件
            if (typeData.NumBuffers > 0 && currentSnapshot.SnapshotDynamicData == null && currentSnapshot.SnapshotDynamicDataSize > 0)
            {
                snapshotDynamicDataPtr = (byte*)UnsafeUtility.Malloc(currentSnapshot.SnapshotDynamicDataSize + dynamicDataHeaderSize, 16, Allocator.Temp);
                dynamicSnapshotDataCapacity = currentSnapshot.SnapshotDynamicDataSize;
            }
            var oldTempWriter = tempWriter;

            SnapshotPreSerializeData preSerializedSnapshot = default;
            var hasPreserializeData = chunk.Has(ref preSerializedGhostType) && SnapshotPreSerializeData.TryGetValue(chunk, out preSerializedSnapshot);
            var hasCustomSerializer = systemData.UseCustomSerializer != 0 && typeData.CustomSerializer.Ptr.IsCreated;
            var lastSerializedEntity = endIndex;

            if (hasPreserializeData)
            {
                UnsafeUtility.MemCpy(snapshot, (byte*)preSerializedSnapshot.Data+snapshotSize*startIndex, snapshotSize*(endIndex-startIndex));
                // 如果此 Chunk 在本 Tick 中已经处理过，则不能复制动态 Snapshot Data，
                // 否则会覆盖已计算的 ChangeMask 并破坏 Delta Compression
                // 只有 Ghost Group 的非根成员会在同一 Tick 中多次发送同一 Chunk
                if (preSerializedSnapshot.DynamicSize > 0 && currentSnapshot.AlreadyUsedChunk == 0)
                    UnsafeUtility.MemCpy(snapshotDynamicDataPtr + dynamicDataHeaderSize, (byte*)preSerializedSnapshot.Data+preSerializedSnapshot.Capacity, preSerializedSnapshot.DynamicSize);
            }

            if (hasCustomSerializer)
            {
                var context = new GhostPrefabCustomSerializer.Context
                {
                    startIndex = startIndex,
                    endIndex = endIndex,
                    ghostType = ghostType,
                    networkId = NetworkId,
                    childEntityLookup = childEntityLookup,
                    serializerState = serializerState,
                    ghostChunkComponentTypes = (IntPtr)ghostChunkComponentTypesPtr,
                    linkedEntityGroupTypeHandle = linkedEntityGroupType,
                    snapshotDataPtr = (IntPtr)snapshot,
                    baselinePerEntityPtr = (IntPtr)baselinesPerEntity,
                    sameBaselinePerEntityPtr = (IntPtr)sameBaselinePerEntity,
                    snapshotDynamicDataPtr = (IntPtr)snapshotDynamicDataPtr,
                    dynamicDataSizePerEntityPtr = (IntPtr)dynamicDataLenPerEntity,
                    zeroBaseline = (IntPtr)tempZeroBaseline,
                    entityStartBit = (IntPtr)entityStartBit,
                    ghostInstances = (IntPtr)ghosts.GetUnsafeReadOnlyPtr(),
                    snapshotOffset = snapshotOffset,
                    snapshotStride = snapshotSize,
                    hasPreserializedData = (byte)(hasPreserializeData
                        ? 1
                        : 0),
                    dynamicDataOffset = dynamicDataHeaderSize,
                    dynamicDataCapacity = dynamicSnapshotDataCapacity + dynamicDataHeaderSize
                };
                typeData.CustomSerializer.Ptr.Invoke(ref chunk,
                    typeData, GhostComponentIndex,
                    ref context,
                    ref tempWriter, compressionModel,
                    ref lastSerializedEntity);
                // 此时 Temp Writer 只会因为连单个 Entity 都没有足够空间而失败
                // 无需重试整个 Chunk，因为 Temp Writer 大小不会变化，可以确定它无法装入当前 Data Stream
                if (tempWriter.HasFailedWrites)
                {
                    return startIndex;
                }
            }
            else
            {
                if (hasPreserializeData)
                {
                    int numComponents = typeData.NumComponents;
                    for (int comp = 0; comp < numComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ValidateGhostComponentIndex(compIdx);

                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.PostSerializeBuffer.Invoke((IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                ghostSerializer.ChangeMaskBits, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            }
                        }
                        else
                        {
                            // TODO：确保 ZeroSize 情况下不调用这些指针，但仍必须更新 entityStartBit
                            // 完成后可以移除 Serializer Template 中的 #ifdef
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.PostSerialize.Invoke((IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp));
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                            }
                        }
                    }
                }
                else
                {
                    // 遍历全部 Component 并调用 Serialize 方法，将 Snapshot Data 写入并把 Entity 序列化到临时数据流
                    int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                    int enableableMaskOffset = 0;
                    for (int comp = 0; comp < numBaseComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ValidateGhostComponentIndex(compIdx);
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var compSize = ghostSerializer.ComponentSize;
                        byte** compData = tempComponentDataPerEntity;
                        int* compDataLen = tempComponentDataLenPerEntity;
                        for (int ent = startIndex; ent < endIndex; ++ent)
                        {
                            compData[ent-startIndex] = null;
                            compDataLen[ent-startIndex] = 0;
                        }
                        // 即使不访问数据，也始终需要按 Component SnapshotSize 增加 Offset
                        // 否则下一个序列化 Component 会把数据复制到错误的内存槽位
                        // 某些情况下可能暂时正常，但该 Snapshot 进入历史并用于插值数据时可能产生错误结果

                        if (ghostSerializer.SerializesEnabledBit != 0)
                        {
                            var handle = ghostChunkComponentTypesPtr[compIdx];
                            UpdateEnableableMasks(chunk, startIndex, endIndex, ref handle, snapshot, changeMaskUints, enableableMaskOffset, snapshotSize);
                            ++enableableMaskOffset;
                            ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                        }
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            if (ghostSerializer.HasGhostFields && chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    compData[ent-startIndex] = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var len);
                                    compDataLen[ent-startIndex] = len;
                                }
                            }
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.SerializeBuffer.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                ghostSerializer.ChangeMaskBits, (IntPtr)compData, (IntPtr)compDataLen, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, ref snapshotDynamicDataOffset, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                            ComponentScopeEnd(serializerIdx);
                            if (ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                            }
                        }
                        else
                        {
                            if (ghostSerializer.HasGhostFields && chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var data = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                    compData[ent-startIndex] = data + ent * compSize;
                            }
                            ComponentScopeBegin(serializerIdx);
                            ghostSerializer.Serialize.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState), (IntPtr) snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, (IntPtr) compData, endIndex - startIndex, (IntPtr) baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr) (entityStartBit + 2 * entityOffset * comp));
                            ComponentScopeEnd(serializerIdx);
                            if(ghostSerializer.HasGhostFields)
                            {
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                            }
                        }
                    }
                    if (typeData.NumChildComponents > 0)
                    {
                        var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                        for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                        {
                            int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                            int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                            ValidateGhostComponentIndex(compIdx);
                            ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                            var compSize = ghostSerializer.ComponentSize;
                            byte** compData = tempComponentDataPerEntity;
                            int* compDataLen = tempComponentDataLenPerEntity;
                            for (int ent = startIndex; ent < endIndex; ++ent)
                            {
                                compData[ent-startIndex] = null;
                                compDataLen[ent - startIndex] = 0;
                            }
                            if(ghostSerializer.ComponentType.IsBuffer)
                            {
                                var snapshotPtr = snapshot;
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                    {
                                        if (ghostSerializer.SerializesEnabledBit != 0)
                                        {
                                            var entityIndex = childChunk.IndexInChunk;
                                            var handle = ghostChunkComponentTypesPtr[compIdx];
                                            UpdateEnableableMasks(childChunk.Chunk, entityIndex, entityIndex+1, ref handle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                        }
                                        if (ghostSerializer.HasGhostFields)
                                        {
                                            var bufData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                            compData[ent-startIndex] = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.IndexInChunk, out var len);
                                            compDataLen[ent-startIndex] = len;
                                        }
                                    }
                                    snapshotPtr += snapshotSize;
                                }
                                ComponentScopeBegin(serializerIdx);
                                ghostSerializer.SerializeBuffer.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState), (IntPtr)snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits,
                                    ghostSerializer.ChangeMaskBits, (IntPtr)compData, (IntPtr)compDataLen, endIndex - startIndex, (IntPtr)baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr)(entityStartBit+2*entityOffset*comp), (IntPtr)snapshotDynamicDataPtr, ref snapshotDynamicDataOffset, (IntPtr)dynamicDataLenPerEntity, dynamicSnapshotDataCapacity + dynamicDataHeaderSize);
                                ComponentScopeEnd(serializerIdx);
                                if (ghostSerializer.HasGhostFields)
                                {
                                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                                    snapshotMaskOffsetInBits += GhostComponentSerializer.DynamicBufferComponentMaskBits;
                                }
                                if (ghostSerializer.SerializesEnabledBit != 0)
                                {
                                    ++enableableMaskOffset;
                                    ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                                }
                            }
                            else
                            {
                                var snapshotPtr = snapshot;
                                for (int ent = startIndex; ent < endIndex; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                    compData[ent-startIndex] = null;
                                    // 此处可以跳过，因为内存缓冲区 Offset 使用起止 Entity 索引计算
                                    if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                    {
                                        if (ghostSerializer.SerializesEnabledBit != 0)
                                        {
                                            var entityIndex = childChunk.IndexInChunk;
                                            var handle = ghostChunkComponentTypesPtr[compIdx];
                                            UpdateEnableableMasks(childChunk.Chunk, entityIndex, entityIndex + 1, ref handle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                        }

                                        if (ghostSerializer.HasGhostFields)
                                        {
                                            compData[ent - startIndex] = (byte*) childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                            compData[ent - startIndex] += childChunk.IndexInChunk * compSize;
                                        }
                                    }

                                    snapshotPtr += snapshotSize;
                                }
                                ComponentScopeBegin(serializerIdx);
                                ghostSerializer.Serialize.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState), (IntPtr) snapshot, snapshotOffset, snapshotSize, snapshotMaskOffsetInBits, (IntPtr) compData, endIndex - startIndex, (IntPtr) baselinesPerEntity, ref tempWriter, ref compressionModel, (IntPtr) (entityStartBit + 2 * entityOffset * comp));
                                ComponentScopeEnd(serializerIdx);
                                if (ghostSerializer.HasGhostFields)
                                {
                                    snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                                    snapshotMaskOffsetInBits += ghostSerializer.ChangeMaskBits;
                                }
                                if (ghostSerializer.SerializesEnabledBit != 0)
                                {
                                    ++enableableMaskOffset;
                                    ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                                }
                            }
                        }
                    }
                    ValidateAllEnableBitsHasBeenWritten(enableableMaskOffset, typeData.EnableableBits);
                }
                if (tempWriter.HasFailedWrites)
                {
                    // 即使日志级别会跳过消息，字符串拼接仍会产生成本，因此至少用条件判断避免该开销
                    if (Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                    {
                        netDebug.LogWarning($"PERFORMANCE: Could not fit snapshot content into temporary buffer of size {tempWriter.Capacity}, increasing size to {tempWriter.Capacity*2} and trying again! If this happens frequently, increase the size of this buffer via `GhostSendSystemData.TempStreamInitialSize`.");
                    }
                    // 临时缓冲区无法容纳全部 Entity 内容，扩大后重试
                    tempWriter = new DataStreamWriter(tempWriter.Capacity*2, Allocator.Temp);
                    tempWriter.WriteBytes(oldTempWriter.AsNativeArray());
                    return SerializeEntities(ref dataStream, out skippedEntityCount, out anyChangeMask,
                        ghostType, chunk, realStartIndex, realEndIndex, useSingleBaseline, currentSnapshot,
                        baselinesPerEntity, sameBaselinePerEntity, dynamicDataLenPerEntity, entityStartBit);
                }
            }
            tempWriter.Flush();
            // 按正确顺序将每个 Entity 的内容从临时数据流复制到输出流
            var writerData = (uint*)tempWriter.AsNativeArray().GetUnsafePtr();
            uint zeroChangeMask = 0;
            bool hasPartialSends = false;
            if (typeData.PredictionOwnerOffset !=0)
            {
                hasPartialSends = ((typeData.PartialComponents != 0) && (typeData.OwnerPredicted != 0));
                hasPartialSends |= typeData.PartialSendToOwner != 0;
            }
            for (int ent = startIndex; ent < lastSerializedEntity; ++ent)
            {
                var oldStream = dataStream;
                int entOffset = ent-startIndex;
                var sameBaselineCount = sameBaselinePerEntity[entOffset];

                int offset = entOffset*sizeof(uint);
                var baseline = baselinesPerEntity[offset];
                if (sameBaselineCount != 0)
                {
                    if (sameBaselineCount < 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(currentSnapshot.SnapshotEntity[ent] == Entity.Null);
#endif
                        // 此 Ghost 为 Irrelevant，不发送任何内容
                        snapshot += snapshotSize;
                        ++skippedEntityCount;
                        PacketDumpIrrelevant(ghosts[ent].ghostId);
                        continue;
                    }
                    var baselineTick0 = (baseline != null) ? new NetworkTick{SerializedData = *(uint*)baseline} : currentTick;
                    var baselinePtr1 = baselinesPerEntity[offset + 1];
                    var baselinePtr2 = baselinesPerEntity[offset + 2];
                    var baselineTick1 = (baselinePtr1 != null) ? new NetworkTick{SerializedData = *(uint*)baselinePtr1} : currentTick;
                    var baselineTick2 = (baselinePtr2 != null) ? new NetworkTick{SerializedData = *(uint*)baselinePtr2} : currentTick;

                    uint baseDiff0 = baselineTick0.IsValid ? (uint)currentTick.TicksSince(baselineTick0) : GhostSystemConstants.MaxBaselineAge;
                    uint baseDiff1 = baselineTick1.IsValid ? (uint)currentTick.TicksSince(baselineTick1) : GhostSystemConstants.MaxBaselineAge;
                    uint baseDiff2 = baselineTick2.IsValid ? (uint)currentTick.TicksSince(baselineTick2) : GhostSystemConstants.MaxBaselineAge;
                    dataStream.WritePackedUInt(baseDiff0, compressionModel);
                    dataStream.WritePackedUInt(baseDiff1, compressionModel);
                    dataStream.WritePackedUInt(baseDiff2, compressionModel);
                    dataStream.WritePackedUInt((uint) sameBaselineCount, compressionModel);

                    PacketDumpBaseline(entOffset, baselineTick0, baselineTick1, baselineTick2, sameBaselineCount, useSingleBaseline, numAvailableBaselines, numPrespawnBaselines, currentSnapshot.NumInFlightBaselines);
                }

                var ghost = ghosts[ent];
                ValidateGhostType(ghost.ghostType, ghostType);

                // 写入 Ghost 和 Snapshot 中的 ChangeMask
                dataStream.WritePackedIntDelta(ghost.ghostId, baseGhostId, compressionModel);
                baseGhostId = ghost.ghostId + 1; // 压缩优化：假定下一个 GID 与前一个接近，
                                                 // 并且通常就是下一个已分配 GID，因为 Chunk 中相邻 Ghost 往往同时生成
                PacketDumpFlush();
                PacketDumpGhostID(ghost.ghostId);

                uint* changeMaskBaseline = (uint*)(baseline+sizeof(uint));
                uint* enableableMaskBaseline = (uint*)(baseline+sizeof(uint) + changeMaskUints * sizeof(uint));

                int changeMaskBaselineMask = ~0;
                int enableableMaskBaselineMask = ~0;

                var isNewGhostForClient = baseline == null;
                if (isNewGhostForClient)
                {
                    changeMaskBaseline = &zeroChangeMask;
                    enableableMaskBaseline = &zeroChangeMask;

                    changeMaskBaselineMask = 0;
                    enableableMaskBaselineMask = 0;

                    // 仅为运行时生成的 Ghost 序列化 Spawn Tick
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                    {
                        dataStream.WritePackedUIntDelta(ghost.spawnTick.SerializedData, baseSpawnTick.SerializedData, compressionModel);
                        baseSpawnTick = ghost.spawnTick; // 压缩优化：假定下一个 Spawn Tick 与前一个接近
                        PacketDumpSpawnTick(ghost.spawnTick);
                    }
                }

                uint prevDynamicSize = 0;
                uint curDynamicSize = 0;
                // 将动态数据大小写入 Snapshot，并相对当前可用 Baseline 进行 Delta Compression 后发送
                if (typeData.NumBuffers != 0)
                {
                    if(dynamicDataLenPerEntity[ent-startIndex] > dynamicSnapshotDataCapacity)
                        throw new InvalidOperationException("dynamic data size larger then the buffer capacity");
                    // Buffer 已从 Chunk 移除时可以为 null
                    if (snapshotDynamicDataPtr != null)
                    {
                        curDynamicSize = (uint) dynamicDataLenPerEntity[ent-startIndex];
                        // 在 Snapshot Data 中存储该 Entity 使用的动态大小，供 Delta Compression 使用
                        ((uint*) snapshotDynamicDataPtr)[ent] = curDynamicSize;
                        var baselineDynamicData = baselinesPerEntity[offset+3];
                        // Prespawn 数据编码方式不同，因此需要特殊处理
                        if (baselineDynamicData != null)
                        {
                            // 对 Prespawn Ghost，仅在 Tick 为 0 时使用后备 Baseline
                            if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId) && (*(uint*)baseline) == 0)
                                prevDynamicSize = ((uint*) baselineDynamicData)[0];
                            else
                                prevDynamicSize = ((uint*) baselineDynamicData)[ent];
                        }
                    }
                    else
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(dynamicDataLenPerEntity[entOffset]==0);
                        UnityEngine.Debug.Assert(currentSnapshot.SnapshotDynamicDataSize==0);
#endif
                    }
                }

                uint* changeMasks = (uint*)(snapshot+sizeof(uint));
                uint* enableableMasks = (uint*)(snapshot+sizeof(uint) + changeMaskUints * sizeof(uint));

                // 此逻辑不适用于自定义 Serializer，预期由自定义 Serializer 在序列化过程中自行完成
                if (hasPartialSends && !hasCustomSerializer)
                {
                    GhostSendType serializeMask = GhostSendType.AllClients;
                    var sendToOwner = SendToOwnerType.All;
                    var isOwner = (NetworkId == *(int*) (snapshot + typeData.PredictionOwnerOffset));
                    sendToOwner = isOwner ? SendToOwnerType.SendToOwner : SendToOwnerType.SendToNonOwner;
                    if (typeData.PartialComponents != 0 && typeData.OwnerPredicted != 0)
                        serializeMask = isOwner ? GhostSendType.OnlyPredictedClients : GhostSendType.OnlyInterpolatedClients;

                    var curMaskOffsetInBits = 0;
                    int curSnapshotDataOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + (changeMaskUints * sizeof(uint)) + (enableableMaskUints * sizeof(uint)));

                    // 补充说明
                    // 如果采用不同的 Component 排序，并允许以下顺序
                    // GhostOwner，始终为 0 或始终序列化，默认只需 1 位而不是 2 位
                    // 全部非可选 Component
                    // 全部可优化 Component，仅 Predicted
                    // 全部受 SendToOwner Mask 控制的 Component
                    // 全部可优化 Component，仅 Interpolated
                    // 则 ChangeMask 和 EnableMask 的总体排列可能更合理，并获得更好的 Delta Compression
                    // 额外收益是可能带来更多性能优化机会，因为部分逻辑可以按连续范围执行
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var changeBits = ghostSerializer.ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentMaskBits
                            : ghostSerializer.ChangeMaskBits;
                        var hasGhostFields = ghostSerializer.HasGhostFields;
                        int componentStride = ghostSerializer.ComponentType.IsBuffer
                            ? GhostComponentSerializer.DynamicBufferComponentSnapshotSize
                            : ghostSerializer.SnapshotSize;
                        componentStride = GhostComponentSerializer.SnapshotSizeAligned(componentStride);
                        // 此功能很少使用，只有少数 Entity，通常是玩家，上的少量 Component 会受益
                        // 因此关键是不能让这些处理拖慢序列化快速路径
                        // 不过这种情况下当前仍会执行无效工作，可在另一 PR 中优化
                        // 虽然 SIMD 可能仍使其更快，但考虑 DataStreamWriter 和 Huffman Compression 的特性，此处未必如此
                        if ((serializeMask & GhostComponentIndex[typeData.FirstComponent + comp].SendMask) == 0 ||
                            (sendToOwner & GhostComponentIndex[typeData.FirstComponent + comp].SendToOwner) == 0)
                        {
                            entityStartBit[(entityOffset*comp + entOffset)*2+1] = 0;
                            // 此处无需重置 EnableMask，因为它本身不是 ChangeMask
                            // 此外，在 uint Mask 内减少 1 位对压缩率的收益难以预测，很可能微乎其微
                            // 对稍后执行的 Delta Compression 而言，Enable Component 默认值为 true，
                            // 更好的默认值可能是 ~0 Mask，或者改为与 true 值进行异或编码
                            if (hasGhostFields)
                            {
                                // 此 Component 不应发送给该 Entity，清除 ChangeMask 和位数以阻止发送
                                GhostComponentSerializer.ResetChangeMask((IntPtr)changeMasks, curMaskOffsetInBits, changeBits);
                                // 理想情况下，此处应把 Snapshot Data 重置为预测 Baseline 的值，以便之后重发时使用更少位
                                // 但 Component 越久未发送，其值越可能已与该 Baseline 相差很大，
                                // 此时与直接使用默认 Baseline 相比可能没有明显差异
                                // 前一种方式的主要优势是保持 Snapshot 值一致，状态更干净
                                // 客户端已经遵循不再更新该 Component 的规则，因此 Snapshot Data 的值实际无关紧要，
                                // 尽管保留最近值会更理想
                                // 为简化处理，此处重置为 0，客户端收到 Component Data 更新时也执行相同操作
                                // 这会稍微增加接收端复杂度，但两端逻辑互为镜像，是保证一致性所必需的
                                var snapshotData = (uint*)(snapshot + curSnapshotDataOffset);
                                for(int i=0;i<componentStride/4;++i)
                                    snapshotData[i] = 0;
                            }
                            // FIXME：需要修改测试，确保 enableableMasks 同时包含 1 和 0
                            // 否则代码可能因为清除了错误的 1 而损坏，却无法被发现
                            // TODO：Buffer 也可以缩减所需动态缓冲区大小，以节省客户端内存
                        }
                        if (hasGhostFields)
                        {
                            curSnapshotDataOffset += componentStride;
                            curMaskOffsetInBits += changeBits;
                        }
                    }
                }
                // 确保清除 ChangeMask 末尾剩余的位
                if ((typeData.ChangeMaskBits&31) != 0)
                    GhostComponentSerializer.CopyToChangeMask((IntPtr)changeMasks, 0, typeData.ChangeMaskBits, 32 - (typeData.ChangeMaskBits&31));
                PacketDumpChangeMasks(changeMasks, changeMaskUints);

                uint anyChangeMaskThisEntity = 0;
                uint anyEnableableMaskChangedThisEntity = 0;
                if (GhostSystemConstants.SnapshotHasCompressedGhostSize)
                {
                    var headerLen = 0;
                    // 计算 Header 部分的压缩大小，并加入最终 Ghost 大小
                    if (typeData.NumBuffers != 0)
                    {
                        var compressedSize = GhostComponentSerializer.GetDeltaCompressedSizeInBits(curDynamicSize, prevDynamicSize, compressionModel);
                        headerLen += compressedSize;
                    }

                    for (int i = 0; i < changeMaskUints; ++i)
                    {
                        uint changeMaskUint = changeMasks[i];
                        anyChangeMaskThisEntity |= changeMaskUint;
                        headerLen += GhostComponentSerializer.GetDeltaCompressedSizeInBits(changeMaskUint, changeMaskBaseline[i & changeMaskBaselineMask], compressionModel);
                    }

                    for (int i = 0; i < enableableMaskUints; ++i)
                    {
                        uint enableBitUint = enableableMasks[i];
                        headerLen += GhostComponentSerializer.GetDeltaCompressedSizeInBits(enableBitUint, enableableMaskBaseline[i & enableableMaskBaselineMask], compressionModel);
                    }
                    int ghostSizeInBits = 0;
                    if (anyChangeMaskThisEntity != 0)
                    {
                        if (hasCustomSerializer)
                        {
                            ghostSizeInBits = entityStartBit[entOffset * 2 + 1];
                        }
                        else
                        {
                            for (int comp = 0; comp < typeData.NumComponents; ++comp)
                                ghostSizeInBits += entityStartBit[(entityOffset * comp + entOffset) * 2 + 1];
                        }
                    }
                    dataStream.WritePackedUIntDelta((uint)(ghostSizeInBits+headerLen), 0, compressionModel);
                }
                // 将动态数据大小写入 Snapshot，并相对当前可用 Baseline 进行 Delta Compression 后发送
                if (typeData.NumBuffers != 0)
                    dataStream.WritePackedUIntDelta(curDynamicSize, prevDynamicSize, compressionModel);

                for (int i = 0; i < changeMaskUints; ++i)
                {
                    uint changeMaskUint = changeMasks[i];
                    anyChangeMaskThisEntity |= changeMaskUint;
                    dataStream.WritePackedUIntDelta(changeMaskUint, changeMaskBaseline[i&changeMaskBaselineMask], compressionModel);
                }
                for (int i = 0; i < enableableMaskUints; ++i)
                {
                    uint enableableMaskUint = enableableMasks[i];
                    anyEnableableMaskChangedThisEntity |= enableableMaskUint ^ enableableMaskBaseline[i & enableableMaskBaselineMask];
                    dataStream.WritePackedUIntDelta(enableableMaskUint, enableableMaskBaseline[i & enableableMaskBaselineMask], compressionModel);
                }
                snapshot += snapshotSize;
                anyChangeMask |= anyChangeMaskThisEntity;
                anyChangeMask |= anyEnableableMaskChangedThisEntity;

#if NETCODE_DEBUG
                // Resize 只增大分配而不会缩小
                // 分析数据显示每个 Ghost 类型平均略多于 20 个 Component，因此 20 是合理初始值，不足时仍可扩容
                tempComponentStats.Resize(typeData.NumComponents, options: NativeArrayOptions.ClearMemory);
                tempComponentStats.ResetToDefault();
                var stats = tempComponentStats;
#endif
                if (anyChangeMaskThisEntity != 0)
                {
                    if (hasCustomSerializer)
                    {
#if NETCODE_DEBUG
                        PacketDumpComponentSize(typeData, entityStartBit+entityOffset*2, entityOffset, entOffset, ref stats);
#endif
                        int start = entityStartBit[(entOffset)*2];
                        int len = entityStartBit[(entOffset)*2+1];
                        if (len > 0)
                        {
                            while (len > 32)
                            {
                                dataStream.WriteRawBits(writerData[start++], 32);
                                len -= 32;
                            }
                            dataStream.WriteRawBits(writerData[start], len);
                        }
                    }
                    else
                    {
#if NETCODE_DEBUG
                        PacketDumpComponentSize(typeData, entityStartBit, entityOffset, entOffset, ref stats);
#endif
                        for (int comp = 0; comp < typeData.NumComponents; ++comp)
                        {
                            int start = entityStartBit[(entityOffset*comp + entOffset)*2];
                            int len = entityStartBit[(entityOffset*comp + entOffset)*2+1];
                            if (len > 0)
                            {
                                while (len > 32)
                                {
                                    dataStream.WriteRawBits(writerData[start++], 32);
                                    len -= 32;
                                }
                                dataStream.WriteRawBits(writerData[start], len);
                            }
                        }
                    }
                }

                if (dataStream.HasFailedWrites)
                {
                    // 回滚到最近的有效状态，并进一步限制可序列化的 Entity 数量
                    PacketDumpHasFailedWritesDuringSerializeEntities(ent);
                    dataStream = oldStream;
                    return ent;
                }

                if (typeData.IsGhostGroup != 0)
                {
                    PacketDumpFlush();
                    GhostSendSystem.s_GhostGroupMarker.Begin();
                    var ghostGroup = chunk.GetBufferAccessor(ref ghostGroupType)[ent];
                    // 序列化 Group 中的其他全部 Ghost，接收系统也必须正确处理
                    dataStream.WritePackedUInt((uint)ghostGroup.Length, compressionModel);
                    if (dataStream.HasFailedWrites)
                    {
                        PacketDumpFailedSerializeGhostGroup(ent);
                        dataStream = oldStream;
                        return ent;
                    }
                    PacketDumpBeginGroup(ghostGroup.Length);
                    PacketDumpFlush();

                    bool success = SerializeGroup(ref dataStream, ref compressionModel, ghostGroup, useSingleBaseline, ref anyChangeMaskThisEntity);
                    anyChangeMask |= anyChangeMaskThisEntity;

                    GhostSendSystem.s_GhostGroupMarker.End();

                    PacketDumpEndGroup(success);
                    PacketDumpFlush();
                    if (!success)
                    {
                        // Snapshot 不会发送，因此在设置 Entity 前中止
                        dataStream = oldStream;
                        return ent;
                    }
                }

#if NETCODE_DEBUG
                // 未发生取消操作，可以累加统计数据
                this.componentStats.IncrementWith(tempComponentStats);
#endif
                if (currentSnapshot.SnapshotData != null)
                {
                    currentSnapshot.SnapshotEntity[ent] = ghostEntities[ent];

                    // 将此 Entity 标记为已生成
                    ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[ent]);

                    // 静态优化系统需要区分以下所有组合
                    // A）新生成 Ghost、Prespawn Ghost、已有 Ghost
                    // B）存在部分 Baseline、完全没有 Baseline、存在 Prespawn Baseline
                    // C）相对 Baseline 或 `default(T)` 没有变化、存在变化
                    // D）Ghost 刚移入此 Chunk 且无 Baseline、Ghost 刚移入此 Chunk 且有 Baseline、Ghost 最近未移入此 Chunk
                    //
                    // 因此满足以下条件时发送 Ghost
                    // 1）此前已 Ack 的 Baseline 数量为零，包括 Prespawn Baseline，此时必须发送
                    //    这可能是新 Ghost，也可能是极少见的刚变化、刚移动或发送频繁的 Ghost
                    //    注意：Prespawn 会使用其特殊 Prespawn Baseline 自动在客户端生成
                    //    注意 2：Ghost 移到另一个 Chunk 时，其 Baseline 也可能一同迁移，参见 `UpdateChunkHistory`
                    // 2）anyChangeMask != 0 时必须发送，因为 Baseline 已过时，可以推断部分数据发生了变化
                    var hasRuntimeBaseline = baseline0 < numAvailableBaselines;
                    var hasValidAckedBaseline = hasRuntimeBaseline || (isPrespawn && numPrespawnBaselines > 0);
                    var wasSentWithChangesBefore = (ghostState.Flags & ConnectionStateData.GhostStateFlags.SentWithChanges) != 0;
                    var forceResendPrespawn = isPrespawn && anyChangeMaskThisEntity == 0 && wasSentWithChangesBefore && !hasRuntimeBaseline;
                    PacketDumpForceResendPrespawn(forceResendPrespawn);

                    anyChangeMaskThisEntity |= !hasValidAckedBaseline || forceResendPrespawn ? 1u : 0u;
                    anyChangeMask |= anyChangeMaskThisEntity;

                    ghostState.Flags |= ConnectionStateData.GhostStateFlags.IsRelevant;
                    if(anyChangeMaskThisEntity != 0)
                        ghostState.Flags |= ConnectionStateData.GhostStateFlags.SentWithChanges;
                }
                PacketDumpFlush();
            }

            if (hasCustomSerializer && lastSerializedEntity != endIndex)
                return lastSerializedEntity;
            // 全部 Entity 处理完成后，记得计入 Chunk 末尾跳过的 Entity
            skippedEntityCount += realEndIndex - endIndex;
            return realEndIndex;
        }

        [Conditional("NETCODE_DEBUG")]
        private void PacketDumpFailedSerializeGhostGroup(int ent)
        {
#if NETCODE_DEBUG
            if (netDebugPacket.IsCreated)
                netDebugPacketDebug.Append((FixedString128Bytes)$"\n\t\t\tFailed to serialize entity group. undoing write of ent:{ent}!\n");
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal static void ValidateAllEnableBitsHasBeenWritten(int enableableMaskOffset, int numEnableBits)
        {
            if (enableableMaskOffset != numEnableBits)
                throw new InvalidOperationException($"Written only {enableableMaskOffset} enable bits data which are less than the expected {numEnableBits} for this ghost type. This is a serialization/replication error.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal static void ValidateWrittenEnableBits(int enableableMaskOffset, int numEnableBits)
        {
            if (enableableMaskOffset > numEnableBits)
                throw new InvalidOperationException($"Written {enableableMaskOffset} enable bits, but expected to write exactly {numEnableBits} for this ghost type.");
        }

        public static int TypeIndexToIndexInTypeArray(ArchetypeChunk chunk, int typeIndex)
        {
            var types = chunk.Archetype.GetComponentTypes();
            for (int i = 0; i < types.Length; ++i)
            {
                if (types[i].TypeIndex == typeIndex)
                    return i;
            }
            return -1;
        }

        public static void UpdateEnableableMasks(ArchetypeChunk chunk, int startIndex, int endIndex, ref DynamicComponentTypeHandle handle, byte* snapshot,
            int changeMaskUints, int enableableMaskOffset, int snapshotSize)
        {
            var array = chunk.GetEnableableBits(ref handle);
            var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));

            var uintOffset = enableableMaskOffset >> 5; // 等价于 `floor(enableableMaskOffset / 32)` 的快捷写法
            var maskOffset = enableableMaskOffset & 0x1f; // 等价于 `enableableMaskOffset % 32` 的快捷写法
            snapshotSize /= 4;

            uint* enableableMasks = (uint*)(snapshot + sizeof(uint) + changeMaskUints * sizeof(uint)) + uintOffset;
            for (int i = startIndex; i < endIndex; ++i)
            {
                if (maskOffset == 0) // 首次写入时重置全部 32 位
                    *enableableMasks = 0U;
                if (bitArray.IsSet(i))
                    (*enableableMasks) |= 1U << maskOffset;
                else
                    (*enableableMasks) &= ~(1U << maskOffset);

                enableableMasks += snapshotSize;
            }
        }

        private bool CanSerializeGroup(in DynamicBuffer<GhostGroup> ghostGroup)
        {
            for (int i = 0; i < ghostGroup.Length; ++i)
            {
                if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var groupChunk))
                {
                    netDebug.LogError("Ghost group contains an member which is not a valid entity");
                    return false;
                }
                #if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (!groupChunk.Chunk.Has(ref ghostChildEntityComponentType))
                    throw new InvalidOperationException("Ghost group contains an member which does not have a GhostChildEntityComponent.");
                #endif
                // Entity 尚未初始化有效状态，继续等待
                if (!chunkSerializationData.TryGetValue(groupChunk.Chunk, out var chunkState))
                    return false;
                // 此 Ghost 类型的 Prefab 尚未被 Ack
                if (chunkState.ghostType >= snapshotAck.NumLoadedPrefabs)
                    return false;
            }
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool SerializeGroup(ref DataStreamWriter dataStream, ref StreamCompressionModel compressionModel,
            in DynamicBuffer<GhostGroup> ghostGroup, bool useSingleBaseline, ref uint anyChangeMaskThisGroup)
        {
            var grpAvailableBaselines = new NativeList<SnapshotBaseline>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
            var baselinesPerEntity = stackalloc byte*[4];
            // 足以容纳任意可能数量的复制 Component
            var entityStartBit = stackalloc int[ghostChunkComponentTypesLength*2 + 2];
            // 还需要跟踪当前写入索引以便回滚
            var currentWriteIndex = stackalloc int[ghostGroup.Length];
            for (int i = 0; i < ghostGroup.Length; ++i)
            {
                if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var childChunk))
                    throw new InvalidOperationException("Ghost group contains an member which is not a valid entity.");
                if (!chunkSerializationData.TryGetValue(childChunk.Chunk, out var childChunkState))
                    throw new InvalidOperationException("Ghost group member does not have state.");
                var childGhostType = childChunkState.ghostType;
                #if ENABLE_UNITY_COLLECTIONS_CHECKS
                var ghostComp = childChunk.Chunk.GetNativeArray(ref ghostComponentType);
                if (ghostComp[childChunk.IndexInChunk].ghostType >= 0 && ghostComp[childChunk.IndexInChunk].ghostType != childGhostType)
                    throw new InvalidOperationException("Ghost group member has invalid ghost type.");
                #endif
                ref readonly var childGhostPrefabSerializer = ref GhostTypeCollection.ElementAtRO(childGhostType);
                ValidateNoNestedGhostGroups(childGhostPrefabSerializer.IsGhostGroup);
                dataStream.WritePackedUInt((uint)childGhostType, compressionModel);
                dataStream.WritePackedUInt(1, compressionModel);
                dataStream.WriteRawBits(childChunk.Chunk.Has(ref prespawnBaselineTypeHandle) ? 1u : 0, 1);
                PacketDumpGroupItem(i, ghostGroup.Length, childGhostType);

                var groupSnapshot = default(CurrentSnapshotState);

                grpAvailableBaselines.Clear();
                groupSnapshot.AvailableBaselines = grpAvailableBaselines;
                if (childGhostPrefabSerializer.NumBuffers > 0)
                {
                    groupSnapshot.SnapshotDynamicDataSize = GatherDynamicBufferSize(childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk + 1, childGhostType);
                }

                uint* snapshotIndex = childChunkState.GetSnapshotIndex();

                var writeIndex = childChunkState.GetSnapshotWriteIndex();
                var baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;
                bool clearEntityArray = true;
                if (snapshotIndex[baselineIndex] != currentTick.SerializedData)
                {
                    // Chunk History 每帧只需更新一次，这是本帧首次使用此 Chunk
                    // TODO：只有发生结构性变更时才需更新 Chunk History，可通过跳过不必要更新进行优化
                    UpdateChunkHistory(childGhostType, childChunk.Chunk, childChunkState, childGhostPrefabSerializer.SnapshotSize);
                    snapshotIndex[writeIndex] = currentTick.SerializedData;
                    var nextWriteIndex = (writeIndex + 1) % GhostSystemConstants.SnapshotHistorySize;
                    childChunkState.SetSnapshotWriteIndex(nextWriteIndex);
                }
                else
                {
                    // 已经推进过，因此使用前一个值
                    writeIndex = baselineIndex;
                    baselineIndex = (GhostSystemConstants.SnapshotHistorySize + writeIndex - 1) %
                            GhostSystemConstants.SnapshotHistorySize;
                    clearEntityArray = false;
                    groupSnapshot.AlreadyUsedChunk = 1;
                }
                currentWriteIndex[i] = writeIndex;

                SetupDataAndAvailableBaselines(ref groupSnapshot, ref childChunkState, childChunk.Chunk, childGhostPrefabSerializer.SnapshotSize, writeIndex, snapshotIndex);

                if (clearEntityArray)
                    UnsafeUtility.MemClear(groupSnapshot.SnapshotEntity, UnsafeUtility.SizeOf<Entity>()*childChunk.Chunk.Capacity);

                // 此递归调用可以复用 ComponentDataPerEntity、ComponentDataLengthPerEntity 和 tempWriter
                // tempBaselinesPerEntity、tempDynamicDataLenPerEntity、tempSameBaselinePerEntity 和 tempEntityStartBit 必须更换

                int sameBaselinePerEntity;
                int dynamicDataLenPerEntity;

                if (SerializeEntities(ref dataStream, out _, out var anyChangeMaskThisEntity, childGhostType, childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk+1, useSingleBaseline, groupSnapshot,
                    baselinesPerEntity, &sameBaselinePerEntity, &dynamicDataLenPerEntity, entityStartBit) != childChunk.IndexInChunk+1)
                {
                    // FIXME：如果 Group 成员本身也是 Group 根，此逻辑无法正常工作，因为此时可能无法回滚作为压缩依据的状态
                    // 这就是不支持嵌套 Ghost Group 的原因
                    // 回滚 Group 成员中全部已写入的 Entity
                    while(i-- > 0)
                    {
                        if (!childEntityLookup.TryGetValue(ghostGroup[i].Value, out var revertChunk))
                            throw new InvalidOperationException("Ghost group contains an member which is not a valid entity.");
                        if (chunkSerializationData.TryGetValue(revertChunk.Chunk, out var revertChunkState))
                        {
                            var childWriteIndex = currentWriteIndex[i];
                            var childCompDataSize = GhostTypeCollection.ElementAtRO(revertChunkState.ghostType).SnapshotSize;
                            var groupSnapshotEntity = revertChunkState.GetEntity(childCompDataSize, revertChunk.Chunk.Capacity, childWriteIndex);
                            groupSnapshotEntity[revertChunk.IndexInChunk] = Entity.Null;
                        }
                    }
                    return false;
                }
                anyChangeMaskThisGroup |= anyChangeMaskThisEntity;
            }
            return true;
        }

        // 遍历 Chunk 中指定 Entity 范围内的全部 Component，
        // 计算存储所有动态 Buffer 内容所需的容量
        private int GatherDynamicBufferSize(in ArchetypeChunk chunk, int startIndex, int endIndex, int ghostType)
        {
            if (chunk.Has(ref preSerializedGhostType) && SnapshotPreSerializeData.TryGetValue(chunk, out var preSerializedSnapshot))
            {
                return preSerializedSnapshot.DynamicSize;
            }

            var helper = new GhostSerializeHelper
            {
                ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                GhostComponentIndex = GhostComponentIndex,
                GhostComponentCollection = GhostComponentCollection,
                childEntityLookup = childEntityLookup,
                linkedEntityGroupType = linkedEntityGroupType,
                ghostChunkComponentTypesPtrLen = ghostChunkComponentTypesLength
            };

            int requiredSize = helper.GatherBufferSize(chunk, startIndex, endIndex, GhostTypeCollection[ghostType]);
            return requiredSize;
        }

        int UpdateGhostRelevancy(ArchetypeChunk chunk, in PrioChunk prioChunk, int startIndex, byte* relevancyData,
            in GhostChunkSerializationState chunkState, int snapshotSize, out bool hasRelevancySpawns)
        {
            hasRelevancySpawns = false;
            var ghost = chunk.GetNativeArray(ref ghostComponentType);
            var ghostSystemState = chunk.GetNativeArray(ref ghostSystemStateType);
            // 先确定每个 Entity 使用的 Baseline，以便按 Baseline + maxCount 发送，而不是逐 Entity 发送
            int irrelevantCount = 0;
            bool setIsRelevant = relevancyMode == GhostRelevancyMode.SetIsRelevant;
            bool chunkMatchesInternalRelevantRule = internalGlobalRelevantMask.Matches(chunk.Archetype);
            bool chunkMatchesEitherRelevantRule = chunkMatchesInternalRelevantRule || userGlobalRelevantMask.Matches(chunk.Archetype);
            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
            {
                // 只有 Ghost 未被特定规则手动标记时，才使用 Query 和/或 Importance Scaling 的相关性标志
                // 原因是相关性集合会覆盖全局规则，因此存在明确规则时应保留该规则
                bool isRelevant = chunkMatchesEitherRelevantRule | prioChunk.isRelevant;
                if (!setIsRelevant | !isRelevant)
                {
                    var key = new RelevantGhostForConnection(NetworkId, ghost[ent].ghostId);
                    isRelevant = relevantGhostForConnection.ContainsKey(key) == setIsRelevant;
                }

                ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[ent]);
                bool wasRelevant = (ghostState.Flags&ConnectionStateData.GhostStateFlags.IsRelevant) != 0;
                var isDespawning = (ghostState.Flags & ConnectionStateData.GhostStateFlags.IsDespawning) != 0;
                relevancyData[ent] = 1;

                // 如果此 Ghost 之前为 Irrelevant，需要等到对应 Despawn 被 Ack，避免在同一 Snapshot 中同时发送 Spawn 和 Despawn
                if (!isRelevant || isDespawning)
                {
                    relevancyData[ent] = 0;
                    // 如果尚未设置 Irrelevant 标志，客户端可能已经见过此 Entity
                    if (wasRelevant)
                    {
                        // 清除 Snapshot History Buffer，避免以此为基准进行 Delta Compression
                        for (int hp = 0; hp < GhostSystemConstants.SnapshotHistorySize; ++hp)
                        {
                            var clearSnapshotEntity = chunkState.GetEntity(snapshotSize, chunk.Capacity, hp);
                            clearSnapshotEntity[ent] = Entity.Null;
                        }
                        // 将此 Ghost 加入待处理 Despawn 队列，此时尚未真正发送 Despawn
                        PendingGhostDespawn.AddNewPendingDespawn(ref *pendingDespawns, ref ghostState.Flags, new GhostCleanup
                        {
                            ghostId = ghost[ent].ghostId,
                            spawnTick = ghost[ent].spawnTick,
                            despawnTick = NetworkTick.Invalid, // 不适用于因 Irrelevant 触发的 Despawn
                        }, PendingGhostDespawn.DespawnReason.Irrelevant);
                    }
                    if (ent >= startIndex)
                        irrelevantCount = irrelevantCount + 1;
                }
                else if (!wasRelevant)
                    hasRelevancySpawns = true;
            }
            return irrelevantCount;
        }
        int UpdateValidGhostGroupRelevancy(ArchetypeChunk chunk, int startIndex, byte* relevancyData, bool keepState)
        {
            var ghost = chunk.GetNativeArray(ref ghostComponentType);
            var ghostGroupAccessor = chunk.GetBufferAccessor(ref ghostGroupType);

            int irrelevantCount = 0;
            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
            {
                relevancyData[ent] = keepState ? relevancyData[ent] : (byte)1;
                if (relevancyData[ent] != 0 && !CanSerializeGroup(ghostGroupAccessor[ent]))
                {
                    PacketDumpSkipInvalidGroup(ghost[ent].ghostId);
                    PacketDumpFlush();
                    relevancyData[ent] = 0;
                    if (ent >= startIndex)
                        ++irrelevantCount;
                }
            }
            return irrelevantCount;
        }
        bool CanUseStaticOptimization(in ArchetypeChunk chunk, int ghostType, int writeIndex, uint* snapshotIndex,
            ref GhostChunkSerializationState chunkState, bool hasRelevancySpawns, bool didOrderChange)
        {
            using var _ = GhostSendSystem.s_CanUseStaticOptimizationMarker.Auto();

            // 此 Chunk 存在相关性变化时不能进行静态优化
            if (hasRelevancySpawns)
            {
                PacketDumpCUSOHasRelevancyChanges();
                return false;
            }

            // 添加或移除了 Entity，因此不能依赖 Component Change Version
            if (didOrderChange)
            {
                PacketDumpCUSOOrderChanged();
                return false;
            }

            // 注意：由于确认客户端 Ack Mask 的逻辑较为特殊，必须始终执行下方 `TryAck`
            // 理想情况下应能更早退出

            // 查找任何已发送且随后被 Ack 的 Zero-Change Snapshot，
            // 即 Tick 大于等于 Zero-Change Snapshot Tick 的任意 Snapshot
            // 实际只需一份，但等待 Ack 期间可能已经发送了两份或更多
            var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
            var zeroChangeVersion = chunkState.GetFirstZeroChangeVersion();
            // Prespawn Chunk 比较特殊，如果其 ZC.Version != 0 且 ZC.Tick 无效，
            // 表示自 Prespawn Scene 加载后除顺序变化外没有发生变化
            // 因此可以推断它们已被隐式 Ack，因为客户端加载 SubScene 时已获得 Prespawn 值，
            // 所以仍可使用静态优化
            var hasImplicitlyAckedZeroChange = zeroChangeVersion != 0 && !zeroChangeTick.IsValid;
            if (hasImplicitlyAckedZeroChange)
                PacketDumpCUSOImplicitAck(ref chunkState);
            bool hasAckedAnyZeroChangeSnapshot = hasImplicitlyAckedZeroChange;
            for (int i = 0; i < GhostSystemConstants.SnapshotHistorySize; ++i)
            {
                var snapshotTick = new NetworkTick {SerializedData = snapshotIndex[i]};
                if (!snapshotTick.IsValid || i == writeIndex) continue;

                // 注意：此处有意忽略 `GhostSystemConstants.MaxBaselineAge`
                // 即使已无法确定 Baseline，仍可将 Ghost 标记为 Static
                // 后续重新发送时只需按未压缩形式发送
                TryAck(ref chunkState, i, snapshotTick);

                hasAckedAnyZeroChangeSnapshot |= (chunkState.HasAckFlag(i) && zeroChangeTick.IsValid && snapshotTick.TicksSince(zeroChangeTick) >= 0);
                // 注意：此处不能提前退出，必须继续遍历并调用 `TryAck`
            }

            if (!hasAckedAnyZeroChangeSnapshot)
            {
                PacketDumpCUSONotYetAckedZC();
                return false;
            }
            PacketDumpCUSOHasAckedZC(ref chunkState);

            // ZC Version 为 0 表示不存在隐式或显式的 Zero-Change Snapshot 可供提前退出
            // 即最近一定发送过尚未 Ack 的变化
            if (zeroChangeVersion == 0)
            {
                PacketDumpCUSONoZC();
                return false;
            }

            // 此时已经确认存在 Zero-Change Version
            // 接下来确保所有 GhostField Component 都没有变化
            // 只要任意一个发生变化，就不能跳过此 Chunk
            ref readonly var typeData = ref GhostTypeCollection.ElementAtRO(ghostType);
            int baseOffset = typeData.FirstComponent;
            int numChildComponents = typeData.NumChildComponents;
            int numBaseComponents = typeData.NumComponents - numChildComponents;
            for (int i = 0; i < numBaseComponents; ++i)
            {
                int compIdx = GhostComponentIndex[baseOffset + i].ComponentIndex;
                ValidateGhostComponentIndex(compIdx);
                if (chunk.DidChange(ref ghostChunkComponentTypesPtr[compIdx], zeroChangeVersion))
                {
                    PacketDumpCUSOAnyGhostComponentChanged(compIdx);
                    // TODO：既然能够知道 Change Version 何时导致数据包被序列化，
                    // 同时也知道 Ghost 最终是否确实发生变化，遇到误报时或许可以向用户记录警告或错误
                    // 但用户写入该 Component 的非 GhostField 是合法行为，
                    // 因此任何校验都需要支持按 Component 或 Ghost 类型启用或禁用
                    return false;
                }
            }

            // 静态优化校验成功
            PacketDumpResult_CUSOSuccess(ref chunkState);
            return true;
        }
        private void UpdateChunkHistory(int ghostType, ArchetypeChunk currentChunk, GhostChunkSerializationState curChunkState, int snapshotSize)
        {
            var ghostSystemState = currentChunk.GetNativeArray(ref ghostSystemStateType);
            var ghostEntities = currentChunk.GetNativeArray(entityType);
            NativeParallelHashMap<uint, IntPtr> prevSnapshots = default;
            for (int currentIndexInChunk = 0, chunkEntityCount = currentChunk.Count; currentIndexInChunk < chunkEntityCount; ++currentIndexInChunk)
            {
                ref var ghostState = ref ghostStateData.GetGhostState(ghostSystemState[currentIndexInChunk]);
                var entity = ghostEntities[currentIndexInChunk];
                // 遍历全部 Entity，找出相比上次位于不同 Chunk 或不同索引的项
                if (ghostState.LastChunk != currentChunk || ghostState.LastIndexInChunk != currentIndexInChunk)
                {
                    // 启用保留 Snapshot History、历史数据存在且不包含 Buffer 时，尝试复制历史数据
                    // 需要检查 IsSameSizeAndCapacity，因为 Chunk 可能被另一 Archetype 复用
                    // 此时虽然能取得有效 Chunk 状态，但由于不知道容量，不能读取 Entity 数组
                    // 还需要检查 GetLastValidTick，以确认 LastChunk 使用的内存当前仍属于存储 Ghost 的 Chunk
                    // 否则 Chunk 内存可能在此循环之前或期间被其他用途复用，导致访问无效且可能动态变化的内存
                    if (systemData.KeepSnapshotHistoryOnStructuralChange && ghostState.LastChunk != default && GhostTypeCollection[ghostType].NumBuffers == 0 &&
                        chunkSerializationData.TryGetValue(ghostState.LastChunk, out var prevChunkState) && prevChunkState.GetLastValidTick() == currentTick &&
                        prevChunkState.IsSameSizeAndCapacity(snapshotSize, ghostState.LastChunk.Capacity))
                    {
                        uint* snapshotIndex = prevChunkState.GetSnapshotIndex();
                        int writeIndex = prevChunkState.GetSnapshotWriteIndex();

                        // 为旧 Chunk 中找到的全部有效历史项建立 Tick 到 Snapshot Data 指针的映射
                        if (prevSnapshots.IsCreated)
                            prevSnapshots.Clear();
                        else
                            prevSnapshots = new NativeParallelHashMap<uint, IntPtr>(GhostSystemConstants.SnapshotHistorySize, Allocator.Temp);
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            // 不复制 Write Index 位置的 Snapshot Data，因为该位置允许保留不完整数据
                            // 清理或写入时出于同样原因不做此检查，该位置保留不完整数据是允许的
                            if (history == writeIndex)
                                continue;
                            var historyEntity = prevChunkState.GetEntity(snapshotSize, ghostState.LastChunk.Capacity, history);
                            if (historyEntity[ghostState.LastIndexInChunk] == entity)
                            {
                                var src = prevChunkState.GetData(snapshotSize, ghostState.LastChunk.Capacity, history);
                                src += snapshotSize*ghostState.LastIndexInChunk;
                                // 加入 prevSnapshots 映射
                                prevSnapshots.TryAdd(snapshotIndex[history], (IntPtr)src);
                                // 清除旧 Chunk 中的历史槽位，因为新 Chunk 将成为权威来源
                                historyEntity[ghostState.LastIndexInChunk] = Entity.Null;
                            }
                        }
                        snapshotIndex = curChunkState.GetSnapshotIndex();
                        // 写入或清除此 Entity 的全部历史
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            // 如果 prevSnapshots 中存在该项，则复制而不是将 Entity 设为 null
                            var historyEntity = curChunkState.GetEntity(snapshotSize, currentChunk.Capacity, history);
                            // 如果此历史项的 Tick 也存在于旧 Snapshot 中，则复制数据并将历史位置标记为有效
                            // 否则标记为无效
                            if (prevSnapshots.TryGetValue(snapshotIndex[history], out var src))
                            {
                                var dst = curChunkState.GetData(snapshotSize, currentChunk.Capacity, history);
                                dst += snapshotSize*currentIndexInChunk;
                                UnsafeUtility.MemCpy(dst, (void*)src, snapshotSize);
                                historyEntity[currentIndexInChunk] = entity;
                                PacketDumpMovedAckHistory(new NetworkTick{SerializedData = snapshotIndex[history],}, prevChunkState.HasAckFlag(history), curChunkState.HasAckFlag(history));
                            }
                            else
                                historyEntity[currentIndexInChunk] = Entity.Null;
                        }
                    }
                    else
                    {
                        // 没有可复制或希望复制的旧历史，因此清除该 Entity 的全部历史
                        for (int history = 0; history < GhostSystemConstants.SnapshotHistorySize; ++history)
                        {
                            var historyEntity = curChunkState.GetEntity(snapshotSize, currentChunk.Capacity, history);
                            historyEntity[currentIndexInChunk] = Entity.Null;
                        }
                    }
                    ghostState.LastChunk = currentChunk;
                    ghostState.LastIndexInChunk = currentIndexInChunk;
                }
            }
        }
        public SerializeEnitiesResult SerializeChunk(in PrioChunk serialChunk, ref DataStreamWriter dataStream,
            out uint thisChunkSentEntities, ref bool didFillPacket)
        {
            thisChunkSentEntities = 0;
            int entitySize = UnsafeUtility.SizeOf<Entity>();
            bool relevancyEnabled = (relevancyMode != GhostRelevancyMode.Disabled);
            bool hasRelevancySpawns = false;
            didFillPacket = false;

            var currentSnapshot = default(CurrentSnapshotState);
            currentSnapshot.AvailableBaselines = tempAvailableBaselines;
            currentSnapshot.AvailableBaselines.Clear();

            var chunk = serialChunk.chunk;
            var startIndex = serialChunk.startIndex;
            var endIndex = chunk.Count;
            var ghostType = serialChunk.ghostType;

            var typeData = GhostTypeCollection[ghostType];
            var isStatic = typeData.CanBeStaticOptimized();

            int snapshotSize = typeData.SnapshotSize;
            var useSingleBaseline = typeData.UseSingleBaseline != 0;
            useSingleBaseline |= isStatic || systemData.ForceSingleBaseline;

            int relevantGhostCount = chunk.Count - serialChunk.startIndex;
            var chunkState = chunkSerializationData[chunk];

            uint* snapshotIndex = chunkState.GetSnapshotIndex();
            int writeIndex = chunkState.GetSnapshotWriteIndex();
            PacketDumpSerializeChunk(chunk, ghostType);
            var didOrderChange = chunk.DidOrderChange(chunkState.GetOrderChangeVersion());
            if (didOrderChange)
            {
                // 此 Chunk 发生了结构性变更，可能包括
                // - 新增 Ghost
                // - 删除 Ghost
                // - 从其他 Ghost Chunk 移入 Ghost
                chunkState.SetOrderChangeVersion(chunk.GetOrderVersion());
                // 对 Prespawn 而言，第一个 Zero-Change Tick 为 0，Version 等于 PrespawnBaseline Buffer 的 Change Version
                // 注意：Chunk 内部的结构性变更不会使 Baseline 失效，因此仍有可能跳过发送该 Chunk
                if (chunk.Has(ref prespawnBaselineTypeHandle))
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, chunk.GetChangeVersion(ref prespawnBaselineTypeHandle));
                else
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                PacketDumpStructuralChange(in serialChunk);
                // 确保历史缓冲区中没有项目引用曾作为其他 Chunk 一部分发送的 Ghost
                // 否则可能相对客户端已不再可用的 Snapshot 进行 Delta Compression
                UpdateChunkHistory(ghostType, chunk, chunkState, snapshotSize);
            }

            // 计算哪些 Entity 为 Relevant，并为 Irrelevant Entity 触发 Despawn
            if (relevancyEnabled)
            {
                using var _ = GhostSendSystem.s_RelevancyMarker.Auto();
                currentSnapshot.relevancyData = (byte*)tempRelevancyPerEntity.GetUnsafePtr();
                int irrelevantCount = UpdateGhostRelevancy(chunk, in serialChunk, startIndex, currentSnapshot.relevancyData, chunkState, snapshotSize, out hasRelevancySpawns);
                relevantGhostCount -= irrelevantCount;
                if (hasRelevancySpawns)
                {
                    // 将此情况视为结构性变更，不尝试跳过任何 Zero-Change 数据包
                    chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                    PacketDumpHasRelevancySpawns();
                }
            }

            // 遍历并将缺少 Child 的 Ghost Group 设为 Irrelevant
            if (typeData.IsGhostGroup!=0)
            {
                using var _ = GhostSendSystem.s_GhostGroupRelevancyMarker.Auto();
                currentSnapshot.relevancyData = (byte*)tempRelevancyPerEntity.GetUnsafePtr();
                int irrelevantCount = UpdateValidGhostGroupRelevancy(chunk, startIndex, currentSnapshot.relevancyData, relevancyEnabled);
                relevantGhostCount -= irrelevantCount;
            }
            chunkState.SetNumRelevant(relevantGhostCount, in chunk);

            if (relevantGhostCount <= 0)
            {
                // 没有内容可发送，因此无需花费时间序列化
                // 但仍需将 Chunk 标记为本帧已发送，避免存在更高优先级 Chunk 时下一帧再次处理
                // 使用相关性并在存在部分发送 Chunk 时发生结构性变更，就可能出现这种情况
                // 此处像已经发送 Chunk 一样更新时间戳，但实际不发送任何内容
                chunkState.SetLastFullUpdate(currentTick);
                PacketDumpResult_NoRelevantGhostsInChunk();
                return SerializeEnitiesResult.Ok;
            }

            // 仅对标记为静态优化的 Ghost 应用 Zero-Change 优化
            // 动态优化 Ghost 依靠 Delta Prediction，在变化保持恒定时也能得到 Zero-Change Snapshot
            // Ghost Group 比较特殊，其中包含其他 Ghost，无法确定它们是否已按 Zero-Change 被 Ack，
            // 因此 Ghost Group 永远不能跳过 Zero-Change 数据包
            if (isStatic)
            {
                // Chunk 被修改时，会在序列化内容后清除修改状态
                // 如果 Snapshot 仍为 Zero-Change，只更新 Version 而不更新 Tick，因为实际仍未发送任何内容
                if (CanUseStaticOptimization(chunk, ghostType, writeIndex, snapshotIndex, ref chunkState, hasRelevancySpawns, didOrderChange))
                {
                    // 没有必须发送的变化，按已发送 Chunk 处理，避免所有静态 Chunk 都积累为最高优先级
                    chunkState.SetLastFullUpdate(currentTick);
                    return SerializeEnitiesResult.Ok;
                }
            }
            else PacketDumpCUSONotStatic(in typeData);

            if (typeData.NumBuffers > 0)
            {
                // 动态 Buffer 内容始终从指定历史槽的动态存储缓冲区起始位置存储
                // 因为每份 Snapshot 只对应 startIndex 到 endIndex 的 Entity 范围，外部范围 0 到 startIndex 和 Count 到 Capacity 均无效
                // 因此此处从 startIndex 而不是 0 开始统计 Buffer 大小

                // FIXME：此操作成本很高，会遍历整个 Chunk 及其 Child Entity，应仅在发生变化时执行
                // 可在 Chunk 状态中备份当前大小和 Version，但由于 Child Entity 可能位于其他 Chunk，整体检查并不简单
                currentSnapshot.SnapshotDynamicDataSize = GatherDynamicBufferSize(chunk, serialChunk.startIndex, serialChunk.chunk.Count, ghostType);
            }

            SetupDataAndAvailableBaselines(ref currentSnapshot, ref chunkState, chunk, snapshotSize, writeIndex, snapshotIndex);

            // 为保证 SnapshotHistorySize 正确，如果 Snapshot History 已被在途 Snapshot 填满，
            // 就不能再次发送此 Ghost，因为没有可用历史空间存放它
            const int neededFreeSlots = 2; // 一个用于当前 Snapshot 的 Write Index，另一个用于当前 Snapshot 的 Baseline
            var snapshotHistorySaturated = currentSnapshot.NumInFlightBaselines >= GhostSystemConstants.SnapshotHistorySize - neededFreeSlots;
            // 以下绕过条件用于保护发送频率，避免 Lag Spike 表现出的高数量在途 Baseline 降低发送节奏
            var ticksSinceLastReceive = Hint.Likely(snapshotAck.LastReceivedSnapshotByRemote.IsValid) ? currentTick.TicksSince(snapshotAck.LastReceivedSnapshotByRemote) : 0;
            var bypassSnapshotHistoryFull = ticksSinceLastReceive > expectedSnapshotRttInSimTicks;
            if (snapshotHistorySaturated)
            {
                PacketDumpResult_SnapshotHistorySaturated(ghostType, in chunk, currentSnapshot.NumInFlightBaselines, ticksSinceLastReceive, bypassSnapshotHistoryFull);
                if (!bypassSnapshotHistoryFull)
                {
                    return SerializeEnitiesResult.Ok;
                }
            }

            snapshotIndex[writeIndex] = currentTick.SerializedData;
            var oldStream = dataStream;

            dataStream.WritePackedUInt((uint) ghostType, compressionModel);
            dataStream.WritePackedUInt((uint) relevantGhostCount, compressionModel);
            // 此连续区段中的 Entity 为 Prespawn 对象时写入 1 位
            // 这会改变 GhostId 的编码方式，并且不写入 Spawn Tick
            dataStream.WriteRawBits(chunk.Has(ref PrespawnIndexType)?1u:0u, 1);
            PacketDumpGhostCount(ghostType, relevantGhostCount);
            if (dataStream.HasFailedWrites)
            {
                PacketDumpResult_WriterFullBeforeSerialize();
                dataStream = oldStream;
                didFillPacket = true;
                return SerializeEnitiesResult.Failed;
            }

            typeData.profilerMarker.Begin();
            // 将当前 Ghost 类型的 Chunk 写入 Data Stream
            tempWriter.Clear(); // 在此处而不是方法内部清理 Temp Writer，便于处理会递归向其中追加更多数据的 Ghost Group
            var ent = SerializeEntities(ref dataStream, out var skippedEntityCount, out var anyChangeMask, ghostType, chunk, startIndex, endIndex, useSingleBaseline, currentSnapshot);
            typeData.profilerMarker.End();

            // 仅追加相对最近已 Ack Baseline 确实发生变化的 Chunk，
            // 并且只有实际发送后才更新 Write Index
            var isPartialChunkSend = startIndex != 0 || ent < endIndex;
            var isZeroChange = anyChangeMask == 0 && !hasRelevancySpawns; // 注意：SerializeEntities 检测到需要发送的顺序变化时，isZeroChange 为 false
            var triggeredZeroChangeOptimization = !isPartialChunkSend && isZeroChange && isStatic;
            if (triggeredZeroChangeOptimization)
            {
                chunkState.SetLastFullUpdate(currentTick);

                var zeroChangeTick = chunkState.GetFirstZeroChangeTick();
                if (!zeroChangeTick.IsValid) zeroChangeTick = currentTick;
                chunkState.SetFirstZeroChange(zeroChangeTick, CurrentSystemVersion);
                PacketDumpResult_ZeroChangeOptimizedChunk(ref dataStream, ref oldStream, ref chunkState);

                dataStream = oldStream;
                return SerializeEnitiesResult.Ok;
            }

            thisChunkSentEntities = (uint) (ent - serialChunk.startIndex - skippedEntityCount);
            var sentAtLeastOneEntity = thisChunkSentEntities > 0;
            if (sentAtLeastOneEntity)
            {
                if (serialChunk.startIndex > 0)
                    UnsafeUtility.MemClear(currentSnapshot.SnapshotEntity, entitySize * serialChunk.startIndex);
                if (ent < chunk.Capacity)
                    UnsafeUtility.MemClear(currentSnapshot.SnapshotEntity + ent,
                        entitySize * (chunk.Capacity - ent));
                var nextWriteIndex = (chunkState.GetSnapshotWriteIndex() + 1) % GhostSystemConstants.SnapshotHistorySize;
                chunkState.SetSnapshotWriteIndex(nextWriteIndex);
            }

            if (isPartialChunkSend)
            {
                // TODO：是否应始终执行此逻辑，还是只允许最高优先级 Chunk 进行部分发送
                //if (pc == 0)

                // 注意：大多数静态 Ghost 的 ChangeBit 为零，因此可通过始终从第 0 个 Entity 开始重发来利用这一点
                // 例如
                // Send0：发送 0 - 4
                // Send1：发送 0 - 8，其中 0 - 4 为 Zero-Change，数据量很小
                // Send2：发送 0 - 10，其中 0 - 8 为 Zero-Change，数据量很小
                if (isStatic)
                    chunkState.SetStartIndex(0);

                // 部分发送的 Chunk 不能视为 Static
                // 好的一面是，随着此 Chunk 中越来越多 Entity 被 Ack，每次写入都会变小
                // 因为此前 Ghost 都已被 Ack，Zero-Change Delta Compression 开始生效
                // 这意味着很可能很快就能发送一份完整 Chunk，之后便可进行 Zero-Change 优化
                // 理论上它可能无限失败，确实存在风险，但实际使用中能够工作
                didFillPacket = true; // 未能发送全部 Ghost，因此数据包一定已经填满
                chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);

                if (sentAtLeastOneEntity)
                {
                    PacketDumpResult_PartialSend();
                    return SerializeEnitiesResult.Ok;
                }
                PacketDumpResult_PacketFullBeforeOneEntity();
                dataStream = oldStream;
                return SerializeEnitiesResult.Failed;
            }

            chunkState.SetLastFullUpdate(currentTick);

            // 此静态 Ghost Chunk 已完整发送
            // 一旦用户 Ack 当前 Snapshot，在该 Chunk 下次变化前就无需再次发送，因此将其标记为从此处开始 Zero-Change
            if (isStatic)
            {
                chunkState.SetFirstZeroChange(currentTick, CurrentSystemVersion);
                PacketDumpResult_StaticFullSend(ref chunkState);
            }
            else
            {
                // Dynamic Ghost 始终会发送，因此永远不能处于 Zero-Change 状态
                chunkState.SetFirstZeroChange(NetworkTick.Invalid, 0);
                PacketDumpResult_DynamicFullSend();
            }
            return SerializeEnitiesResult.Ok;
        }
    }
}
