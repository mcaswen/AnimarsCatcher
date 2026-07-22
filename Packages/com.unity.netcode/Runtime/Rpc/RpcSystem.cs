using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Networking.Transport.Error;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于简化接收 RPC Command 的反序列化与执行系统和 Job 编写工作的结构体
    /// </summary>
    public struct RpcExecutor
    {
        /// <summary>
        /// 用作 RPC 执行方法参数的结构体，参见 <see cref="ExecuteDelegate"/> 委托
        /// 包含输入 Data Stream、接收连接以及可用于解码和编写 RPC 逻辑的其他数据
        /// </summary>
        public struct Parameters
        {
            /// <summary>
            /// 包含 RPC 数据的 Data Stream
            /// </summary>
            public DataStreamReader Reader;
            /// <summary>
            /// 收到 RPC 的连接
            /// </summary>
            public Entity Connection;
            /// <summary>
            /// 在客户端上表示存储客户端连接 UniqueId 的 Singleton Entity
            /// 值为 Entity.Null 时表示尚未创建该 Entity，此时应进行创建
            /// </summary>
            internal Entity ClientConnectionUniqueIdEntity;
            /// <summary>
            /// 在客户端上表示当前客户端到服务器连接的唯一 ID
            /// 尚未设置时为 0
            /// </summary>
            internal uint ClientCurrentConnectionUniqueId;
            /// <summary>
            /// 上述 <see cref="Connection"/> 的缓存 Component 状态，会自动写回
            /// </summary>
            internal NetworkStreamConnection ConnectionStateRef;
            /// <summary>
            /// 可用于执行结构性变更的 Command Buffer
            /// </summary>
            public EntityCommandBuffer.ParallelWriter CommandBuffer;
            /// <summary>
            /// 向 Command Buffer 添加 Command 时必须使用的排序顺序
            /// </summary>
            public int JobIndex;
            /// <summary>
            /// 指向 <see cref="RpcDeserializerState"/> 实例的指针
            /// </summary>
            internal IntPtr State;
            /// <summary>
            /// 日志记录器
            /// </summary>
            public NetDebug NetDebug;
            /// <summary>
            /// 此 Component 值的缓存
            /// </summary>
            public NetworkProtocolVersion ProtocolVersion;
            /// <summary>
            /// 此 World 名称的缓存
            /// </summary>
            public FixedString128Bytes WorldName;
            /// <summary>
            /// 此 World 使用 <see cref="RpcCollection.DynamicAssemblyList"/> 时为 true
            /// </summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool UseDynamicAssemblyList;
            /// <summary>
            /// 当前是否在服务器 World 中执行
            /// </summary>
            [MarshalAs(UnmanagedType.U1)]
            public bool IsServer;

            /// <summary>
            /// 此 RPC 是否为绕过序列化的 Loopback RPC
            /// 出于性能考虑，此时 RPC 执行代码不应进行序列化，只需从 <see cref="GetPassthroughActionData"/> 读取数据
            /// </summary>
            // TODO-release：为此使用场景补充新的文档条目和示例
            [MarshalAs(UnmanagedType.U1)]
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            public bool IsPassthroughRPC;
#else
            internal bool IsPassthroughRPC;
#endif

            /// <summary>
            /// 指向直通 Action Data 的指针，用于绕过序列化流程的 Single World Host
            /// </summary>
            internal IntPtr actionDataOverridePtr;

            /// <summary>
            /// 可用于反序列化 RPC 的 <see cref="RpcDeserializerState"/> 实例
            /// </summary>
            public RpcDeserializerState DeserializerState
            {
                get { unsafe { return UnsafeUtility.AsRef<RpcDeserializerState>((void*)State); } }
            }

            // TODO-release：改用更合适的名称
            /// <summary>
            /// 在 Single World Host 场景中，RPC 数据无需反序列化，可直接从此处获取，从而绕过序列化与反序列化逻辑
            /// </summary>
            /// <typeparam name="TActionData">RPC Component 类型</typeparam>
            /// <returns></returns>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            public unsafe TActionData GetPassthroughActionData<TActionData>() where TActionData : unmanaged, IComponentData
#else
            internal unsafe TActionData GetPassthroughActionData<TActionData>() where TActionData : unmanaged, IComponentData
#endif
            {
                return UnsafeUtility.AsRef<TActionData>((void*)actionDataOverridePtr);
            }
        }

        /// <summary>
        /// <para>收到 RPC 时调用的 Burst 兼容静态方法引用
        /// 例如
        /// </para>
        /// <code>
        ///     [BurstCompile(DisableDirectCall = true)]
        ///     [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
        ///     private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
        /// </code>
        /// </summary>
        /// <remarks>
        /// 为规避 Burst 与函数委托的问题，必须设置 <c>DisableDirectCall = true</c>
        /// 实现自定义 RPC Serializer 时，请记得禁用直接调用
        /// </remarks>
        /// <param name="parameters">自定义 RPC Serializer 的参数</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ExecuteDelegate(ref Parameters parameters);

        /// <summary>
        /// <para>用于实现 <see cref="IRpcCommandSerializer{T}"/> 接口执行方法的辅助方法
        /// 调用 ExecuteCreateRequestComponent 会创建一个带有 <typeparamref name="TActionRequest"/>
        /// 和 <see cref="ReceiveRpcCommandRequest"/> Component 的新 Entity
        /// 用户需要自行编写系统消费所创建的 RPC Entity，例如
        /// </para>
        /// <code>
        /// public struct MyRpcConsumeSystem : ISystem
        /// {
        ///    private Query rcpQuery;
        ///    public void OnCreate(ref SystemState state)
        ///    {
        ///        var builder = new EntityQueryBuilder(Allocator.Temp).WithAll&lt;MyRpc, ReceiveRpcCommandRequestComponent&gt;();
        ///        rcpQuery = state.GetEntityQuery(builder);
        ///    }
        ///    public void OnUpdate(ref SystemState state)
        ///    {
        ///         foreach(var rpc in SystemAPI.Query&lt;MyRpc&gt;().WithAll&lt;ReceiveRpcCommandRequestComponent&gt;())
        ///         {
        ///             // 使用 RPC 执行业务逻辑
        ///         }
        ///         // 消费全部 RPC
        ///         state.EntityManager.DestroyEntity(rpcQuery);
        ///    }
        /// }
        /// </code>
        /// </summary>
        /// <param name="parameters">包含 <see cref="EntityCommandBuffer"/>、JobIndex 和 Connection Entity 的容器</param>
        /// <typeparam name="TActionSerializer"><see cref="IRpcCommandSerializer{TActionRequest}"/> 类型的结构体</typeparam>
        /// <typeparam name="TActionRequest"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
        /// <returns>为 RPC 请求创建的 Entity，其名称设为 'NetCodeRPC'</returns>
        public static Entity ExecuteCreateRequestComponent<TActionSerializer, TActionRequest>(ref Parameters parameters)
            where TActionRequest : unmanaged, IComponentData
            where TActionSerializer : struct, IRpcCommandSerializer<TActionRequest>
        {
            var rpcData = default(TActionRequest);

            if (parameters.IsPassthroughRPC)
            {
                rpcData = parameters.GetPassthroughActionData<TActionRequest>();
            }
            else
            {
                var rpcSerializer = default(TActionSerializer);
                rpcSerializer.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);
            }

            var entity = parameters.CommandBuffer.CreateEntity(parameters.JobIndex);
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, new ReceiveRpcCommandRequest {SourceConnection = parameters.Connection});
            parameters.CommandBuffer.AddComponent(parameters.JobIndex, entity, rpcData);

#if !DOTS_DISABLE_DEBUG_NAMES
            FixedString64Bytes truncatedName = new FixedString64Bytes();
            truncatedName.CopyFromTruncated((FixedString512Bytes)$"NetcodeRPC_{ComponentType.ReadWrite<TActionRequest>().ToFixedString()}");
            parameters.CommandBuffer.SetName(parameters.JobIndex, entity, truncatedName);
#endif
            return entity;
        }
    }

    /// <summary>
    /// <para>
    /// 负责发送和接收 RPC 的系统
    /// </para>
    /// <para>
    /// RpcSystem 会为所有活动连接 Flush <see cref="OutgoingRpcDataStreamBuffer"/> 中已调度的全部出站 RPC
    /// 一个 World 可以在单帧内为每条连接触发多个 RPC
    /// 为减少在途可靠消息数量，系统会尝试将多个 RPC 合并到单个数据包中
    /// </para>
    /// <para>
    /// 数据包队列大小有限，参见 <see cref="NetworkParameterConstants.SendQueueCapacity"/> 和 <see cref="NetworkConfigParameter"/>，
    /// 因此可用数据包数量可能不足以完全 Flush 队列
    /// 此时待处理消息会在下一帧或资源可用时继续尝试发送
    /// </para>
    /// <para>
    /// 收到 RPC 数据包后，首先由 <see cref="NetworkStreamReceiveSystem"/> 处理
    /// 该系统解码传入网络数据包，并将其追加到接收消息连接的 <see cref="IncomingRpcDataStreamBuffer"/>
    /// 随后 RpcSystem 会让全部已收消息出队，并通过调用其执行方法进行分发，
    /// 参见 <see cref="IRpcCommandSerializer{T}"/> 和 <see cref="RpcExecutor"/>
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    [BurstCompile]
    public partial struct RpcSystem : ISystem
    {
        /// <summary>
        /// 初始 Handshake 期间，客户端和服务器通过内部 RPC 交换各自的 <see cref="NetworkProtocolVersion"/>
        /// 收到后，RpcSystem 会进行协议检查，验证版本是否兼容
        /// 验证失败时会创建带 <see cref="ProtocolVersionError"/> Component 的新 Entity，
        /// 随后由 <see cref="RpcSystemErrors"/> 系统处理生成的错误
        /// </summary>
        internal struct ProtocolVersionError : IComponentData
        {
            public Entity connection;
            public NetworkProtocolVersion remoteProtocol;
        }

        private NativeList<RpcCollection.RpcData> m_RpcData;
        private NativeParallelHashMap<ulong, int> m_RpcTypeHashToIndex;
        private NativeReference<byte> m_DynamicAssemblyList;

        private EntityQuery m_RpcBufferGroup;

        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<NetworkStreamConnection> m_NetworkStreamConnectionHandle;
        private BufferTypeHandle<IncomingRpcDataStreamBuffer> m_IncomingRpcDataStreamBufferComponentHandle;
        private BufferTypeHandle<OutgoingRpcDataStreamBuffer> m_OutgoingRpcDataStreamBufferComponentHandle;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<OutgoingRpcDataStreamBuffer>() == 1);
            UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<IncomingRpcDataStreamBuffer>() == 1);
#endif

            m_RpcData = new NativeList<RpcCollection.RpcData>(16, Allocator.Persistent);
            m_RpcTypeHashToIndex = new NativeParallelHashMap<ulong, int>(16, Allocator.Persistent);
            m_DynamicAssemblyList = new NativeReference<byte>(Allocator.Persistent);
            var rpcSingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<RpcCollection>());
            state.EntityManager.SetName(rpcSingleton, "RpcCollection-Singleton");
            state.EntityManager.SetComponentData(rpcSingleton, new RpcCollection
            {
                m_DynamicAssemblyList = m_DynamicAssemblyList,
                m_RpcData = m_RpcData,
                m_RpcTypeHashToIndex = m_RpcTypeHashToIndex,
                m_IsFinal = 0
            });

            m_RpcBufferGroup = state.GetEntityQuery(
                ComponentType.ReadWrite<IncomingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<OutgoingRpcDataStreamBuffer>(),
                ComponentType.ReadWrite<NetworkStreamConnection>() // Single World Host 存在没有 NetworkStreamConnection 的连接，TODO-release：处理已断开客户端
                );
            state.RequireForUpdate(m_RpcBufferGroup);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_NetworkStreamConnectionHandle = state.GetComponentTypeHandle<NetworkStreamConnection>();
            m_IncomingRpcDataStreamBufferComponentHandle = state.GetBufferTypeHandle<IncomingRpcDataStreamBuffer>();
            m_OutgoingRpcDataStreamBufferComponentHandle = state.GetBufferTypeHandle<OutgoingRpcDataStreamBuffer>();

            var rpcCollection = SystemAPI.GetSingleton<RpcCollection>();
            rpcCollection.RegisterRpc<RequestProtocolVersionHandshake>();
            rpcCollection.RegisterRpc<ServerRequestApprovalAfterHandshake>();
            rpcCollection.RegisterRpc<ServerApprovedConnection>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_RpcData.Dispose();
            m_RpcTypeHashToIndex.Dispose();
            m_DynamicAssemblyList.Dispose();
        }

        /// <summary>
        /// 调用 RPC 执行方法，默认包含来自 <see cref="RpcExecutor.ExecuteCreateRequestComponent"/> 的反序列化逻辑
        /// </summary>
        [BurstCompile]
        struct RpcExecJob : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<NetworkStreamConnection> connectionType;
            public BufferTypeHandle<IncomingRpcDataStreamBuffer> inBufferType;
            public BufferTypeHandle<OutgoingRpcDataStreamBuffer> outBufferType;
            public Entity connectionUniqueIdEntity;
            public uint connectionUniqueId;
            [ReadOnly] public NativeList<RpcCollection.RpcData> execute;
            [ReadOnly] public NativeParallelHashMap<ulong, int> hashToIndex; // TODO：int 范围大于 ushort
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly ghostMap;

            public uint localTime;

            public ConcurrentDriverStore concurrentDriverStore;
            public NetworkProtocolVersion jobProtocolVersion;
            public byte dynamicAssemblyList;
            public FixedString128Bytes worldName;
            public NetDebug netDebug;
            public byte isServer;

            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持带 Enableable Component 类型的查询
                Assert.IsFalse(useEnabledMask);

                var entities = chunk.GetNativeArray(entityType);
                var rpcInBuffer = chunk.GetBufferAccessor(ref inBufferType);
                var rpcOutBuffer = chunk.GetBufferAccessor(ref outBufferType);
                var connections = chunk.GetNativeArray(ref connectionType);
                var deserializeState = new RpcDeserializerState
                {
                    ghostMap = ghostMap,
                    CompressionModel = StreamCompressionModel.Default, // TODO：未来支持自定义时接入
                };
                for (int i = 0; i < rpcInBuffer.Length; ++i)
                {
                    var connectionEntity = entities[i];
                    var conn = connections[i];
                    var concurrentDriver = concurrentDriverStore.GetConcurrentDriver(conn.DriverId);
                    ref var driver = ref concurrentDriver.driver;
                    var conState = concurrentDriver.driver.GetConnectionState(conn.Value);

                    // 当前处于断开状态时，检查传入缓冲区是否包含协议版本 RPC，以便处理并在版本不匹配时报告断开原因
                    if (conState == NetworkConnection.State.Disconnected && rpcInBuffer[i].Length > 0)
                    {
                        ushort rpcIndex = 0;
                        if (dynamicAssemblyList == 1)
                        {
                            var rpcHashPeek = *(ulong*) rpcInBuffer[i].GetUnsafeReadOnlyPtr();
                            if (hashToIndex.TryGetValue(rpcHashPeek, out var rpcIndexInt))
                                rpcIndex = (ushort) rpcIndexInt;
                            else rpcIndex = ushort.MaxValue;
                        }
                        else
                        {
                            rpcIndex = *(ushort*) rpcInBuffer[i].GetUnsafeReadOnlyPtr();
                        }

                        if (rpcIndex < execute.Length && execute[rpcIndex].IsApprovalType == 1)
                            netDebug.DebugLog($"[{worldName}] {conn.Value.ToFixedString()} in disconnected state but allowing {execute[rpcIndex].ToFixedString()} to get processed, as is approval RPC!");
                        else
                            continue;
                    }
                    else if (conState != NetworkConnection.State.Connected)
                    {
                        // Transport 层尚未连接，因此需要等到连接后再处理出站和入站 RPC
                        // 此时不会丢弃 RPC，只会继续保留
                        continue;
                    }

                    var dynArray = rpcInBuffer[i];
                    var parameters = new RpcExecutor.Parameters
                    {
                        Reader = dynArray.AsDataStreamReader(),
                        CommandBuffer = commandBuffer,
                        State = (IntPtr)UnsafeUtility.AddressOf(ref deserializeState),
                        Connection = connectionEntity,
                        ClientConnectionUniqueIdEntity = connectionUniqueIdEntity,
                        ClientCurrentConnectionUniqueId = connectionUniqueId,
                        JobIndex = unfilteredChunkIndex,
                        ConnectionStateRef = conn,
                        NetDebug = netDebug,
                        ProtocolVersion = jobProtocolVersion,
                        UseDynamicAssemblyList = dynamicAssemblyList != 0,
                        WorldName = worldName,
                        IsServer = isServer == 1
                    };
                    int msgHeaderLen = RpcCollection.GetInnerRpcMessageHeaderLength(dynamicAssemblyList == 1);
                    while (parameters.Reader.GetBytesRead() < parameters.Reader.Length)
                    {
                        int rpcIndex;
                        if (dynamicAssemblyList == 1)
                        {
                            ulong rpcHash = parameters.Reader.ReadULong();
                            if (!hashToIndex.TryGetValue(rpcHash, out rpcIndex))
                            {
                                netDebug.LogError(
                                    $"[{worldName}] RpcSystem received rpc with invalid hash ({rpcHash}) from {conn.Value.ToFixedString()}");
                                commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                    new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                                break;
                            }
                        }
                        else
                        {
                            rpcIndex = parameters.Reader.ReadUShort();
                        }

                        var rpcSizeBits = parameters.Reader.ReadUShort();
                        var rpcSizeBytes = (rpcSizeBits + 7) >> 3;

                        // 连接审批阶段不允许常规 RPC
                        // 在客户端上，ProtocolVersion 和 NetworkID RPC 可以通过，
                        // 因为它们由服务器在审批完成后的下一阶段 Handshake 中发送
                        if (conn.IsHandshakeOrApproval)
                        {
                            if (execute[rpcIndex].IsApprovalType == 0)
                            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                netDebug.LogError($"[{worldName}] RpcSystem received non-approval RPC {execute[rpcIndex].ToFixedString()} while in the {conn.CurrentState.ToFixedString()} connection state, from {conn.Value.ToFixedString()}. Make sure you only send non-approval RPCs once the connection is approved. Disconnecting.");
#endif
                                commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                    new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                                break;
                            }
                        }

                        var rpcBitStart = parameters.Reader.GetBitsRead();
                        if (Hint.Unlikely(rpcIndex >= execute.Length))
                        {
                            netDebug.LogError($"[{worldName}] RpcSystem received invalid rpc (index {rpcIndex} out of range) from {conn.Value.ToFixedString()}!");
                            commandBuffer.AddComponent(unfilteredChunkIndex, connectionEntity,
                                new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                            break;
                        }

                        execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                        // TODO：可在此增加防御性检查，判断 execute[rpcIndex].Execute.Ptr.Invoke 是否遇到致命错误并提前退出

                        // 验证 rpcSizeBits 是否与反序列化读取量一致
                        var rpcBitsRead = parameters.Reader.GetBitsRead() - rpcBitStart;
                        if (parameters.Reader.HasFailedReads || rpcSizeBits != rpcBitsRead)
                        {
                            var rpcBytesRead = (rpcBitsRead + 7) >> 3;
                            netDebug.LogError($"[{worldName}] RpcSystem failed to deserialize RPC '{execute[rpcIndex].ToFixedString()}', as bits read ({rpcBitsRead} [{rpcBytesRead}B] did not match expected ({rpcSizeBits} [{rpcSizeBytes}B])! Be aware that the incorrectly deserialized RPC may have still executed, but this connection will soon be closed.");
                            commandBuffer.AddComponent(unfilteredChunkIndex, entities[i], new NetworkStreamRequestDisconnect {Reason = NetworkStreamDisconnectReason.InvalidRpc});
                            break;
                        }

                        parameters.Reader.Flush(); // 每个打包 RPC 都按字节对齐，因此必须填充所有未使用的位

                        // 写回 ConnectionStateRef
                        conn = parameters.ConnectionStateRef;
                        connections[i] = parameters.ConnectionStateRef;
                    }

                    dynArray.Clear();

                    var sendBuffer = rpcOutBuffer[i];
                    while (sendBuffer.Length > 0)
                    {
                        // Writer 返回的缓冲区大小由 Transport 定义
                        // 最大 RPC 大小并非由 NetCode 决定
                        int result;
                        if ((result = driver.BeginSend(concurrentDriver.reliablePipeline, conn.Value, out var rpcPacketWriter)) < 0)
                        {
                            if(result == (int)StatusCode.NetworkSendQueueFull)
                                netDebug.DebugLog($"[{worldName}] RpcSystem BeginSend encountered StatusCode.NetworkSendQueueFull (-5), which is an expected StatusCode when sending many reliable RPCs within a short duration (the NetworkConfigParameter.sendQueue is full). Will re-attempt on future ticks, until all have succeeded.\nhttps://docs.unity3d.com/Packages/com.unity.transport@2.2/manual/faq.html#what-does-error-networksendqueuefull-mean");
                            else netDebug.LogWarning($"[{worldName}] RPCSystem failed to BeginSend message with StatusCode: {result}. Retrying next tick!");
                            break;
                        }

                        rpcPacketWriter.WriteByte((byte) NetworkStreamProtocol.Rpc);
                        rpcPacketWriter.WriteUInt(localTime);
                        var headerLengthBytes = rpcPacketWriter.Length;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(headerLengthBytes == RpcCollection.k_RpcCommonHeaderLengthBytes);
#endif

                        // sendBuffer 中排队的 RPC 过多时，尽可能多地发送
                        if (sendBuffer.Length + headerLengthBytes > rpcPacketWriter.Capacity)
                        {
                            var sendArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(sendBuffer.GetUnsafePtr(), sendBuffer.Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            var safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(sendBuffer.AsNativeArray());
                            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref sendArray, safety);
#endif
                            var reader = new DataStreamReader(sendArray);
                            ushort rpcIndex;
                            ulong rpcHash;
                            if (dynamicAssemblyList == 1)
                            {
                                rpcHash = reader.ReadULong();
                                if (hashToIndex.TryGetValue(rpcHash, out var rpcIndexInt))
                                    rpcIndex = (ushort) rpcIndexInt;
                                else throw new InvalidOperationException($"[{worldName}][RpcSystem] Attempting to send RPC with hash '{rpcHash}' that is unknown to our own collection!");
                            }
                            else
                            {
                                rpcHash = 0;
                                rpcIndex = reader.ReadUShort();
                            }

                            var payloadLengthBits = reader.ReadUShort();
                            var payloadLengthBytes = ((payloadLengthBits + 7) >> 3);
                            var rpcLengthBytes = payloadLengthBytes + msgHeaderLen;
                            var totalLengthBytes = rpcLengthBytes + headerLengthBytes;
                            if (totalLengthBytes > rpcPacketWriter.Capacity)
                            {
                                sendBuffer.Clear();
                                driver.AbortSend(rpcPacketWriter);
                                // 数据包连一条消息都无法容纳，这是严重错误
                                var rpcName = rpcIndex < execute.Length ? execute[rpcIndex].ToFixedString() : $"Rpc[{rpcHash}, ??, index: {rpcIndex}]";
                                throw new InvalidOperationException($"[{worldName}][RpcSystem] RPC '{rpcName}' was too big to be sent! It was {totalLengthBytes} bytes [netcode header: {headerLengthBytes}B, rpc message header: {msgHeaderLen}B, payload: {payloadLengthBits} bits], but UTP only offered a packet buffer of {rpcPacketWriter.Capacity}B! Reduce the size of this RPC payload!");
                            }

                            rpcPacketWriter.WriteBytesUnsafe((byte*) sendBuffer.GetUnsafePtr(), rpcLengthBytes);

                            // 继续尝试在此数据包中容纳尽可能多的消息
                            while (true)
                            {
                                var curTmpDataLength = rpcPacketWriter.Length - headerLengthBytes;
                                var subArray = sendArray.GetSubArray(curTmpDataLength, sendArray.Length - curTmpDataLength);
                                reader = new DataStreamReader(subArray);
                                if (dynamicAssemblyList == 1)
                                    reader.ReadULong();
                                else
                                    reader.ReadUShort();
                                var innerPayloadLengthBits = reader.ReadUShort();
                                var innerPayloadLengthBytes = ((innerPayloadLengthBits+7) >> 3);
                                var innerRpcLengthBytes = innerPayloadLengthBytes + msgHeaderLen;
                                if (rpcPacketWriter.Length + innerRpcLengthBytes > rpcPacketWriter.Capacity)
                                    break;
                                rpcPacketWriter.WriteBytesUnsafe((byte*) subArray.GetUnsafeReadOnlyPtr(), innerRpcLengthBytes);
                            }
                        }
                        else
                            rpcPacketWriter.WriteBytesUnsafe((byte*) sendBuffer.GetUnsafePtr(), sendBuffer.Length);

                        // 发送失败时停止处理并等待下一帧
                        if ((result = driver.EndSend(rpcPacketWriter)) <= 0)
                        {
                            if (result == (int) StatusCode.NetworkSendQueueFull)
                                netDebug.DebugLog($"[{worldName}] RpcSystem EndSend encountered StatusCode.NetworkSendQueueFull (-5), which is an expected StatusCode when sending many reliable RPCs within a short duration (hitting the outbound ReliableUtility.Parameters.WindowSize capacity). Will re-attempt on future ticks, until all have succeeded.\nhttps://docs.unity3d.com/Packages/com.unity.transport@2.2/manual/faq.html#what-does-error-networksendqueuefull-mean");
                            else netDebug.LogWarning($"[{worldName}] An error occured during RpcSystem EndSend with StatusCode: {result}, UTP Buffer Capacity: {rpcPacketWriter.Capacity}. Retrying next tick!");
                            break;
                        }

                        var tmpDataLength = rpcPacketWriter.Length - headerLengthBytes;
                        if (tmpDataLength < sendBuffer.Length)
                        {
                            // 压缩缓冲区，移除已经发送的 RPC
                            for (int cpy = tmpDataLength; cpy < sendBuffer.Length; ++cpy)
                                sendBuffer[cpy - tmpDataLength] = sendBuffer[cpy];
                            sendBuffer.ResizeUninitialized(sendBuffer.Length - tmpDataLength);
                        }
                        else
                            sendBuffer.Clear();
                    }
                }
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // 从 Reader Stream 反序列化 Command 类型
            // 执行 RPC
            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            SystemAPI.TryGetSingleton(out NetworkProtocolVersion protocolVersion);
            var connectionUniqueIdEntity = Entity.Null;
            ConnectionUniqueId connectionUniqueId = default;
            if (!state.WorldUnmanaged.IsServer())
            {
                SystemAPI.TryGetSingletonEntity<ConnectionUniqueId>(out connectionUniqueIdEntity);
                SystemAPI.TryGetSingleton(out connectionUniqueId);
            }

            m_EntityTypeHandle.Update(ref state);
            m_NetworkStreamConnectionHandle.Update(ref state);
            m_IncomingRpcDataStreamBufferComponentHandle.Update(ref state);
            m_OutgoingRpcDataStreamBufferComponentHandle.Update(ref state);
            var execJob = new RpcExecJob
            {
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                entityType = m_EntityTypeHandle,
                connectionType = m_NetworkStreamConnectionHandle,
                inBufferType = m_IncomingRpcDataStreamBufferComponentHandle,
                outBufferType = m_OutgoingRpcDataStreamBufferComponentHandle,
                connectionUniqueIdEntity = connectionUniqueIdEntity,
                connectionUniqueId = connectionUniqueId.Value,
                execute = m_RpcData,
                hashToIndex = m_RpcTypeHashToIndex,
                ghostMap = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().Value,
                localTime = NetworkTimeSystem.TimestampMS,
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                jobProtocolVersion = protocolVersion,
                dynamicAssemblyList = m_DynamicAssemblyList.Value,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
                worldName = state.WorldUnmanaged.Name,
                isServer = state.WorldUnmanaged.IsServer() ? (byte)1 : (byte)0
            };
            state.Dependency = execJob.ScheduleParallel(m_RpcBufferGroup, state.Dependency);
            state.Dependency = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(state.Dependency);
        }
    }

    /// <summary>
    /// <para>负责处理 <see cref="RpcSystem"/> 接收 RPC 时创建的全部
    /// <see cref="RpcSystem.ProtocolVersionError"/> 的系统
    /// </para>
    /// <para>
    /// 系统会向产生 <see cref="RpcSystem.ProtocolVersionError"/> 的连接添加
    /// <see cref="NetworkStreamRequestDisconnect"/> Component 以断开连接，
    /// 并向应用程序报告包含以下内容的详细错误消息
    /// </para>
    /// <para> - 本地协议</para>
    /// <para> - 远端协议</para>
    /// <para> - 全部已注册 RPC 的列表</para>
    /// <para> - 全部已注册 Serializer 的列表</para>
    /// </summary>
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    public partial struct RpcSystemErrors : ISystem
    {
        private EntityQuery m_ProtocolErrorQuery;
        private ComponentLookup<NetworkStreamConnection> m_NetworkStreamConnectionFromEntity;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            m_ProtocolErrorQuery = state.GetEntityQuery(ComponentType.ReadOnly<RpcSystem.ProtocolVersionError>());
            state.RequireForUpdate(m_ProtocolErrorQuery);
            state.RequireForUpdate<GhostCollection>();

            m_NetworkStreamConnectionFromEntity = state.GetComponentLookup<NetworkStreamConnection>(true);
        }

        [BurstCompile]
        partial struct ReportRpcErrors : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            [ReadOnly] public ComponentLookup<NetworkStreamConnection> connections;
            public NativeArray<FixedString128Bytes> rpcs;
            public NativeArray<FixedString128Bytes> componentInfo;
            public NetDebug netDebug;
            public NetworkProtocolVersion localProtocol;
            public FixedString128Bytes worldName;
            public void Execute(Entity entity, in RpcSystem.ProtocolVersionError rpcError)
            {
                FixedString128Bytes connection = "unknown connection";
                if (rpcError.connection != Entity.Null)
                {
                    commandBuffer.AddComponent(rpcError.connection,
                        new NetworkStreamRequestDisconnect
                            { Reason = NetworkStreamDisconnectReason.InvalidRpc });
                    connection = connections[rpcError.connection].Value.ToFixedString();
                }

                var errorHeader = (FixedString512Bytes)$"[{worldName}] RpcSystem received bad protocol version from {connection}";
                errorHeader.Append((FixedString32Bytes)"\nLocal protocol: ");
                errorHeader.Append(localProtocol.ToFixedString());
                errorHeader.Append((FixedString32Bytes)"\nRemote protocol: ");
                errorHeader.Append(rpcError.remoteProtocol.ToFixedString());
                errorHeader.Append((FixedString512Bytes)"\nSee the following errors for more information.");
                netDebug.LogError(errorHeader);

                if (localProtocol.NetCodeVersion != rpcError.remoteProtocol.NetCodeVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The NetCode version mismatched between remote and local. Ensure that you are using the same version of Netcode for Entities on both client and server.");
                }

                if (localProtocol.GameVersion != rpcError.remoteProtocol.GameVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
                }

                if (localProtocol.RpcCollectionVersion != rpcError.remoteProtocol.RpcCollectionVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The RPC Collection mismatched between remote and local. Compare the following list of RPCs against the set produced by the remote, to find which RPCs are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                }

                if (localProtocol.ComponentCollectionVersion != rpcError.remoteProtocol.ComponentCollectionVersion)
                {
                    netDebug.LogError((FixedString512Bytes)"The Component Collection mismatched between remote and local. Compare the following list of Components against the set produced by the remote, to find which components are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                }


                var s = (FixedString512Bytes)"RPC List (for above 'bad protocol version' error): ";
                s.Append(rpcs.Length);
                netDebug.LogError(s);

                for (int i = 0; i < rpcs.Length; ++i)
                    netDebug.LogError($"RpcHash[{i}] = {rpcs[i]}");

                s = (FixedString512Bytes)"Component serializer data (for above 'bad protocol version' error): ";
                s.Append(componentInfo.Length);
                netDebug.LogError(s);

                for (int i = 0; i < componentInfo.Length; ++i)
                    netDebug.LogError($"ComponentHash[{i}] = {componentInfo[i]}");

                commandBuffer.DestroyEntity(entity);
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_NetworkStreamConnectionFromEntity.Update(ref state);

            var collectionRpcs = SystemAPI.GetSingleton<RpcCollection>().Rpcs;
            var rpcs = CollectionHelper.CreateNativeArray<FixedString128Bytes>(collectionRpcs.Length, state.WorldUpdateAllocator);
            for (int i = 0; i < collectionRpcs.Length; ++i)
            {
                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(collectionRpcs[i].TypeHash);
                rpcs[i] = new FixedString128Bytes(TypeManager.GetTypeInfo(typeIndex).DebugTypeName);
            }
            FixedString128Bytes serializerHashString = default;
            var ghostSerializerCollection = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
            var componentInfo = CollectionHelper.CreateNativeArray<FixedString128Bytes>(ghostSerializerCollection.Length, state.WorldUpdateAllocator);
            for (int serializerIndex = 0; serializerIndex < ghostSerializerCollection.Length; ++serializerIndex)
            {
                GhostCollectionSystem.GetSerializerHashString(ghostSerializerCollection[serializerIndex],
                    ref serializerHashString);
                componentInfo[serializerIndex] = serializerHashString;
                serializerHashString.Clear();
            }

            var reportJob = new ReportRpcErrors
            {
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged),
                connections = m_NetworkStreamConnectionFromEntity,
                rpcs = rpcs,
                componentInfo = componentInfo,
                netDebug = SystemAPI.GetSingleton<NetDebug>(),
                localProtocol = SystemAPI.GetSingleton<NetworkProtocolVersion>(),
                worldName = state.WorldUnmanaged.Name
            };

            state.Dependency = reportJob.Schedule(state.Dependency);
        }
    }
}
