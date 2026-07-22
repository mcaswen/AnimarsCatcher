#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Entities;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含所有命令接收系统的 Group，仅存在于 Server World
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation, WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(NetworkStreamReceiveSystem))]
    public partial class CommandReceiveSystemGroup : ComponentSystemGroup
    {
    }

    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(CommandReceiveSystemGroup), OrderLast = true)]
    [BurstCompile]
    internal partial struct CommandReceiveClearSystem : ISystem
    {
        EntityQuery m_NetworkTimeSingleton;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_NetworkTimeSingleton = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        [BurstCompile]
        partial struct CommandReceiveClearJob : IJobEntity
        {
            public NetworkTick _currentTick;

            public void Execute(DynamicBuffer<IncomingCommandDataStreamBuffer> buffer, ref NetworkSnapshotAck snapshotAck)
            {
                buffer.Clear();
                if (snapshotAck.MostRecentFullCommandTick.IsValid)
                {
                    int age = _currentTick.TicksSince(snapshotAck.MostRecentFullCommandTick);
                    age *= 256;
                    snapshotAck.ServerCommandAge = (snapshotAck.ServerCommandAge * 7 + age) / 8;
                }
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = m_NetworkTimeSingleton.GetSingleton<NetworkTime>();
            var currentTick = networkTime.ServerTick;

            var commandReceiveClearJob = new CommandReceiveClearJob() { _currentTick = currentTick };
            commandReceiveClearJob.ScheduleParallel();
        }
    }

    /// <summary>
    /// 用于实现命令接收系统的辅助结构体
    /// 通常由代码生成器使用，只应在特殊情况下直接使用
    /// </summary>
    /// <typeparam name="TCommandDataSerializer">实现 ICommandDataSerializer 的 Unmanaged CommandDataSerializer</typeparam>
    /// <typeparam name="TCommandData">实现 ICommandData 的 Unmanaged CommandData</typeparam>
    public struct CommandReceiveSystem<TCommandDataSerializer, TCommandData>
        where TCommandData : unmanaged, ICommandData
        where TCommandDataSerializer : unmanaged, ICommandDataSerializer<TCommandData>
    {
            /// <summary>
            /// 代码生成器用于实现生成的接收 Job 的 Execute 方法的辅助结构体
            /// ReceiveJobData 实现命令反序列化逻辑，从数据流读取序列化命令并将其加入目标 Entity 的 Command Buffer
            /// 在命令反序列化期间，如果目标 Entity 具有 <see cref="CommandDataInterpolationDelay"/> 组件，
            /// 系统会用最近上报的插值延迟更新该组件
            /// </summary>
        public struct ReceiveJobData
        {
                /// <summary>
                /// 用于添加反序列化命令的输出 Command Buffer
            /// </summary>
            public BufferLookup<TCommandData> commandData;
                /// <summary>
                /// 用于从目标 Entity 获取可选 <see cref="CommandDataInterpolationDelay"/> 组件的访问器
            /// </summary>
            public ComponentLookup<CommandDataInterpolationDelay> delayFromEntity;
                /// <summary>
                /// 用于获取可选 <see cref="GhostOwner"/> 组件的访问器，
                /// 使用 <see cref="AutoCommandTarget"/> 时也用于查找目标 Entity
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostOwner> ghostOwnerFromEntity;
                /// <summary>
                /// 用于获取可选 <see cref="AutoCommandTarget"/> 组件的访问器
            /// </summary>
            [ReadOnly] public ComponentLookup<AutoCommandTarget> autoCommandTargetFromEntity;
                /// <summary>
                /// 解码差分压缩命令所使用的压缩模型
            /// </summary>
            public StreamCompressionModel compressionModel;
                /// <summary>
                /// 用于从 <see cref="IncomingCommandDataStreamBuffer"/> Buffer 读取数据的只读类型句柄
            /// </summary>
            [ReadOnly] public BufferTypeHandle<IncomingCommandDataStreamBuffer> cmdBufferType;
                /// <summary>
                /// <see cref="EnablePacketLogging"/> 的类型句柄，用于把命令信息转储到磁盘
            /// </summary>
            public ComponentTypeHandle<EnablePacketLogging> enablePacketLoggingType;
                /// <summary>
                /// 用于获取连接 <see cref="NetworkSnapshotAck"/> 的类型句柄
            /// </summary>
            public ComponentTypeHandle<NetworkSnapshotAck> snapshotAckType;
                /// <summary>
                /// 用于获取连接 <see cref="NetworkId"/> 的只读类型句柄
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<NetworkId> networkIdType;
                /// <summary>
                /// 用于获取连接 <see cref="CommandTarget"/> 的只读类型句柄
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<CommandTarget> commmandTargetType;
                /// <summary>
                /// 根据 <see cref="SpawnedGhost"/> 标识获取 Ghost Entity 实例的只读映射
                /// 更多信息请参阅 <see cref="SpawnedGhostEntityMap"/>
            /// </summary>
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;
                /// <summary>
                /// 当前服务器 Tick
            /// </summary>
            public NetworkTick serverTick;
                /// <summary>
                /// <see cref="NetDebug"/> Singleton 组件实例
            /// </summary>
            public NetDebug netDebug;
                /// <summary>
                /// <see cref="ICommandData"/> 类型的稳定 Hash，用于验证命令是否一致
            /// </summary>
            public ulong stableHash;

                /// <summary>
                /// 反序列化数据包中的所有命令，并把所有输入放入 Entity 的 <see cref="ICommandData"/> Buffer
            /// </summary>
            /// <param name="reader"></param>
            /// <param name="targetEntity"></param>
            /// <param name="tick"></param>
            /// <param name="snapshotAck"></param>
            /// <param name="numCommandsSent"></param>
            /// <param name="reusableTempBuffer"></param>
            /// <param name="arrivalStats"></param>
            /// <param name="enablePacketLogging"></param>
            /// <param name="readerStartBit"></param>
            /// <param name="spawnedGhost"></param>
            internal void Deserialize(ref DataStreamReader reader, Entity targetEntity,
                uint tick, in NetworkSnapshotAck snapshotAck, uint numCommandsSent, Span<TCommandData> reusableTempBuffer,
                ref CommandArrivalStatistics arrivalStats, ref EnablePacketLogging enablePacketLogging,
                int readerStartBit, in SpawnedGhost spawnedGhost)
            {
                if (delayFromEntity.HasComponent(targetEntity))
                    delayFromEntity[targetEntity] = new CommandDataInterpolationDelay{ Delay = snapshotAck.RemoteInterpolationDelay };

                var deserializeState = new RpcDeserializerState
                {
                    ghostMap = ghostMap,
                    CompressionModel = compressionModel,
                };
                var command = commandData[targetEntity];
                var baselineReceivedCommand = default(TCommandData);
                var serializer = default(TCommandDataSerializer);

                // 反序列化第一条命令，它以零值（默认值）作为差分压缩 Baseline
                baselineReceivedCommand.Tick = new NetworkTick{SerializedData = reader.ReadUInt()};
                serializer.Deserialize(ref reader, deserializeState, ref baselineReceivedCommand, default, compressionModel);
                // 把接收到的命令存入网络 Command Buffer
                reusableTempBuffer[0] = baselineReceivedCommand;

                var earlyByTicks = baselineReceivedCommand.Tick.TicksSince(serverTick);
                var isFirstLate = earlyByTicks < 0;
                if (isFirstLate) arrivalStats.NumArrivedTooLate++;
#if NETCODE_DEBUG
                if (enablePacketLogging.IsPacketCacheCreated)
                {
                    enablePacketLogging.LogToPacket($"[CRS][{serializer.ToFixedString()}:{stableHash}] Received command packet from {targetEntity.ToFixedString()} on GhostInst[type:??|id:{spawnedGhost.ghostId},st:{spawnedGhost.spawnTick.ToFixedString()}] targeting tick {baselineReceivedCommand.Tick.ToFixedString()}:\n\t| arrivalTick: {serverTick.ToFixedString()}\n\t| margin: {earlyByTicks}");
                    FixedString512Bytes baselineLog = $"\t[b]=[{baselineReceivedCommand.Tick.ToFixedString()}|{baselineReceivedCommand.ToFixedString()}]";
                    if (isFirstLate) baselineLog.Append((FixedString32Bytes) " Late!");
                    if (reader.HasFailedReads) baselineLog.Append((FixedString32Bytes) " HasFailedReads!");
                    enablePacketLogging.LogToPacket(baselineLog);
                }
#endif

                // 反序列化后续 N 条命令
                var assumedTickIndex = baselineReceivedCommand.Tick;
                for (uint inputIndex = 1; inputIndex < numCommandsSent; ++inputIndex)
                {
                    var receivedCommand = default(TCommandData);
                    receivedCommand.Tick = ReadTickDeltaCompressed(ref reader, ref assumedTickIndex);

                    // 如果此标志为 false，则输入 i-1 与输入 i 相同
                    // 注意命令按倒序排列，因此 i-1 实际上是下一个 Tick 的输入
                    var changeBit = reader.ReadRawBits(1);
                    if (changeBit == 0)
                    {
                        // 从技术上讲，无效 Tick 的 changeBit 始终为零
                        var copyOfNextInput = receivedCommand.Tick.IsValid
                            ? reusableTempBuffer[(int) (inputIndex - 1)]
                            : default;
                        copyOfNextInput.Tick = receivedCommand.Tick;
                        reusableTempBuffer[(int) inputIndex] = copyOfNextInput;
                    }
                    else
                    {
                        serializer.Deserialize(ref reader, deserializeState, ref receivedCommand, baselineReceivedCommand,
                            compressionModel);
                        reusableTempBuffer[(int) inputIndex] = receivedCommand;
                    }

                    // 判断此输入是否到达过晚
                    // 注意：这里没有检查第一条输入，从技术上讲它本身也可能迟到
                    bool isLate = receivedCommand.Tick.IsValid && receivedCommand.Tick.TicksSince(serverTick) < 0;
                    if (isLate) arrivalStats.NumArrivedTooLate++;

#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        FixedString512Bytes debug = $"\t[{inputIndex}]=[{reusableTempBuffer[(int) inputIndex].Tick.ToFixedString()}|{reusableTempBuffer[(int) inputIndex].ToFixedString()}] (cb:{changeBit})";
                        if (isLate) debug.Append((FixedString32Bytes) " Late!");
                        if (reader.HasFailedReads) debug.Append((FixedString32Bytes) " HasFailedReads!");
                        enablePacketLogging.LogToPacket(debug);
                    }
#endif
                }

                var totalBitsRead = reader.GetBitsRead() - readerStartBit;
                totalBitsRead = ((totalBitsRead + 7) / 8) * 8; // 对齐到整字节，使其与发送端完全一致
#if NETCODE_DEBUG
                if(enablePacketLogging.IsPacketCacheCreated)
                    enablePacketLogging.LogToPacket($"\t---\n\t{CommandDataUtility.FormatBitsBytes(totalBitsRead)}\n");
#endif

                // 按命令生成顺序添加，而不是按发送顺序添加
                for (int i = (int) numCommandsSent - 1; i >= 0; --i)
                {
                    if (!reusableTempBuffer[i].Tick.IsValid)
                        continue;
                    var input = reusableTempBuffer[i];
                    // 这是特殊情况，因为它可能是当前 Server Tick 能获得的最新输入，必须以某种方式保存
                    // 获取前一个 Tick 的数据时也需要返回前一个 Tick 实际使用的内容
                    // 因此把最近收到的输入 Tick 伪装成当前 Server Tick，尽管它实际上属于已经模拟过的 Tick
                    // 如果还有更新且应由 Server Tick 使用的输入，它必须包含在此数据包中，并会覆盖 Server Tick 的状态
                    if (serverTick.IsNewerThan(reusableTempBuffer[i].Tick))
                        input.Tick = serverTick;
                    var didReplaceExisting = command.AddCommandData(input);
                    if (didReplaceExisting) arrivalStats.NumRedundantResends++;
                }

                // 更新统计信息
                {
                    arrivalStats.NumCommandPacketsArrived++;
                    arrivalStats.NumCommandsArrived += numCommandsSent;
                    arrivalStats.AvgCommandPayloadSizeInBits = arrivalStats.AvgCommandPayloadSizeInBits == 0
                        ? totalBitsRead
                        : math.lerp(arrivalStats.AvgCommandPayloadSizeInBits, totalBitsRead, 0.125f);
                }
            }

            /// <summary>
            /// 此实现应与 CommandSendSystem 中的 Writer 方法对照检查
            /// </summary>
            /// <param name="reader"></param>
            /// <param name="assumedTickIndex"></param>
            /// <returns></returns>
            private NetworkTick ReadTickDeltaCompressed(ref DataStreamReader reader, ref NetworkTick assumedTickIndex)
            {
                if (Hint.Likely(assumedTickIndex.IsValid))
                {
                    var delta = reader.ReadRawBits(CommandSendSystemGroup.k_TickDeltaBits) + 1;
                    if (Hint.Likely(delta <= 3))
                    {
                        assumedTickIndex.Subtract(delta);
                        return assumedTickIndex;
                    }
                    // 从上一个值减去 4，因为差值不可能是 -1、-2 或 -3
                    if(assumedTickIndex.IsValid) assumedTickIndex.Subtract(4);
                    assumedTickIndex.SerializedData = reader.ReadPackedUIntDelta(assumedTickIndex.SerializedData, compressionModel);
                    return assumedTickIndex;
                }

                reader.ReadRawBits(CommandSendSystemGroup.k_TickDeltaBits);
                assumedTickIndex.SerializedData = reader.ReadPackedUIntDelta(assumedTickIndex.SerializedData, compressionModel);
                return assumedTickIndex;
            }

            /// <summary>
            /// 解码 Chunk 内所有连接的 <see cref="IncomingCommandDataStreamBuffer"/> 中的命令
            /// 通过 <see cref="CommandTarget"/> 的目标 Entity，或在启用时通过 <see cref="AutoCommandTarget"/>，
            /// 查找应将命令加入队列的目标 Entity
            /// </summary>
            /// <param name="chunk">包含待解码命令的 Chunk</param>
            /// <param name="orderIndex">顺序索引</param>
            public unsafe void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var snapshotAcks = chunk.GetNativeArray(ref snapshotAckType);
                var snapshotAcksWritePtr = (NetworkSnapshotAck*)snapshotAcks.GetUnsafePtr();
                var networkIds = chunk.GetNativeArray(ref networkIdType);
                var commandTargets = chunk.GetNativeArray(ref commmandTargetType);
                var cmdBuffers = chunk.GetBufferAccessor(ref cmdBufferType);
#if NETCODE_DEBUG
                var enablePacketLoggings = chunk.Has(ref enablePacketLoggingType)
                    ? chunk.GetNativeArray(ref enablePacketLoggingType)
                    : default(NativeArray<EnablePacketLogging>);
#else
                var enablePacketLoggings = default(NativeArray<EnablePacketLogging>);
#endif
                Span<TCommandData> reusableTempBuffer = stackalloc TCommandData[CommandSendSystemGroup.k_MaxInputBufferSendSize];

                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    var owner = networkIds[i].Value;
                    ref var snapshotAck = ref snapshotAcksWritePtr[i];
                    var buffer = cmdBuffers[i];
                    if (buffer.Length < 4)
                        continue;

                    DataStreamReader reader = buffer.AsDataStreamReader();
                    var tick = reader.ReadUInt();
                    while (reader.GetBytesRead() + 10 <= reader.Length)
                    {
                        var readerStartBit = reader.GetBitsRead();

                        var hash = reader.ReadULong();
                        var commandPayloadLength = reader.ReadUShort();
                        var startPos = reader.GetBytesRead();
                        if (hash == stableHash)
                        {
                            // 读取 Ghost ID
                            var ghostId = reader.ReadInt();
                            var spawnTick = new NetworkTick {SerializedData = reader.ReadUInt()};
                            var spawnedGhost = new SpawnedGhost {ghostId = ghostId, spawnTick = spawnTick};

                            var numCommandsSent = reader.ReadRawBits(CommandSendSystemGroup.k_MaxInputBufferSendBits) + 1;

                            var targetEntity = commandTargets[i].targetEntity;
                            if (ghostId != 0)
                            {
                                targetEntity = Entity.Null;
                                if (ghostMap.TryGetValue(spawnedGhost, out var ghostEnt))
                                {
                                    if (ghostOwnerFromEntity.HasComponent(ghostEnt) && autoCommandTargetFromEntity.HasComponent(ghostEnt))
                                    {
                                        var ghostOwner = ghostOwnerFromEntity[ghostEnt].NetworkId;
                                        if (ghostOwner == owner)
                                        {
                                            if (autoCommandTargetFromEntity[ghostEnt].Enabled)
                                            {
                                                targetEntity = ghostEnt;
                                            }
                                            else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) but AutoCommandTarget is Disabled on Server.");
                                        }
                                        else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which is owned by another player ({ghostOwner})!");
                                    }
                                    else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which hasn't got the GhostOwner + AutoCommandTarget combination of components!");
                                }
                                else LogToPacket(enablePacketLoggings, i, $"[CRS][{default(TCommandDataSerializer).ToFixedString()}] Client {owner} sent input for ghostId (id:{ghostId},spawnTick:{spawnTick.ToFixedString()}) which does not exist on the server!");
                            }

                            if (commandData.HasBuffer(targetEntity))
                            {
#if NETCODE_DEBUG
                                var enablePacketLogging = enablePacketLoggings.IsCreated ? enablePacketLoggings[i] : default;
#else
                                var enablePacketLogging = default(EnablePacketLogging);
#endif
                                Deserialize(ref reader, targetEntity, tick, snapshotAck, numCommandsSent, reusableTempBuffer, ref snapshotAck.CommandArrivalStatistics, ref enablePacketLogging, readerStartBit, in spawnedGhost);

                                // 验证接收的字节数，不验证 bit，因为发送端没有发送精确 bit 数
                                var actualBitsRead = reader.GetBytesRead() - startPos;
                                if (reader.HasFailedReads || actualBitsRead != commandPayloadLength)
                                {
                                    netDebug.LogError($"Failed to correctly deserialize command '{ComponentType.ReadWrite<TCommandData>().ToFixedString()}' on {targetEntity.ToFixedString()} from NID[{owner}]! Expected: {commandPayloadLength} bytes, actual {actualBitsRead} bytes, reader.HasFailedReads: {reader.HasFailedReads}!");
                                    // TODO 检查此错误在生产环境中的发生频率，并决定是否应断开该玩家
                                }
                            }
                        }

                        reader.SeekSet(startPos + commandPayloadLength);
                    }
                }
            }

            [Conditional("NETCODE_DEBUG")]
            // ReSharper disable UnusedParameter.Local
            private void LogToPacket(in NativeArray<EnablePacketLogging> enablePacketLoggings, int index, in FixedString512Bytes msg)
            {
                // ReSharper enable UnusedParameter.Local
#if NETCODE_DEBUG
                if (!enablePacketLoggings.IsCreated) return;
                var epl = enablePacketLoggings[index];
                if (!epl.IsPacketCacheCreated) return;
                epl.LogToPacket(msg);
#endif
            }
        }

        /// <summary>
        /// 调度处理 Job 时使用的查询
        /// </summary>
        public EntityQuery Query => m_entityQuery;
        private EntityQuery m_entityQuery;
        private EntityQuery m_SpawnedGhostEntityMapQuery;
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_NetDebugQuery;
        private StreamCompressionModel m_CompressionModel;

        private BufferLookup<TCommandData> m_TCommandDataFromEntity;
        private ComponentLookup<CommandDataInterpolationDelay> m_CommandDataInterpolationDelayFromEntity;
        private ComponentLookup<GhostOwner> m_GhostOwnerLookup;
        private ComponentLookup<AutoCommandTarget> m_AutoCommandTargetFromEntity;
        private BufferTypeHandle<IncomingCommandDataStreamBuffer> m_IncomingCommandDataStreamBufferComponentHandle;
        private ComponentTypeHandle<NetworkSnapshotAck> m_NetworkSnapshotAckComponentHandle;
        private ComponentTypeHandle<EnablePacketLogging> m_EnablePacketLoggingTypeComponentHandle;
        private ComponentTypeHandle<NetworkId> m_NetworkIdComponentHandle;
        private ComponentTypeHandle<CommandTarget> m_CommandTargetComponentHandle;

        /// <summary>
        /// 由 Job System 的代码生成器调用
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            m_CompressionModel = StreamCompressionModel.Default;
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamInGame, IncomingCommandDataStreamBuffer, NetworkSnapshotAck>()
                .WithAllRW<CommandTarget>();
            m_entityQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<SpawnedGhostEntityMap>();
            m_SpawnedGhostEntityMapQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkTime>();
            m_NetworkTimeQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetDebug>();
            m_NetDebugQuery = state.GetEntityQuery(builder);

            m_TCommandDataFromEntity = state.GetBufferLookup<TCommandData>();
            m_CommandDataInterpolationDelayFromEntity = state.GetComponentLookup<CommandDataInterpolationDelay>();
            m_GhostOwnerLookup = state.GetComponentLookup<GhostOwner>(true);
            m_AutoCommandTargetFromEntity = state.GetComponentLookup<AutoCommandTarget>(true);
            m_IncomingCommandDataStreamBufferComponentHandle = state.GetBufferTypeHandle<IncomingCommandDataStreamBuffer>(true);
            m_NetworkSnapshotAckComponentHandle = state.GetComponentTypeHandle<NetworkSnapshotAck>(false);
            m_EnablePacketLoggingTypeComponentHandle = state.GetComponentTypeHandle<EnablePacketLogging>(false);
            m_NetworkIdComponentHandle = state.GetComponentTypeHandle<NetworkId>(true);
            m_CommandTargetComponentHandle = state.GetComponentTypeHandle<CommandTarget>(true);

            state.RequireForUpdate(m_entityQuery);
            state.RequireForUpdate<TCommandData>();
        }

        /// <summary>
        /// 初始化处理 Job 的内部状态，应从 ISystem 的 OnUpdate 调用
        /// </summary>
        /// <param name="state">原始 Entity System 状态</param>
        /// <returns>已构造并完成状态初始化的 <see cref="ReceiveJobData"/></returns>
        public ReceiveJobData InitJobData(ref SystemState state)
        {
            m_TCommandDataFromEntity.Update(ref state);
            m_CommandDataInterpolationDelayFromEntity.Update(ref state);
            m_GhostOwnerLookup.Update(ref state);
            m_AutoCommandTargetFromEntity.Update(ref state);
            m_IncomingCommandDataStreamBufferComponentHandle.Update(ref state);
            m_NetworkSnapshotAckComponentHandle.Update(ref state);
            m_EnablePacketLoggingTypeComponentHandle.Update(ref state);
            m_NetworkIdComponentHandle.Update(ref state);
            m_CommandTargetComponentHandle.Update(ref state);
            var recvJob = new ReceiveJobData
            {
                commandData = m_TCommandDataFromEntity,
                delayFromEntity = m_CommandDataInterpolationDelayFromEntity,
                ghostOwnerFromEntity = m_GhostOwnerLookup,
                autoCommandTargetFromEntity = m_AutoCommandTargetFromEntity,
                compressionModel = m_CompressionModel,
                cmdBufferType = m_IncomingCommandDataStreamBufferComponentHandle,
                snapshotAckType = m_NetworkSnapshotAckComponentHandle,
                enablePacketLoggingType = m_EnablePacketLoggingTypeComponentHandle,
                networkIdType = m_NetworkIdComponentHandle,
                commmandTargetType = m_CommandTargetComponentHandle,
                ghostMap = m_SpawnedGhostEntityMapQuery.GetSingleton<SpawnedGhostEntityMap>().Value,
                serverTick = m_NetworkTimeQuery.GetSingleton<NetworkTime>().ServerTick,
                netDebug = m_NetDebugQuery.GetSingleton<NetDebug>(),
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash
            };
            return recvJob;
        }
    }
}
