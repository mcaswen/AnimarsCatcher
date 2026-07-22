#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Mathematics;

namespace Unity.NetCode
{
    /// <summary>
    /// 代码生成器使用的 Singleton，保存客户端输入发生变化的 Tick 映射
    /// 当输入没有变化时，预测循环可以据此批处理多个步骤
    /// </summary>
    public struct UniqueInputTickMap : IComponentData
    {
        /// <summary>
        /// 输入相较前一帧发生变化的 Tick 集合，Value 不会被使用，但通常设为与 Key 相同的 Tick
        /// </summary>
        public NativeParallelHashMap<NetworkTick, NetworkTick>.ParallelWriter Value;
        internal NativeParallelHashMap<NetworkTick, NetworkTick> TickMap;
    }

    /// <summary>
    /// 所有输入采集系统的父 Group，仅存在于 Client World 和 Local World，后者用于让单机模式复用同一套输入采集系统
    /// 它在 <see cref="CommandSendSystemGroup"/> 之前运行，以消除输入采集与命令提交之间的延迟
    /// 所有把用户输入转换为 <see cref="ICommandData"/> 命令数据的系统都必须在此 Group 中更新，
    /// 例如读取 <see cref="UnityEngine.Input"/> 的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.LocalSimulation, WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public partial class GhostInputSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 所有生成系统的父 Group，这些系统把数据从 <see cref="IInputComponentData"/> 复制到其底层 <see cref="InputBufferData{T}"/>
    /// 后者是保存已生成用户命令的环形 Buffer
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup), OrderLast = true)]
    public partial class CopyInputToCommandBufferSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 所有生成系统的父 Group，这些系统把数据从底层 <see cref="InputBufferData{T}"/> 复制到其父 <see cref="IInputComponentData"/>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation,
                       WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class CopyCommandBufferToInputSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 此 Group 包含所有核心生成的命令比较系统，用于识别客户端输入发生变化的 Tick
    /// 参见 <see cref="m_UniqueInputTicks"/>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation, WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostInputSystemGroup))]
    public partial class CompareCommandSystemGroup : ComponentSystemGroup
    {
        private NativeParallelHashMap<NetworkTick, NetworkTick> m_UniqueInputTicks;
        /// <summary>
        /// 创建 <see cref="UniqueInputTickMap"/> Singleton，并保存 UniqueInputTicks HashMap 的引用
        /// </summary>
        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                base.OnCreate();
                Enabled = false;
                return;
            }
            m_UniqueInputTicks = new NativeParallelHashMap<NetworkTick, NetworkTick>(CommandDataUtility.k_CommandDataMaxSize * 4, Allocator.Persistent);
            var singletonEntity = EntityManager.CreateEntity(ComponentType.ReadWrite<UniqueInputTickMap>());
            EntityManager.SetName(singletonEntity, "UniqueInputTickMap-Singleton");
            EntityManager.SetComponentData(singletonEntity, new UniqueInputTickMap{Value = m_UniqueInputTicks.AsParallelWriter(), TickMap = m_UniqueInputTicks});

            base.OnCreate();
        }
        /// <summary>
        /// 释放所有已分配资源
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();

            m_UniqueInputTicks.Dispose();
        }
    }

    /// <summary>
    /// 所有命令序列化系统的父 Group，这些系统把 <see cref="ICommandData"/> 结构体序列化到
    /// <see cref="OutgoingCommandDataStreamBuffer"/> 缓冲区
    /// 随后由 <see cref="CommandSendPacketSystem"/> 发送序列化命令
    /// 此 Group 仅存在于 Client World
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation, WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostInputSystemGroup))]
    // 此依赖仅用于发送 Ack
    [UpdateAfter(typeof(GhostReceiveSystem))]
    public partial class CommandSendSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// 单个 Command Payload 的最大序列化大小，包含 Command Header
        /// 因此应在差分压缩后验证
        /// </summary>
        public const int k_MaxCommandSerializedPayloadBytes = 1024;

        /// <summary>
        /// 单个数据包中客户端能够为指定 Ghost 向服务器发送的最大命令数量
        /// 由 <see cref="k_MaxInputBufferSendBits"/> 决定，2^5 表示 (0,31)，排除零后得到 (1,32)
        /// </summary>
        /// <remarks>实际发送的命令数量为 `<see cref="ClientTickRate.TargetCommandSlack"/> + <see cref="ClientTickRate.NumAdditionalCommandsToSend"/>`</remarks>
        public const int k_MaxInputBufferSendSize = 1 << k_MaxInputBufferSendBits;

        /// <summary>
        /// 用多少个 bit 发送 Buffer 长度
        /// <see cref="k_MaxInputBufferSendBits"/>
        /// </summary>
        internal const int k_MaxInputBufferSendBits = 5;

        /// <summary>
        /// 为 Buffer 中每个较早 Tick 的 Tick 差值分配多少个 bit
        /// 注意：最大值是保留给“使用 Huffman”的哨兵值
        /// </summary>
        internal const int k_TickDeltaBits = 2;

        private NetworkTick m_LastInputTargetTick;

        protected override void OnCreate()
        {
            base.OnCreate();
            if (World.IsHost())
                Enabled = false;
        }
        protected override void OnUpdate()
        {
            var clientNetTime = SystemAPI.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            // 确保每个 Tick 只发送一次 Ack，仅在使用动态时间步时触发
            if (inputTargetTick.IsValid && inputTargetTick != m_LastInputTargetTick)
                base.OnUpdate();
            m_LastInputTargetTick = inputTargetTick;
        }
    }

    /// <summary>
    /// <para>负责构建 Command Packet 并发送到服务器的系统
    /// 作为 Command 协议的一部分，它会执行以下操作：</para>
    /// <para>- 刷新 <see cref="OutgoingCommandDataStreamBuffer"/> 中的全部序列化命令</para>
    /// <para>- 向服务器确认最近收到的 Snapshot</para>
    /// <para>- 把客户端本地时间和远端时间发回服务器，用于计算 RTT</para>
    /// <para>- 把已加载的 Ghost Prefab 信息发送到服务器</para>
    /// <para>- 计算当前客户端插值延迟，用于延迟补偿</para>
    /// </summary>
    [UpdateInGroup(typeof(CommandSendSystemGroup), OrderLast = true)]
    [BurstCompile]
    internal partial struct CommandSendPacketSystem : ISystem
    {
        private StreamCompressionModel m_CompressionModel;
        private EntityQuery m_connectionQuery;
        // 数据包 Header 总计由 29 字节组成
        private const int k_CommandHeadersBytes =
            1 + // 协议 ID
            4 + // 最近从服务器收到的 Snapshot Tick
            4 + // 已接收 Snapshot Mask
            4 + // 本地时间，用于计算 RTT
            4 + // 本地时间与最近收到的远端时间之差，用于计算已流逝 RTT，并剔除客户端重发 Ack 所花费的时间
            4 + // 插值延迟
            2 + // 已加载 Prefab 数量
            1 +  // 表示 Command Tick 是完整 Tick 还是 Partial Tick
            4; // 第一条 Command 的 Tick

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamConnection, NetworkStreamInGame, NetworkSnapshotAck>()
                .WithAllRW<OutgoingCommandDataStreamBuffer>();
            m_connectionQuery = state.GetEntityQuery(builder);
            m_CompressionModel = StreamCompressionModel.Default;

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate(m_connectionQuery);
        }

        [BurstCompile]
        [WithAll(typeof(NetworkStreamInGame))]
        partial struct CommandSendPacket : IJobEntity
        {
            public ConcurrentDriverStore concurrentDriverStore;
            public NetDebug netDebug;
#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<uint> netStats;
#endif
            public uint localTime;
            public int numLoadedPrefabs;
            public NetworkTick inputTargetTick;
            public float inputTargetTickFraction;
            public uint interpolationDelay;
            public unsafe void Execute(DynamicBuffer<OutgoingCommandDataStreamBuffer> rpcData,
                    in NetworkStreamConnection connection, in NetworkSnapshotAck ack)
            {
                if (!connection.Value.IsCreated)
                    return;

                var concurrentDriver = concurrentDriverStore.GetConcurrentDriver(connection.DriverId);
                var requiredPayloadSize = k_CommandHeadersBytes + rpcData.Length;
                int maxSnapshotSizeWithoutFragmentation = concurrentDriver.driver.m_DriverSender.m_SendQueue.PayloadCapacity - concurrentDriver.driver.MaxHeaderSize(concurrentDriver.unreliablePipeline);
                var pipelineToUse = requiredPayloadSize > maxSnapshotSizeWithoutFragmentation ? concurrentDriver.unreliableFragmentedPipeline : concurrentDriver.unreliablePipeline;
                int result;
                if ((result = concurrentDriver.driver.BeginSend(pipelineToUse, connection.Value, out var writer, requiredPayloadSize)) < 0)
                {
                    netDebug.LogWarning($"CommandSendPacket BeginSend failed with errorCode: {result} on {connection.Value.ToFixedString()}!");
                    rpcData.Clear();
                    return;
                }
                // 如果修改下面任意写入操作，例如添加、移除或改变类型，也必须更新 k_CommandHeadersBytes 常量
                writer.WriteByte((byte)NetworkStreamProtocol.Command);
                writer.WriteUInt(ack.LastReceivedSnapshotByLocal.SerializedData);
                writer.WriteUInt(ack.ReceivedSnapshotByLocalMask);
                writer.WriteUInt(localTime);
                uint returnTime = ack.CalculateReturnTime(localTime);
                writer.WriteUInt(returnTime);
                writer.WriteUInt(interpolationDelay);
                writer.WriteUShort((ushort)numLoadedPrefabs);
                writer.WriteByte((byte)(inputTargetTickFraction < 1f ? 0 : 1));
                writer.WriteUInt(inputTargetTick.SerializedData);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Assertions.Assert.AreEqual(writer.Length, k_CommandHeadersBytes);
#endif
                writer.WriteBytesUnsafe((byte*)rpcData.GetUnsafeReadOnlyPtr(), rpcData.Length);
                rpcData.Clear();

#if UNITY_EDITOR || NETCODE_DEBUG
                netStats[0] = inputTargetTick.SerializedData;
                netStats[1] = (uint)writer.Length;
#endif

                if(writer.HasFailedWrites)
                    netDebug.LogError($"CommandSendPacket job triggered Writer.HasFailedWrites on {connection.Value.ToFixedString()}, despite allocating the collection based on needed size!");
                if ((result = concurrentDriver.driver.EndSend(writer)) <= 0)
                    netDebug.LogError($"CommandSendPacket EndSend failed with errorCode: {result} on {connection.Value.ToFixedString()}!");
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var clientNetTime = SystemAPI.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            // 插值到达指定 Tick 前的剩余时间，需要将其加入差值
            var subTickDeltaAdjust = 1 - clientNetTime.InterpolationTickFraction;
            // 实际到达 Server Tick 前的剩余时间，需要从差值中扣除
            subTickDeltaAdjust -= 1 - clientNetTime.ServerTickFraction;
            var interpolationDelay = clientNetTime.ServerTick.TicksSince(clientNetTime.InterpolationTick);
            if (subTickDeltaAdjust >= 1)
                ++interpolationDelay;
            else if (subTickDeltaAdjust < 0)
                --interpolationDelay;
            interpolationDelay = math.max(interpolationDelay, 0);

            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            var sendJob = new CommandSendPacket
            {
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
#if UNITY_EDITOR || NETCODE_DEBUG
                netStats = SystemAPI.GetSingletonRW<GhostStatsCollectionCommand>().ValueRO.Value,
#endif
                localTime = NetworkTimeSystem.TimestampMS,
                numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs,
                inputTargetTick = inputTargetTick,
                inputTargetTickFraction = clientNetTime.ServerTickFraction,
                interpolationDelay = (uint)interpolationDelay
            };
            state.Dependency = sendJob.Schedule(state.Dependency);
            state.Dependency = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(state.Dependency);
        }
    }

    /// <summary>
    /// 用于实现命令发送系统的辅助结构体
    /// 通常由代码生成器使用，只应在特殊情况下直接使用
    /// </summary>
    /// <typeparam name="TCommandDataSerializer">实现 ICommandDataSerializer 的 Unmanaged CommandDataSerializer</typeparam>
    /// <typeparam name="TCommandData">实现 ICommandData 的 Unmanaged CommandData</typeparam>
    public struct CommandSendSystem<TCommandDataSerializer, TCommandData>
        where TCommandData : unmanaged, ICommandData
        where TCommandDataSerializer : unmanaged, ICommandDataSerializer<TCommandData>
    {
        /// <summary>
        /// 代码生成的 Command Job 使用的辅助结构体，负责把 <see cref="ICommandData"/>
        /// 序列化到客户端连接的 <see cref="OutgoingCommandDataStreamBuffer"/>
        /// </summary>
        public struct SendJobData
        {
            /// <summary>
            /// 用于访问 Chunk 数据的只读 <see cref="CommandTarget"/> 类型句柄
            /// </summary>
            [ReadOnly] public ComponentTypeHandle<CommandTarget> commmandTargetType;
            /// <summary>
            /// 用于访问 Chunk 数据的 <see cref="OutgoingCommandDataStreamBuffer"/> Buffer 类型句柄
            /// 这是该 Job 的输出 Buffer
            /// </summary>
            public BufferTypeHandle<OutgoingCommandDataStreamBuffer> outgoingCommandBufferType;
            /// <summary>
            /// 用于转储数据包的 <see cref="EnablePacketLogging"/> 类型句柄
            /// </summary>
            public ComponentTypeHandle<EnablePacketLogging> enablePacketLoggingType;
            /// <summary>
            /// 用于从目标 Entity 获取 Input Buffer 的访问器
            /// </summary>
            [ReadOnly] public BufferLookup<TCommandData> inputFromEntity;
            /// <summary>
            /// 用于访问 Chunk 数据的只读 <see cref="GhostInstance"/> 类型句柄
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
            /// <summary>
            /// 用于从目标 Ghost Entity 获取 <see cref="GhostOwner"/> 的只读访问器
            /// </summary>
            [ReadOnly] public ComponentLookup<GhostOwner> ghostOwnerFromEntity;
            /// <summary>
            /// 用于从目标 Ghost Entity 获取 <see cref="AutoCommandTarget"/> 的只读访问器
            /// </summary>
            [ReadOnly] public ComponentLookup<AutoCommandTarget> autoCommandTargetFromEntity;
            /// <summary>
            /// 对旧输入进行差分编码时使用的压缩模型
            /// 第一条输入，也就是当前 Tick 的输入，按原值序列化；较早的输入以第一条输入为 Baseline 进行差分序列化，以减少带宽
            /// </summary>
            public StreamCompressionModel compressionModel;
            /// <summary>
            /// 命令应在服务器上执行的 Server Tick
            /// </summary>
            public NetworkTick inputTargetTick;
            /// <summary>
            /// 上一次发送此命令对应的 Server Tick
            /// </summary>
            public NetworkTick prevInputTargetTick;
            /// <summary>
            /// 所有具有 <see cref="AutoCommandTarget"/> 组件的 Ghost Entity 列表
            /// </summary>
            [ReadOnly] public NativeList<Entity> autoCommandTargetEntities;
            /// <summary>
            /// Command 类型的稳定类型 Hash
            /// 该值会被序列化，并在服务器端用于匹配和验证已发送输入数据的正确性
            /// </summary>
            public ulong stableHash;
            /// <summary>
            /// 应发送多少个 Tick 的输入
            /// 该值表示从当前 Tick 开始向前计算的最近 N 个 Tick
            /// </summary>
            public uint numCommandsToSend;

            void Serialize(DynamicBuffer<OutgoingCommandDataStreamBuffer> rpcData, Entity targetEntity, bool isAutoTarget, ref EnablePacketLogging enablePacketLogging)
            {
                var inputBuffer = inputFromEntity[targetEntity];
                // 检查 Buffer 是否包含待发送 Tick 的数据，首先确认它是否包含任何数据
                if (!inputBuffer.GetDataAtTick(inputTargetTick, out var baselineInputData))
                {
#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                        enablePacketLogging.LogToPacket($"\n[CSS][{default(TCommandDataSerializer).ToFixedString()}:{stableHash}] No data for {targetEntity.ToFixedString()} on inputTargetTick: {inputTargetTick.ToFixedString()}, ignoring.\n");
#endif
                    return;
                }
                // 接着检查最近输入是否已发送，以及当前最新数据是否已经超出 Buffer 容量
                // 检查是否已发送对于处理客户端性能极差的情况十分重要
                if (prevInputTargetTick.IsValid && !baselineInputData.Tick.IsNewerThan(prevInputTargetTick) && inputTargetTick.TicksSince(baselineInputData.Tick) >= CommandDataUtility.k_CommandDataMaxSize)
                {
#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                        enablePacketLogging.LogToPacket($"\n[CSS][{default(TCommandDataSerializer).ToFixedString()}:{stableHash}] Already sent input for {targetEntity.ToFixedString()} on inputTargetTick: {baselineInputData.Tick}, ignoring.\n");
#endif
                    return;
                }

                var oldLen = rpcData.Length;
                const int maxHeaderSize = sizeof(ulong) + // 命令 Hash
                                       sizeof(ushort) + // 序列化大小
                                       sizeof(int) + // Ghost ID 或 0
                                       sizeof(uint) + // spawnTick 或 0
                                       sizeof(byte) + // numCommandsToSend，实际只占 5 bit
                                       sizeof(int); // 当前 Tick

                rpcData.ResizeUninitialized(oldLen + CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes + maxHeaderSize);
                var writer = new DataStreamWriter(rpcData.Reinterpret<byte>().AsNativeArray().GetSubArray(oldLen,
                    CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes));

                writer.WriteULong(stableHash);
                var lengthWriter = writer;
                writer.WriteUShort(0);
                var startLength = writer.Length;
                GhostInstance ghostComponent;
                if (isAutoTarget)
                {
                    ghostComponent = ghostFromEntity[targetEntity];
                    writer.WriteInt(ghostComponent.ghostId);
                    writer.WriteUInt(ghostComponent.spawnTick.SerializedData);
                }
                else
                {
                    ghostComponent = default;
                    writer.WriteInt(0);
                    writer.WriteUInt(0);
                }

                // 待发送命令数量
                writer.WriteRawBits(numCommandsToSend - 1, CommandSendSystemGroup.k_MaxInputBufferSendBits);

                // 写入第一条输入
                var serializer = default(TCommandDataSerializer);
                var serializerState = new RpcSerializerState
                {
                    GhostFromEntity = ghostFromEntity,
                    CompressionModel = compressionModel,
                };
                writer.WriteUInt(baselineInputData.Tick.SerializedData);

#if NETCODE_DEBUG
                var firstSerializeLengthInBits = writer.LengthInBits;
#endif
                serializer.Serialize(ref writer, serializerState, baselineInputData, default, compressionModel);
#if NETCODE_DEBUG
                firstSerializeLengthInBits = writer.LengthInBits - firstSerializeLengthInBits;
#endif

                // Target Tick 是早于刚刚采样 Tick 的最近 Tick
                var targetTick = baselineInputData.Tick;
                if (targetTick.IsValid)
                {
                    targetTick.Decrement();
                }

                // 以差分压缩方式写入后续 N 条输入
                TCommandData inputData = baselineInputData;


#if NETCODE_DEBUG
                var payloadBits = firstSerializeLengthInBits;
                var payloadTickBits = 32;

                if (enablePacketLogging.IsPacketCacheCreated)
                {
                    enablePacketLogging.LogToPacket($"[CSS][{serializer.ToFixedString()}:{stableHash}] Sent for inputTargetTick: {inputTargetTick.ToFixedString()} | {targetEntity.ToFixedString()} on {ghostComponent.ToFixedString()} | isAutoTarget:{isAutoTarget}\n\t| stableHash: {CommandDataUtility.FormatBitsBytes(64)}\n\t| commandSize: {CommandDataUtility.FormatBitsBytes(16)}\n\t| autoCommandTargetGhost: {CommandDataUtility.FormatBitsBytes(64)}\n\t| numCommandsToSend({numCommandsToSend}): {CommandDataUtility.FormatBitsBytes(CommandSendSystemGroup.k_MaxInputBufferSendBits)}");
                    enablePacketLogging.LogToPacket($"\t[b]=[{baselineInputData.Tick.ToFixedString()}|{baselineInputData.ToFixedString()}] (tick: {CommandDataUtility.FormatBitsBytes(32)}) (data: {CommandDataUtility.FormatBitsBytes(firstSerializeLengthInBits)})");
                }
#endif
                var assumedTickIndex = baselineInputData.Tick;
                for (uint inputIndex = 1; inputIndex < numCommandsToSend; ++inputIndex)
                {
                    var prevInputData = inputData;
                    var changeBit = GetDataAtTickAndCmp(targetTick, ref prevInputData, inputBuffer, ref inputData, serializer);
#if NETCODE_DEBUG
                    var tickBits = writer.LengthInBits;
#endif
                    WriteTickDeltaCompressed(ref assumedTickIndex, ref writer, inputData);
#if NETCODE_DEBUG
                    FixedString512Bytes debug = default;
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        tickBits = writer.LengthInBits - tickBits;
                        payloadTickBits += tickBits;
                        debug.Append((FixedString512Bytes) $"\t[{inputIndex}]=[{inputData.Tick.ToFixedString()}|{inputData.ToFixedString()}] (cb: {changeBit}) (t?: {CommandDataUtility.FormatBitsBytes(tickBits)})");
                    }
#endif

                    // 如果没有变化，则通过 1 bit Change Mask 标志完全跳过序列化
                    writer.WriteRawBits(changeBit, 1);

                    if (changeBit != 0)
                    {
#if NETCODE_DEBUG
                        var successiveSerializeLengthInBits = writer.LengthInBits;
#endif

                        serializer.Serialize(ref writer, serializerState, inputData, baselineInputData, compressionModel);
#if NETCODE_DEBUG
                        if (enablePacketLogging.IsPacketCacheCreated)
                        {
                            var dataBits = writer.LengthInBits - successiveSerializeLengthInBits;
                            payloadBits += dataBits;
                            debug.Append((FixedString512Bytes) $" (data: {CommandDataUtility.FormatBitsBytes(dataBits)})");
                        }
#endif
                    }

#if NETCODE_DEBUG
                    if (enablePacketLogging.IsPacketCacheCreated)
                    {
                        if (writer.HasFailedWrites)
                            debug.Append((FixedString32Bytes) "\nHasFailedWrites!");
                        enablePacketLogging.LogToPacket(debug);
                    }
#endif
                    targetTick = inputData.Tick;
                    if (targetTick.IsValid)
                    {
                        targetTick.Decrement();
                    }
                }

                var flush = writer.LengthInBits;
                writer.Flush();
                flush = writer.LengthInBits - flush;

                if (writer.HasFailedWrites)
                {
                    // TODO 进一步改进
                    // 理想情况下应在此输出原始 TCommandData 类型，但对于 IInputCommands，目前几乎无法做到
                    // 除非把原始组件类型一直向下传递，因为类型信息此时已经丢失
                    UnityEngine.Debug.LogError($"CommandSendSystem failed to serialize '{ComponentType.ReadWrite<TCommandData>().ToFixedString()}' as the serialized payload is too large (limit: {CommandSendSystemGroup.k_MaxCommandSerializedPayloadBytes})! For redundancy, we pack the command for the current server tick and the last {numCommandsToSend} (configurable) values (delta-compressed) inside the payload. Please try to keep ICommandData or IInputComponentData small (tens of bytes). Remember they are serialized at the `SimulationTickRate` and can consume a lot of the client outgoing and server ingress bandwidth.\nContents:'{inputData.ToFixedString()}'.");
                }

#if NETCODE_DEBUG
                var totalCommandBits = writer.LengthInBits; // 此操作之后 Writer 会失效
#endif
                var totalCommandBytes = (ushort)(writer.Length - startLength);
                lengthWriter.WriteUShort(totalCommandBytes);
                rpcData.ResizeUninitialized(oldLen + writer.Length);

#if NETCODE_DEBUG
                if (enablePacketLogging.IsPacketCacheCreated)
                    enablePacketLogging.LogToPacket($"\t| payloadTicks: {CommandDataUtility.FormatBitsBytes(payloadTickBits)}\n\t| payload: {CommandDataUtility.FormatBitsBytes(payloadBits)}\n\t| changeBits: {CommandDataUtility.FormatBitsBytes((int) (numCommandsToSend-1))}\n\t| flush: {CommandDataUtility.FormatBitsBytes(flush)}\n\t---\n\t{CommandDataUtility.FormatBitsBytes(totalCommandBits)}\n");
#endif
            }

            /// <summary>
            /// 首先假定前一个 Tick 是当前输入 Tick 减 1
            /// 常见情况下发送 2 bit，即差值为 -1、-2 或 -3，在预先减 1 后对应 0、1 或 2
            /// 差值为 -4 或更小时回退到 Huffman 编码
            /// </summary>
            /// <param name="assumedTickIndex"></param>
            /// <param name="writer"></param>
            /// <param name="inputData"></param>
            private void WriteTickDeltaCompressed(ref NetworkTick assumedTickIndex, ref DataStreamWriter writer, in TCommandData inputData)
            {
                const int outOfRange = 3;
                if (Hint.Likely(assumedTickIndex.IsValid && inputData.Tick.IsValid))
                {
                    // 常见情况是相差 1、2 或 3 个 Tick
                    // 因此分别用 0、1、2 表示这些差值，只在必要时回退到 Huffman 编码
                    var deltaTicks = assumedTickIndex.TicksSince(inputData.Tick);
                    if (Hint.Likely(deltaTicks >= 1 && deltaTicks < 3))
                    {
                        writer.WriteRawBits((uint) deltaTicks - 1, CommandSendSystemGroup.k_TickDeltaBits);
                    }
                    else
                    {
                        deltaTicks = outOfRange;
                        writer.WriteRawBits((uint) deltaTicks, CommandSendSystemGroup.k_TickDeltaBits);
                        // 从上一个值减去 4，因为差值不可能是 -1、-2 或 -3
                        if(assumedTickIndex.IsValid) assumedTickIndex.Subtract(4);
                        writer.WritePackedUIntDelta(inputData.Tick.SerializedData, assumedTickIndex.SerializedData, compressionModel);
                    }
                }
                else
                {
                    writer.WriteRawBits(outOfRange, CommandSendSystemGroup.k_TickDeltaBits);
                    writer.WritePackedUIntDelta(inputData.Tick.SerializedData, assumedTickIndex.SerializedData, compressionModel);
                }

                assumedTickIndex = inputData.Tick;
            }

            /// <summary>
            /// 如果 <see cref="prevInputData"/> 与 <see cref="targetTick"/> 对应的输入相同则返回 1
            /// 没有数据或 Tick 无效时返回 0
            /// </summary>
            private static uint GetDataAtTickAndCmp(NetworkTick targetTick, ref TCommandData prevInputData,
                DynamicBuffer<TCommandData> input, ref TCommandData inputData, TCommandDataSerializer serializer)
            {
                if (!targetTick.IsValid)
                    return 0;
                return input.GetDataAtTick(targetTick, out inputData)
                    ? serializer.CalculateChangeMask(in inputData, in prevInputData)
                    : 0u;
            }

            /// <summary>
            /// <para>查找当前 Tick 需要序列化命令的所有 Ghost Entity，
            /// 并将其命令加入 <see cref="OutgoingCommandDataStreamBuffer"/> 队列
            /// 以下 Entity 会被视为潜在 Ghost 目标：</para>
            /// <para>- <see cref="CommandTarget"/> 引用的 Entity</para>
            /// <para>- 由该玩家拥有且具有已启用 <see cref="AutoCommandTarget"/> 组件的全部 Ghost，参见 <see cref="GhostOwner"/></para>
            /// </summary>
            /// <param name="chunk">包含连接 Entity 的 Chunk</param>
            /// <param name="orderIndex">未使用，表示向 Entity Command Buffer 入队操作的排序索引</param>
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var commandTargets = chunk.GetNativeArray(ref commmandTargetType);
                var rpcDatas = chunk.GetBufferAccessor(ref outgoingCommandBufferType);
#if NETCODE_DEBUG
                var enablePacketLoggings = chunk.Has(ref enablePacketLoggingType) ? chunk.GetNativeArray(ref enablePacketLoggingType) : default;
#endif

                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    var targetEntity = commandTargets[i].targetEntity;
#if NETCODE_DEBUG
                    var enablePacketLogging = enablePacketLoggings.IsCreated ? enablePacketLoggings[i] : default;
#else
                    var enablePacketLogging = default(EnablePacketLogging);
#endif

                    bool sentTarget = false;
                    for (int ent = 0; ent < autoCommandTargetEntities.Length; ++ent)
                    {
                        var autoTarget = autoCommandTargetEntities[ent];
                        if (autoCommandTargetFromEntity[autoTarget].Enabled &&
                            inputFromEntity.HasBuffer(autoTarget))
                        {
                            Serialize(rpcDatas[i], autoTarget, true, ref enablePacketLogging);
                            sentTarget |= (autoTarget == targetEntity);
                        }
                    }
                    if (!sentTarget && inputFromEntity.HasBuffer(targetEntity))
                        Serialize(rpcDatas[i], targetEntity, false, ref enablePacketLogging);
                }
            }
        }

        /// <summary>
        /// 调度处理 Job 时使用的查询
        /// </summary>
        public EntityQuery Query => m_connectionQuery;
        private EntityQuery m_connectionQuery;
        private EntityQuery m_autoTargetQuery;
        private EntityQuery m_networkTimeQuery;
        private EntityQuery m_clientTickRateQuery;
        private StreamCompressionModel m_CompressionModel;
        private NetworkTick m_PrevInputTargetTick;

        private ComponentTypeHandle<CommandTarget> m_CommandTargetComponentHandle;
        private ComponentTypeHandle<EnablePacketLogging> m_EnablePacketLoggingTypeComponentHandle;
        private BufferTypeHandle<OutgoingCommandDataStreamBuffer> m_OutgoingCommandDataStreamBufferComponentHandle;
        private BufferLookup<TCommandData> m_TCommandDataFromEntity;
        private ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        private ComponentLookup<GhostOwner> m_GhostOwnerLookup;
        private ComponentLookup<AutoCommandTarget> m_AutoCommandTargetFromEntity;
        /// <summary>
        /// 初始化辅助结构体，应从 ISystem 的 OnCreate 调用
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkStreamInGame, CommandTarget>();
            m_connectionQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<GhostInstance, GhostOwner, GhostOwnerIsLocal, TCommandData, AutoCommandTarget>();
            m_autoTargetQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkTime>();
            m_networkTimeQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<ClientTickRate>();
            m_clientTickRateQuery = state.GetEntityQuery(builder);

            m_CompressionModel = StreamCompressionModel.Default;
            m_CommandTargetComponentHandle = state.GetComponentTypeHandle<CommandTarget>(true);
            m_EnablePacketLoggingTypeComponentHandle = state.GetComponentTypeHandle<EnablePacketLogging>(false);
            m_OutgoingCommandDataStreamBufferComponentHandle = state.GetBufferTypeHandle<OutgoingCommandDataStreamBuffer>();
            m_TCommandDataFromEntity = state.GetBufferLookup<TCommandData>(true);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_GhostOwnerLookup = state.GetComponentLookup<GhostOwner>(true);
            m_AutoCommandTargetFromEntity = state.GetComponentLookup<AutoCommandTarget>(true);

            state.RequireForUpdate(m_connectionQuery);
            state.RequireForUpdate(m_networkTimeQuery);
            state.RequireForUpdate<GhostCollection>();
        }

        /// <summary>
        /// 初始化处理 Job 的内部状态，应从 ISystem 的 OnUpdate 调用
        /// </summary>
        /// <param name="state">原始 Entity System 状态</param>
        /// <returns>已构造并完成状态初始化的 <see cref="SendJobData"/></returns>
        public SendJobData InitJobData(ref SystemState state)
        {
            m_CommandTargetComponentHandle.Update(ref state);
            m_EnablePacketLoggingTypeComponentHandle.Update(ref state);
            m_OutgoingCommandDataStreamBufferComponentHandle.Update(ref state);
            m_TCommandDataFromEntity.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);
            m_GhostOwnerLookup.Update(ref state);
            m_AutoCommandTargetFromEntity.Update(ref state);

            var clientNetTime = m_networkTimeQuery.GetSingleton<NetworkTime>();
            var inputTargetTick = clientNetTime.InputTargetTick;
            var targetEntities = m_autoTargetQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var autoHandle);

            // NumAdditionalCommandsToSend 非常重要，原因如下
            // TargetCommandSlack 尝试确保输入在服务器需要消费前 N 个 Tick 到达
            // 这样可以避免输入经常因到达过晚而被 DGS 的服务器权威模拟丢弃
            // 但客户端时间线与服务器时间线的偏差可能超过 16.67 ms，即 60 Hz 下的一个 Tick
            // 因此，如果每个数据包不额外包含一个 Tick，并且客户端与服务器发生不同步，这种情况非常常见，
            // 即使网络没有丢包，也会丢失完整的输入数据包
            if (!m_clientTickRateQuery.TryGetSingleton(out ClientTickRate clientTickRate))
                clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            var numCommandsToSend = Mathematics.math.clamp(clientTickRate.TargetCommandSlack + clientTickRate.NumAdditionalCommandsToSend, 1u, CommandSendSystemGroup.k_MaxInputBufferSendSize);

            var sendJob = new SendJobData
            {
                commmandTargetType = m_CommandTargetComponentHandle,
                enablePacketLoggingType = m_EnablePacketLoggingTypeComponentHandle,
                outgoingCommandBufferType = m_OutgoingCommandDataStreamBufferComponentHandle,
                inputFromEntity = m_TCommandDataFromEntity,
                ghostFromEntity = m_GhostComponentFromEntity,
                ghostOwnerFromEntity = m_GhostOwnerLookup,
                autoCommandTargetFromEntity = m_AutoCommandTargetFromEntity,
                compressionModel = m_CompressionModel,
                inputTargetTick = inputTargetTick,
                prevInputTargetTick = m_PrevInputTargetTick,
                autoCommandTargetEntities = targetEntities,
                stableHash = TypeManager.GetTypeInfo<TCommandData>().StableTypeHash,
                numCommandsToSend = numCommandsToSend,
            };
            m_PrevInputTargetTick = inputTargetTick;
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, autoHandle);
            return sendJob;
        }

        /// <summary>
        /// 检查处理 Job 是否需要运行的工具方法，用于在 ISystem 的 OnUpdate 中提前退出
        /// </summary>
        /// <param name="state">原始 Entity System 状态</param>
        /// <returns>处理 Job 是否需要运行</returns>
        public bool ShouldRunCommandJob(ref SystemState state)
        {
            // 存在自动命令目标 Entity 时始终运行 Job
            if (!m_autoTargetQuery.IsEmptyIgnoreFilter)
                return true;
            // 否则仅当 CommandTarget 存在且具有此组件类型时运行
            if (!m_connectionQuery.TryGetSingleton<CommandTarget>(out var commandTarget))
                return false;
            if (!state.EntityManager.HasComponent<TCommandData>(commandTarget.targetEntity))
                return false;

            return true;
        }
    }
}
