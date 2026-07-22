using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    /// <summary>
    ///     位于 <see cref="NetworkReceiveSystemGroup" /> 末尾的 <see cref="EntityCommandBufferSystem" />，
    ///     用于同步连接 Entity 的状态，例如创建与销毁
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial class NetworkGroupCommandBufferSystem : EntityCommandBufferSystem
    {
        private EntityQuery m_ConnectionQuery;
        private EntityQuery m_IncorrectlyDisposedConnectionsQuery;
        private EntityQuery m_RpcRequests;
        private EntityQuery m_PrespawnSubcenes;

        /// <summary>
        ///     调用 <see cref="SystemAPI.GetSingleton{T}" /> 获取此系统的该组件，
        ///     再对该 Singleton 调用 <see cref="CreateCommandBuffer" />，创建由此系统回放的 ECB
        /// </summary>
        /// <remarks>
        ///     适用于当前记录 Entity 命令，但希望在本帧稍后或下一帧早期回放的情况
        /// </remarks>
        public unsafe struct Singleton : IComponentData, IECBSingleton
        {
            internal UnsafeList<EntityCommandBuffer>* pendingBuffers;
            internal AllocatorManager.AllocatorHandle allocator;

            /// <summary>
            ///     创建由父系统回放的 Command Buffer
            /// </summary>
            /// <remarks>
            ///     此方法创建的 Command Buffer 会自动添加到系统的待处理 Buffer 列表
            /// </remarks>
            /// <param name="world">回放 Command Buffer 的 World</param>
            /// <returns>用于记录命令的 Command Buffer</returns>
            public EntityCommandBuffer CreateCommandBuffer(WorldUnmanaged world)
            {
                return EntityCommandBufferSystem.CreateCommandBuffer(ref *pendingBuffers, allocator, world);
            }

            /// <summary>
            ///     设置此系统更新时要回放的 Command Buffer 列表
            /// </summary>
            /// <remarks>
            ///     此方法仅供内部使用，但受语言限制必须作为公共 API 暴露
            ///     通过 <see cref="CreateCommandBuffer" /> 创建的 Command Buffer 会自动添加到系统的待回放 Buffer 列表
            /// </remarks>
            /// <param name="buffers">
            ///     要回放的 Buffer 列表，该列表会替换此系统现有的全部待处理 Command Buffer
            /// </param>
            public void SetPendingBufferList(ref UnsafeList<EntityCommandBuffer> buffers)
            {
                pendingBuffers = (UnsafeList<EntityCommandBuffer>*) UnsafeUtility.AddressOf(ref buffers);
            }

            /// <summary>
            ///     设置通过此 Singleton 创建 Command Buffer 时使用的 Allocator
            /// </summary>
            /// <param name="allocatorIn">要使用的 Allocator</param>
            public void SetAllocator(Allocator allocatorIn)
            {
                allocator = allocatorIn;
            }

            /// <summary>
            ///     设置通过此 Singleton 创建 Command Buffer 时使用的 Allocator
            /// </summary>
            /// <param name="allocatorIn">要使用的 Allocator</param>
            public void SetAllocator(AllocatorManager.AllocatorHandle allocatorIn)
            {
                allocator = allocatorIn;
            }
        }

        /// <inheritdoc cref="EntityCommandBufferSystem.OnCreate" />
        protected override void OnCreate()
        {
            base.OnCreate();
            this.RegisterSingleton<Singleton>(ref PendingBuffers, World.Unmanaged);

            m_IncorrectlyDisposedConnectionsQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>(), ComponentType.Exclude<IncomingRpcDataStreamBuffer>());
            m_ConnectionQuery = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            m_RpcRequests = GetEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            m_PrespawnSubcenes = GetEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostCleanup>());
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            PatchConnectionEvents(ref CheckedStateRef);
        }

        /// <summary>
        ///     修补本帧早些时候由 ECB 创建且现已存在的 Connection Event Entity
        /// </summary>
        /// <param name="state"></param>
        [BurstCompile]
        private void PatchConnectionEvents(ref SystemState state)
        {
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            var netDebug = SystemAPI.GetSingleton<NetDebug>();

            NativeArray<NetworkStreamConnection> connections = default;
            NativeArray<Entity> entities = default;
            var connectionEvents = networkStreamDriver.ConnectionEventsList;
            NativeList<NetCodeConnectionEvent> disconnected = new NativeList<NetCodeConnectionEvent>(connectionEvents.Length, Allocator.Temp);
            for (var i = 0; i < connectionEvents.Length; i++)
            {
                ref var connectionEvent = ref connectionEvents.ElementAt(i);

                if (connectionEvent.State == ConnectionState.State.Disconnected)
                    disconnected.Add(connectionEvent);

                if (connectionEvent.ConnectionEntity.Index >= 0)
                    continue;

                if (!connections.IsCreated)
                {
                    m_ConnectionQuery.CompleteDependency();
                    connections = m_ConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
                    entities = m_ConnectionQuery.ToEntityArray(Allocator.Temp);
                }

                if (!TrySetFromConnectionId(connections, entities, connectionEvent.ConnectionId, ref connectionEvent.ConnectionEntity))
                {
                    netDebug.LogError($"Unable to find Connection Entity after ECB Playback, for NetCodeConnectionEvent: {connectionEvent.ToFixedString()}! Forced to set to Entity.Null.");
                    connectionEvent.ConnectionEntity = Entity.Null;
                }

                static bool TrySetFromConnectionId(NativeArray<NetworkStreamConnection> ids, NativeArray<Entity> entities, NetworkConnection searchId, ref Entity toSet)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (ids[i].Value.ConnectionId == searchId.ConnectionId)
                        {
                            toSet = entities[i];
                            return true;
                        }
                    }

                    return false;
                }
            }
            CleanupStaleReceivedRpcs(ref state, disconnected, netDebug);
            StopStreamingPrespawnSubscenes(ref state, disconnected);

            // 应用这些事件
            // 结构变更后重新获取 NetworkStreamDriver
            networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            networkStreamDriver.ConnectionEventsForTick = connectionEvents.AsReadOnly();

            // 检测被错误释放的 NetworkConnection Entity，并妥善清理
            if(!m_IncorrectlyDisposedConnectionsQuery.IsEmpty)
            {
                var incorrectlyDisposedConnectionEntities = m_IncorrectlyDisposedConnectionsQuery.ToEntityArray(Allocator.Temp);
                var incorrectlyDisposedConnections = m_IncorrectlyDisposedConnectionsQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
                for (int i = 0; i < incorrectlyDisposedConnections.Length; i++)
                {
                    netDebug.LogError($"The entity for {incorrectlyDisposedConnections[i].Value.ToFixedString()} ({incorrectlyDisposedConnectionEntities[i].ToFixedString()}) has been incorrectly disposed in '{state.WorldUnmanaged.Name}'! You should never dispose the connection entity yourself! Instead, call Disconnect on the driver with it. Manually disconnecting it for you now.");
                    networkStreamDriver.DriverStore.Disconnect(incorrectlyDisposedConnections[i]);
                }
                state.EntityManager.RemoveComponent<NetworkStreamConnection>(m_IncorrectlyDisposedConnectionsQuery);
            }
        }

        /// <summary>
        /// 当指定 SubScene 加载完成并就绪时，客户端会请求服务器开始流式发送其中的 Prespawn Ghost
        /// 随后会启用附加在该 SubScene 上的 SubSceneWithGhostCleanup 标志并跟踪状态
        /// 客户端断开连接时需要关闭此标志，确保重新连接后再次向服务器发送 Prespawn 流式传输请求
        /// </summary>
        void StopStreamingPrespawnSubscenes(ref SystemState state, NativeList<NetCodeConnectionEvent> disconnected)
        {
            if (World.IsClient() && disconnected.Length > 0 && !m_PrespawnSubcenes.IsEmpty)
            {
                var prespawnSubscenes = m_PrespawnSubcenes.ToComponentDataArray<SubSceneWithGhostCleanup>(Allocator.Temp);
                var prespawnSubscenesEntities = m_PrespawnSubcenes.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < prespawnSubscenes.Length; ++i)
                {
                    var subSceneWithGhostCleanup = prespawnSubscenes[i];
                    subSceneWithGhostCleanup.Streaming = 0;
                    state.EntityManager.SetComponentData(prespawnSubscenesEntities[i], subSceneWithGhostCleanup);
                }
            }
        }

        /// <summary>
        /// 修复 RPC 已到达但 Network Connection 已关闭的问题
        /// 系统会清理这些过期 RPC，使用户代码无需在所有 RPC 处理逻辑中防御此情况
        /// </summary>
        private void CleanupStaleReceivedRpcs(ref SystemState state, NativeList<NetCodeConnectionEvent> disconnectionEvents, in NetDebug netDebug)
        {
            if (disconnectionEvents.Length > 0 && !m_RpcRequests.IsEmpty)
            {
                var rpcRequests = m_RpcRequests.ToComponentDataArray<ReceiveRpcCommandRequest>(Allocator.Temp);
                var rpcEntities = m_RpcRequests.ToEntityArray(Allocator.Temp);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var debugString = new FixedString512Bytes();
#endif
                for (int i = 0; i < rpcRequests.Length; ++i)
                {
                    for (int j = 0; j < disconnectionEvents.Length; ++j)
                    {
                        if (disconnectionEvents[j].ConnectionEntity == rpcRequests[i].SourceConnection)
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (debugString.Length == 0)
                                debugString += "Removing RPCs with destroyed connection:\n";

                            EntityManager.GetName(rpcEntities[i], out var rpcName);
                            if (rpcName.IsEmpty)
                                rpcName = "EMPTY_NAME";
                            debugString.Append($"'{rpcName} {disconnectionEvents[j].Id.ToFixedString()} {disconnectionEvents[j].ConnectionEntity.ToFixedString()}'\n");
#endif

                            state.EntityManager.DestroyEntity(rpcEntities[i]);
                        }
                    }
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (debugString.Length > 0)
                    netDebug.DebugLog(debugString);
#endif
            }
        }
    }
}
