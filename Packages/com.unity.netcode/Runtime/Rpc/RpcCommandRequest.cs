#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于升级到新 Component 类型的临时类型，应在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("SendRpcCommandRequestComponent has been deprecated. Use SendRpcCommandRequest instead (UnityUpgradable) -> SendRpcCommandRequest", true)]
    public struct SendRpcCommandRequestComponent : IComponentData
    {}
    /// <summary>
    /// 用于升级到新 Component 类型的临时类型，应在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("ReceiveRpcCommandRequestComponent has been deprecated. Use ReceiveRpcCommandRequest instead (UnityUpgradable) -> ReceiveRpcCommandRequest", true)]
    public struct ReceiveRpcCommandRequestComponent : IComponentData
    {}

    /// <summary>
    /// 表示 RPC 应发送到远端连接且不应在本地处理的 Component
    /// </summary>
    public struct SendRpcCommandRequest : IComponentData
    {
        /// <summary>
        /// 此 RPC 要定向发送到的 NetworkConnection Entity，设为 Entity.Null 时广播到全部连接
        /// </summary>
        public Entity TargetConnection;
    }
    /// <summary>
    /// 表示已从远端连接收到 RPC 且应进行处理的 Component
    /// </summary>
    public struct ReceiveRpcCommandRequest : IComponentData
    {
        /// <summary>
        /// 发送当前待处理 RPC 的连接
        /// </summary>
        public Entity SourceConnection;

#if NETCODE_DEBUG
        /// <inheritdoc cref="Consume"/>
        public ushort Age;

#endif
        /// <inheritdoc cref="Consume"/>
        public bool IsConsumed
        {
            get
            {
#if NETCODE_DEBUG
                return Age == ushort.MaxValue;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// <see cref="ReceiveRpcCommandRequest"/> 由 <see cref="WarnAboutStaleRpcSystem"/> 监控，
        /// 当此 <see cref="Age"/> 值超过 <see cref="NetDebug.MaxRpcAgeFrames"/> 时记录警告
        /// 该值以模拟帧计数，收到 RPC 的模拟帧记为 0
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Consume()
        {
#if NETCODE_DEBUG
            Age = ushort.MaxValue;
#endif
        }
    }

    /// <summary>
    /// 确保对 Command Request Entity 的全部处理都在正确位置执行的 Group
    /// 此 Group 供代码生成使用，仅在实现自定义 Command Request Processor 时才应直接使用
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(RpcSystem))]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class RpcCommandRequestSystemGroup : ComponentSystemGroup
    {
        EntityQuery m_Query;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<SendRpcCommandRequest>());
        }
        protected override void OnUpdate()
        {
            if (!m_Query.IsEmptyIgnoreFilter)
                base.OnUpdate();
        }
    }

    /// <summary>
    /// 用于实现 RPC Command Request Entity 处理系统的辅助结构体
    /// 通常由代码生成使用，仅在特殊情况下才应直接使用
    /// </summary>
    /// <typeparam name="TActionSerializer"><see cref="IRpcCommandSerializer{TActionRequest}"/> 的 Unmanaged 类型</typeparam>
    /// <typeparam name="TActionRequest"><see cref="IComponentData"/> 的 Unmanaged 类型</typeparam>
    public struct RpcCommandRequest<TActionSerializer, TActionRequest>
        where TActionRequest : unmanaged, IComponentData
        where TActionSerializer : unmanaged, IRpcCommandSerializer<TActionRequest>
    {
        /// <summary>
        /// <para>可嵌入 System Job 中并用于委托 RPC 处理的结构体
        /// 使用示例</para>
        /// <code>
        /// [BurstCompile]
        /// struct SendRpc : IJobChunk
        /// {
        ///     public RpcCommandRequest{MyRpcCommand, MyRpcCommand}.SendRpcData data;
        ///     public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        ///     {
        ///         data.Execute(chunk, unfilteredChunkIndex);
        ///     }
        /// }
        /// </code>
        /// <para>始终使用 <see cref="RpcCommandRequest{TActionSerializer,TActionRequest}.InitJobData"/> 方法构建有效实例</para>
        /// </summary>
        public struct SendRpcData
        {
            internal EntityCommandBuffer.ParallelWriter commandBuffer; // 模拟开始阶段使用
            [ReadOnly] internal EntityTypeHandle entitiesType;
            [ReadOnly] internal ComponentTypeHandle<SendRpcCommandRequest> rpcRequestType;
            [ReadOnly] internal ComponentTypeHandle<TActionRequest> actionRequestType;
            [ReadOnly] internal ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] internal ComponentLookup<NetworkId> networkIdLookup;
            [ReadOnly] internal ComponentLookup<NetworkStreamConnection> networkStreamConnectionLookup;
            [ReadOnly] internal ComponentLookup<LocalConnection> localConnectionLookup;
            [ReadOnly] internal NativeList<RpcCollection.RpcData> execute;
            [ReadOnly] internal NativeParallelHashMap<ulong, int> hashToIndex;
            internal BufferLookup<OutgoingRpcDataStreamBuffer> rpcFromEntity;
            internal RpcQueue<TActionSerializer, TActionRequest> rpcQueue;
            [ReadOnly] internal NativeList<Entity> connections;
            internal NetDebug netDebug;
            internal byte requireConnectionApproval;
            internal byte isApprovalRpc;
            internal byte isServer;
            internal byte isHost;
            internal FixedString128Bytes worldName;
            internal NativeArray<NetCodeConnectionEvent>.ReadOnly connectionEventsForTick;

            // 处理全部发送请求
            void LambdaMethod(Entity entity, int orderIndex, in SendRpcCommandRequest dest, in TActionRequest action)
            {
                commandBuffer.DestroyEntity(orderIndex, entity);
                if (dest.TargetConnection != Entity.Null)
                {
                    ValidateIncorrectApprovalUsage(dest.TargetConnection, false);
                    ValidateAndQueueRpc(dest.TargetConnection, false, action, orderIndex);
                }
                else
                {
                    if (connections.Length == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var msg = isServer != 0
                            ? $"[{worldName}] Cannot broadcast RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' as no remote connections. I.e. No `NetworkStreamConnection` entities found, as no clients connected to this server."
                            : $"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to the server as not connected to one! I.e. No `NetworkStreamConnection` entity, as this client world is not connected (nor connecting) to any server.";
                        if (!AnyDisconnectEvents(connectionEventsForTick))
                            netDebug.LogWarning(msg);
                        else netDebug.DebugLog(msg);
                        static bool AnyDisconnectEvents(NativeArray<NetCodeConnectionEvent>.ReadOnly eventsForTickLocal)
                        {
                            foreach (var evt in eventsForTickLocal)
                                if (evt.State == ConnectionState.State.Disconnected)
                                    return true;
                            return false;
                        }
#endif
                        return;
                    }

                    ValidateIncorrectApprovalUsage(connections[0], isServer != 0);
                    for (var i = 0; i < connections.Length; ++i)
                    {
                        ValidateAndQueueRpc(connections[i], isServer != 0, action, orderIndex);
                    }
                }
            }

            private void ValidateAndQueueRpc(Entity connectionEntity, bool isBroadcast, TActionRequest action, int orderIndex)
            {
                // action 参数需要按值传递，以降低下方通过指针复制的不安全操作所带来的风险

                // TODO-release：引入新审批流程后回头处理此处，并修复上方没有连接时的流程
                // TODO-release MTT-13314：处理用户直接调用 Schedule 并绕过 RPC Entity 更新的情况，Single World Host 也应支持
                if (isHost == 1 && localConnectionLookup.HasComponent(connectionEntity))
                {
                    // Single World Host 直通：如果带 RPC Buffer 的 Entity 是 Host 本地连接，
                    // 则立即在此创建 Entity，效果等同于服务器已收到该 RPC
                    unsafe
                    {
                        RpcExecutor.Parameters parameters = new RpcExecutor.Parameters()
                        {
                            CommandBuffer = commandBuffer,
                            Connection = connectionEntity,
                            JobIndex = orderIndex,
                            actionDataOverridePtr = (IntPtr)UnsafeUtility.AddressOf(ref action),
                            IsPassthroughRPC = true,
                            NetDebug = netDebug,
                            WorldName = worldName,
                            IsServer = isServer == 1
                        };

                        var rpcHash = TypeManager.GetTypeInfo<TActionRequest>().StableTypeHash;
                        hashToIndex.TryGetValue(rpcHash, out var rpcIndex);
                        // 如果用户实现了自定义 RPC 序列化或执行逻辑，也必须进行调用
                        // 这会触发常规流程，因此也能处理远端触发相应回调的情况
                        // ExecuteCreateRequestComponent 应处理是否序列化的边界情况，并创建相应 Action Component
                        execute[rpcIndex].Execute.Ptr.Invoke(ref parameters);
                        return;
                    }
                }

                // TODO：如果 Cleanup Component 已移除或不允许结构性变更，
                // 应通过检查 Entity 是否存在，在 TargetConnection 被赋予错误 Entity 时报告错误
                if (!networkStreamConnectionLookup.TryGetComponent(connectionEntity, out var networkStreamConnection)
                    || !rpcFromEntity.TryGetBuffer(connectionEntity, out var buffer))
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (isBroadcast || FindDidJustDisconnect(connectionEntity))
                        netDebug.DebugLog($"{Prefix(true, connectionEntity)} as they just disconnected.");
                    else
                        netDebug.LogWarning($"{Prefix(false, connectionEntity)} as its connection entity ({connectionEntity.ToFixedString()}) does not have a `NetworkStreamConnection` or `OutgoingRpcDataStreamBuffer` component (anymore?). Did you assign the correct entity?");
#endif
                    return;
                }

                var isHandshakeOrApproval = networkStreamConnection.IsHandshakeOrApproval;
                if (isHandshakeOrApproval)
                {
                    if (isApprovalRpc == 0)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        FixedString512Bytes msg = $"{Prefix(isBroadcast, connectionEntity)} as it is not an Approval RPC, and its {networkStreamConnection.Value.ToFixedString()} - on {connectionEntity.ToFixedString()} - is in state `{networkStreamConnection.CurrentState.ToFixedString()}`!";
                        if (isBroadcast)
                            netDebug.DebugLog(msg);
                        else
                        {
                            msg.Append((FixedString128Bytes)" You MUST wait for Handshake and Approval to complete, OR convert this RPC to an `IApprovalRpcCommand`!");
                            netDebug.LogError(msg);
                        }
#endif
                        return;
                    }
                }
                else
                {
                    var isConnected = networkStreamConnection.CurrentState == ConnectionState.State.Connected && networkIdLookup.HasComponent(connectionEntity);
                    if (!isConnected)
                    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        FixedString512Bytes msg = $"{Prefix(isBroadcast, connectionEntity)} as its {networkStreamConnection.Value.ToFixedString()} - on {connectionEntity.ToFixedString()} - is in state `{networkStreamConnection.CurrentState.ToFixedString()}`!";
                        if (isBroadcast)
                            netDebug.DebugLog(msg);
                        else netDebug.LogError(msg);
#endif
                        return;
                    }
                }

                rpcQueue.Schedule(buffer, ghostFromEntity, action);
            }

            private bool FindDidJustDisconnect(Entity entity)
            {
                foreach (var evt  in connectionEventsForTick)
                {
                    if (evt.State == ConnectionState.State.Disconnected && evt.ConnectionEntity == entity)
                        return true;
                }
                return false;
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void ValidateIncorrectApprovalUsage(Entity connectionEntity, bool isBroadcast)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (requireConnectionApproval == 0 && isApprovalRpc == 1 && !netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning)
                {
                    FixedString512Bytes msg = isBroadcast
                        ? $"[{worldName}] Broadcasting approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' but connection approval is disabled. We will still attempt to broadcast the RPC."
                        : $"[{worldName}] Sending approval RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {Target(connectionEntity)} but connection approval is disabled. We will still attempt to send the RPC.";
                    msg.Append((FixedString128Bytes)" If intentional, suppress via `NetDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning`.");
                    netDebug.LogWarning(msg);
                }
#endif
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            FixedString512Bytes Prefix(bool isBroadcast, Entity connectionEntity)
            {
                return isBroadcast
                    ? $"[{worldName}] Broadcast of RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' will skip client connection {connectionEntity.ToFixedString()}"
                    : $"[{worldName}] Cannot send RPC '{ComponentType.ReadOnly<TActionRequest>().GetDebugTypeName()}' to {Target(connectionEntity)}";
            }

            private FixedString128Bytes Target(Entity connectionEntity) => isServer == 0 ? $"the server" : $"TargetConnection:{connectionEntity.ToFixedString()}";
#endif

            /// <summary>
            /// 从 <see cref="IJobChunk.Execute"/> 方法调用此方法以处理 RPC 请求
            /// </summary>
            /// <param name="chunk">当前 Chunk</param>
            /// <param name="orderIndex">顺序索引</param>
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var entities = chunk.GetNativeArray(entitiesType);
                var rpcRequests = chunk.GetNativeArray(ref rpcRequestType);
                if (ComponentType.ReadOnly<TActionRequest>().IsZeroSized)
                {
                    TActionRequest action = default;
                    for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], action);
                    }
                }
                else
                {
                    var actions = chunk.GetNativeArray(ref actionRequestType);
                    for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                    {
                        LambdaMethod(entities[i], orderIndex, rpcRequests[i], actions[i]);
                    }
                }
            }
        }

        private RpcQueue<TActionSerializer, TActionRequest> m_RpcQueue;
        private EntityQuery m_ConnectionsQuery;
        private EntityQuery m_CommandBufferQuery;
        private EntityQuery m_NetDebugQuery;
        private EntityQuery m_NetworkStreamDriver;
        /// <summary>
        /// 调度处理 Job 时使用的查询
        /// </summary>
        public EntityQuery Query;

        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<SendRpcCommandRequest> m_SendRpcCommandRequestComponentHandle;
        ComponentTypeHandle<TActionRequest> m_TActionRequestHandle;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdLookup;
        ComponentLookup<NetworkStreamConnection> m_NetworkStreamConnectionLookup;
        ComponentLookup<LocalConnection> m_LocalConnectionLookup;
        EntityQuery m_RpcCollectionQuery;
        BufferLookup<OutgoingRpcDataStreamBuffer> m_OutgoingRpcDataStreamBufferComponentFromEntity;
        bool m_IsApprovalRpc;

        /// <summary>
        /// 初始化辅助结构体，应从 ISystem 的 OnCreate 中调用
        /// </summary>
        /// <param name="state"><see cref="SystemState"/></param>
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<RpcCollection>();
            m_RpcCollectionQuery = state.GetEntityQuery(builder);
            var rpcCollection = m_RpcCollectionQuery.GetSingleton<RpcCollection>();
            rpcCollection.RegisterRpc<TActionSerializer, TActionRequest>();
            m_RpcQueue = rpcCollection.GetRpcQueue<TActionSerializer, TActionRequest>();
            builder.Reset();
            builder.WithAll<SendRpcCommandRequest, TActionRequest>();
            Query = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<OutgoingRpcDataStreamBuffer>();
            m_ConnectionsQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<BeginSimulationEntityCommandBufferSystem.Singleton>();
            builder.WithOptions(EntityQueryOptions.IncludeSystems);
            m_CommandBufferQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetDebug>();
            m_NetDebugQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkStreamDriver>();
            m_NetworkStreamDriver = state.GetEntityQuery(builder);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_SendRpcCommandRequestComponentHandle = state.GetComponentTypeHandle<SendRpcCommandRequest>(true);
            m_TActionRequestHandle = state.GetComponentTypeHandle<TActionRequest>(true);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_NetworkIdLookup = state.GetComponentLookup<NetworkId>(true);
            m_NetworkStreamConnectionLookup = state.GetComponentLookup<NetworkStreamConnection>(true);
            m_LocalConnectionLookup = state.GetComponentLookup<LocalConnection>(true);
            m_OutgoingRpcDataStreamBufferComponentFromEntity = state.GetBufferLookup<OutgoingRpcDataStreamBuffer>();

            var componentsManagedType = ComponentType.ReadWrite<TActionRequest>().GetManagedType();
            if (RpcCollection.IsApprovalRpcType(componentsManagedType))
                m_IsApprovalRpc = true;

            state.RequireForUpdate(Query);
        }

        /// <summary>
        /// 初始化处理 Job 的内部状态，应从 ISystem 的 OnUpdate 中调用
        /// </summary>
        /// <param name="state">原始 Entity System 状态</param>
        /// <returns>使用 <paramref name="state"/> 初始化的 <see cref="SendRpcData"/></returns>
        public SendRpcData InitJobData(ref SystemState state)
        {
            var connections = m_ConnectionsQuery.ToEntityListAsync(state.WorldUpdateAllocator,
                out var connectionsHandle);
            m_EntityTypeHandle.Update(ref state);
            m_SendRpcCommandRequestComponentHandle.Update(ref state);
            m_TActionRequestHandle.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);
            m_NetworkIdLookup.Update(ref state);
            m_NetworkStreamConnectionLookup.Update(ref state);
            m_LocalConnectionLookup.Update(ref state);
            m_OutgoingRpcDataStreamBufferComponentFromEntity.Update(ref state);
            var nsd = m_NetworkStreamDriver.GetSingleton<NetworkStreamDriver>();
            var rpcCollection = m_RpcCollectionQuery.GetSingleton<RpcCollection>();
            var sendJob = new SendRpcData
            {
                commandBuffer = m_CommandBufferQuery.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                entitiesType = m_EntityTypeHandle,
                rpcRequestType = m_SendRpcCommandRequestComponentHandle,
                actionRequestType = m_TActionRequestHandle,
                ghostFromEntity = m_GhostComponentFromEntity,
                rpcFromEntity = m_OutgoingRpcDataStreamBufferComponentFromEntity,
                networkIdLookup = m_NetworkIdLookup,
                networkStreamConnectionLookup = m_NetworkStreamConnectionLookup,
                localConnectionLookup = m_LocalConnectionLookup,
                execute = rpcCollection.m_RpcData,
                hashToIndex = rpcCollection.m_RpcTypeHashToIndex,
                rpcQueue = m_RpcQueue,
                connections = connections,
                connectionEventsForTick = nsd.ConnectionEventsForTick,
                netDebug = m_NetDebugQuery.GetSingleton<NetDebug>(),
                requireConnectionApproval = nsd.RequireConnectionApproval ? (byte)1 : (byte)0,
                isApprovalRpc = m_IsApprovalRpc ? (byte)1 : (byte)0,
                isServer = state.WorldUnmanaged.IsServer() ? (byte)1 : (byte)0,
                isHost = state.WorldUnmanaged.IsHost() ? (byte)1 : (byte)0,
                worldName = state.WorldUnmanaged.Name,
            };
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, connectionsHandle);
            return sendJob;
        }
    }
}
