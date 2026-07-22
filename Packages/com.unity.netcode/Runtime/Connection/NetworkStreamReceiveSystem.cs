#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    /// <summary>
    /// 所有从服务器接收数据、处理连接，以及需要在 Ghost Simulation Group 前执行操作的系统父 Group
    /// <see cref="CommandSendSystemGroup"/> 和 <see cref="NetworkStreamReceiveSystem"/> 会在此 Group 中更新
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation,
        WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    public partial class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    internal struct MigratedNetworkIdsData : IComponentData
    {
        public NativeHashMap<uint, int> MigratedNetworkIds;
    }

    internal struct NetworkIDAllocationData : IComponentData
    {
        public NativeReference<int> NumNetworkIds;
        public NativeQueue<int> FreeNetworkIds;
    }

    /// <summary>
    /// 用于创建和注册新 <see cref="NetworkDriver"/> 实例的工厂接口，需要由具体类实现
    /// </summary>
    public interface INetworkStreamDriverConstructor
    {
        /// <summary>
        /// 向 Driver Store 注册适合客户端使用的新 <see cref="NetworkDriver"/> 实例
        /// </summary>
        /// <param name="world">客户端 World</param>
        /// <param name="driver">Driver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        void CreateClientDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
        /// <summary>
        /// 向 Driver Store 注册适合服务器使用的新 <see cref="NetworkDriver"/> 实例
        /// </summary>
        /// <param name="world">服务器 World</param>
        /// <param name="driver">Driver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        void CreateServerDriver(World world, ref NetworkDriverStore driver, NetDebug netDebug);
    }

    /// <summary>
    /// 处理 NetworkStreamRequestConnect 组件的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [BurstCompile]
    public partial struct NetworkStreamConnectSystem : ISystem
    {
        EntityQuery m_ConnectionRequestConnectQuery;
        ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestConnectQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestConnect>());
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            networkStreamDriver.ConnectionEventsList.Clear();

            if (m_ConnectionRequestConnectQuery.IsEmpty) return;
            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;

            var requests = m_ConnectionRequestConnectQuery.ToComponentDataArray<NetworkStreamRequestConnect>(Allocator.Temp);
            var requetEntity = m_ConnectionRequestConnectQuery.ToEntityArray(Allocator.Temp);
            systemState.EntityManager.RemoveComponent<NetworkStreamRequestConnect>(m_ConnectionRequestConnectQuery);
            if (requests.Length > 1)
            {
                // 存在多个请求，受 Chunk 顺序限制，无法可靠判断最后入队的是哪一个
                // 除非增加 Timestamp 等信息，这需要用户添加或由框架提供正式 API
                // 后续可以支持，目前只处理第一个请求并丢弃其他请求
                netDebug.LogError($"Found {requests.Length} pending connection requests. It is required that only one NetworkStreamRequestConnect is queued at any time. Only the connect request to {requests[0].Endpoint.ToFixedString()} will be handled.");

                for (int i = 1; i < requests.Length; ++i)
                {
                    if (stateFromEntity.HasComponent(requetEntity[i]))
                    {
                        var state = stateFromEntity[requetEntity[i]];
                        state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                        state.CurrentState = ConnectionState.State.Disconnected;
                        stateFromEntity[requetEntity[i]] = state;
                    }
                    systemState.EntityManager.DestroyEntity(requetEntity[i]);
                }
            }
            // TODO 正确处理连接请求与已经连接的情况
            // 可能需要释放 Driver，并处理 NetworkStreamReceiveSystem 的相关问题
            var connection = networkStreamDriver.Connect(systemState.EntityManager, requests[0].Endpoint, requetEntity[0]);
            if(connection == Entity.Null)
            {
                netDebug.LogError($"Connect request for {requests[0].Endpoint.ToFixedString()} failed.");
                if (stateFromEntity.HasComponent(requetEntity[0]))
                {
                    var state = stateFromEntity[requetEntity[0]];
                    state.DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                    state.CurrentState = ConnectionState.State.Disconnected;
                    stateFromEntity[requetEntity[0]] = state;
                }
                systemState.EntityManager.DestroyEntity(requetEntity[0]);
            }
        }
    }
    /// <summary>
    /// 处理 NetworkStreamRequestListen 组件的系统
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(NetworkStreamReceiveSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamListenSystem : ISystem
    {
        EntityQuery m_ConnectionRequestListenQuery;
        ComponentLookup<NetworkStreamRequestListenResult> m_ConnectionStateFromEntity;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            m_ConnectionRequestListenQuery = state.GetEntityQuery(ComponentType.ReadWrite<NetworkStreamRequestListen>());
            m_ConnectionStateFromEntity = state.GetComponentLookup<NetworkStreamRequestListenResult>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<NetDebug>();
        }

        /// <inheritdoc/>
        public void OnUpdate(ref SystemState systemState)
        {
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            networkStreamDriver.ConnectionEventsList.Clear();

            if (m_ConnectionRequestListenQuery.IsEmpty) return;

            m_ConnectionStateFromEntity.Update(ref systemState);
            var stateFromEntity = m_ConnectionStateFromEntity;
            var requestCount = m_ConnectionRequestListenQuery.CalculateEntityCount();
            var requestListens = m_ConnectionRequestListenQuery.ToComponentDataArray<NetworkStreamRequestListen>(Allocator.Temp);
            var requestEntity = m_ConnectionRequestListenQuery.ToEntityArray(Allocator.Temp);
            var endpoint = requestListens[0].Endpoint;
            var requestEnt = requestEntity[0];
            if (requestListens.Length > 1)
            {
                // 存在多个请求，受 Chunk 顺序限制，无法可靠判断最后入队的是哪一个
                // 除非增加 Timestamp 等信息，这需要用户添加或由框架提供正式 API
                // 可以在 1.1 中实现更完善的方案，目前只处理第一个请求并丢弃其他请求
                netDebug.LogError($"Found {requestCount} pending listen requests. Only one NetworkStreamRequestListen can be queued at any time. Only the request to listen at {requestListens[0].Endpoint.ToFixedString()} will be handled.");
                for (int i = 1; i < requestEntity.Length; ++i)
                {
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.RefusedMultipleRequests
                        };
                    }
                }
            }

            var anyInterfaceListening = false;
            for (int i = networkStreamDriver.DriverStore.FirstDriver; i < networkStreamDriver.DriverStore.LastDriver; ++i)
            {
                anyInterfaceListening |= networkStreamDriver.DriverStore.GetDriverInstanceRO(i).driver.Listening;
            }

            // TODO 可以支持此情况，但需要额外处理并释放 Driver
            // 此操作发生在 NetworkStreamReceiveSystem 之前，部分逻辑也可能无法工作
            if (anyInterfaceListening)
            {
                netDebug.LogError($"Listen request for address {endpoint.ToFixedString()} refused. Driver is already listening");
                if (stateFromEntity.HasComponent(requestEnt))
                {
                    stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                    {
                        Endpoint = requestListens[0].Endpoint,
                        RequestState = NetworkStreamRequestListenResult.State.RefusedAlreadyListening
                    };
                }
            }
            else
            {
                if (networkStreamDriver.Listen(endpoint))
                {
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.Succeeded
                        };
                    }
                }
                else
                {
                    netDebug.LogError($"Listen request for address {endpoint.ToFixedString()} failed.");
                    if (stateFromEntity.HasComponent(requestEnt))
                    {
                        stateFromEntity[requestEnt] = new NetworkStreamRequestListenResult
                        {
                            Endpoint = requestListens[0].Endpoint,
                            RequestState = NetworkStreamRequestListenResult.State.Failed
                        };
                    }
                }
            }
            // 消费全部请求
            systemState.EntityManager.DestroyEntity(m_ConnectionRequestListenQuery);
        }
    }

    /// <summary>
    /// <para>NetworkStreamReceiveSystem 是 NetCode 包最重要的系统之一
    /// 其核心职责是管理全部 <see cref="NetworkStreamConnection"/> 生命周期，包括创建、更新和销毁，
    /// 并接收全部 <see cref="NetworkStreamProtocol"/> 消息类型
    /// 它还负责：</para>
    /// <para>- 创建 <see cref="NetworkStreamDriver"/> Singleton，另请参阅 <see cref="NetworkDriverStore"/> 和 <see cref="NetworkDriver"/></para>
    /// <para>- 处理 Driver 迁移，参见 <see cref="DriverMigrationSystem"/> 和 <see cref="MigrationTicket"/></para>
    /// <para>- 监听并接受入站连接，仅服务器</para>
    /// <para>- 在初始 Handshake 期间交换 <see cref="NetworkProtocolVersion"/></para>
    /// <para>- 更新存在的 <see cref="ConnectionState"/> 状态组件</para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [CreateAfter(typeof(RpcSystem))]
    [BurstCompile]
    public unsafe partial struct NetworkStreamReceiveSystem : ISystem
    {
        static INetworkStreamDriverConstructor s_DriverConstructor;
        static readonly ProfilerMarker k_Scheduling = new ProfilerMarker("NetworkStreamReceiveSystem_Scheduling");

        /// <summary>
        /// 分配自定义 <see cref="INetworkStreamDriverConstructor"/> 以定制 <see cref="NetworkDriver"/> 构造过程
        /// </summary>
        public static INetworkStreamDriverConstructor DriverConstructor
        {
            get { return s_DriverConstructor ??= DefaultDriverBuilder.DefaultDriverConstructor; }
            set => s_DriverConstructor = value;
        }

        internal enum DriverState
        {
            Default,
            Migrating
        }

        ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<NetworkStreamDriver.Pointers>((void*)m_DriverPointers).DriverStore;
        NativeReference<uint> m_RandomIndex;
        NativeReference<int> m_NumNetworkIds;
        NativeQueue<int> m_FreeNetworkIds;
        RpcQueue<ServerApprovedConnection, ServerApprovedConnection> m_ServerApprovedConnectionRpcQueue;
        RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> m_RequestProtocolVersionHandshakeRpcQueue;
        RpcQueue<ServerRequestApprovalAfterHandshake,ServerRequestApprovalAfterHandshake> m_ServerRequestApprovalRpcQueue;
        NativeList<uint> m_ConnectionUniqueIds;

        EntityQuery m_RefreshTickRateQuery;

        IntPtr m_DriverPointers;
        ComponentLookup<ConnectionState> m_ConnectionStateFromEntity;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
        ComponentLookup<ConnectionUniqueId> m_ConnectionUniqueIdFromEntity;
        ComponentLookup<ConnectionApproved> m_ApprovedFromEntity;
        ComponentLookup<NetworkStreamRequestDisconnect> m_RequestDisconnectFromEntity;
        ComponentLookup<NetworkStreamInGame> m_InGameFromEntity;
        ComponentLookup<EnablePacketLogging> m_EnablePacketLoggingFromEntity;
        BufferLookup<OutgoingRpcDataStreamBuffer> m_OutgoingRpcBufferFromEntity;
        BufferLookup<IncomingRpcDataStreamBuffer> m_RpcBufferFromEntity;
        BufferLookup<IncomingCommandDataStreamBuffer> m_CmdBufferFromEntity;
        BufferLookup<IncomingSnapshotDataStreamBuffer> m_SnapshotBufferFromEntity;
        NativeList<NetCodeConnectionEvent> m_ConnectionEvents;
        private NetworkPipelineStageId m_reliableSequencedPipelineStageId;

        NativeHashMap<uint, int> m_MigrationIds;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            DriverMigrationSystem driverMigrationSystem = default;
            foreach (var world in World.All)
            {
                if ((driverMigrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            m_RandomIndex = new NativeReference<uint>(Allocator.Persistent);
            m_RandomIndex.Value = (uint)System.Diagnostics.Stopwatch.GetTimestamp();
            m_NumNetworkIds = new NativeReference<int>(Allocator.Persistent);
            m_FreeNetworkIds = new NativeQueue<int>(Allocator.Persistent);
            m_ConnectionEvents = new NativeList<NetCodeConnectionEvent>(32, Allocator.Persistent);
            m_ConnectionUniqueIds = new NativeList<uint>(16, Allocator.Persistent);

            var rpcCollection = SystemAPI.GetSingleton<RpcCollection>();
            m_ServerApprovedConnectionRpcQueue = rpcCollection.GetRpcQueue<ServerApprovedConnection>();
            m_RequestProtocolVersionHandshakeRpcQueue = rpcCollection.GetRpcQueue<RequestProtocolVersionHandshake>();
            m_ServerRequestApprovalRpcQueue = rpcCollection.GetRpcQueue<ServerRequestApprovalAfterHandshake>();
            m_ConnectionStateFromEntity = state.GetComponentLookup<ConnectionState>(false);
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            m_ConnectionUniqueIdFromEntity = state.GetComponentLookup<ConnectionUniqueId>(true);
            m_ApprovedFromEntity = state.GetComponentLookup<ConnectionApproved>(true);
            m_RequestDisconnectFromEntity = state.GetComponentLookup<NetworkStreamRequestDisconnect>();
            m_InGameFromEntity = state.GetComponentLookup<NetworkStreamInGame>();
            m_EnablePacketLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>();

            m_OutgoingRpcBufferFromEntity = state.GetBufferLookup<OutgoingRpcDataStreamBuffer>();
            m_RpcBufferFromEntity = state.GetBufferLookup<IncomingRpcDataStreamBuffer>();
            m_CmdBufferFromEntity = state.GetBufferLookup<IncomingCommandDataStreamBuffer>();
            m_SnapshotBufferFromEntity = state.GetBufferLookup<IncomingSnapshotDataStreamBuffer>();
            m_reliableSequencedPipelineStageId = NetworkPipelineStageId.Get<ReliableSequencedPipelineStage>();

            AttemptCreateFakeHostConnection(ref state);

            NetworkEndpoint lastEp = default;
            NetworkDriverStore driverStore = default;
            if (SystemAPI.HasSingleton<MigrationTicket>())
            {
                 var ticket = SystemAPI.GetSingleton<MigrationTicket>();
                 // 加载 Driver 和全部网络连接数据
                 var driverState = driverMigrationSystem.Load(ticket.Value);
                 driverStore = driverState.DriverStore;
                 lastEp = driverState.LastEp;
                 m_NumNetworkIds.Value = driverState.NextId;
                 foreach (var id in driverState.FreeList)
                     m_FreeNetworkIds.Enqueue(id);
                 driverState.FreeList.Dispose();
            }
            else
            {
                driverStore = new NetworkDriverStore();
                if (state.World.IsServer())
                    DriverConstructor.CreateServerDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
                else
                    DriverConstructor.CreateClientDriver(state.World, ref driverStore, SystemAPI.GetSingleton<NetDebug>());
            }

            m_DriverPointers = (IntPtr)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NetworkStreamDriver.Pointers>(), UnsafeUtility.AlignOf<NetworkStreamDriver.Pointers>(), Allocator.Persistent);
            UnsafeUtility.MemClear((void*)m_DriverPointers, UnsafeUtility.SizeOf<NetworkStreamDriver.Pointers>());
            var networkStreamEntity = state.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamDriver>());
            state.EntityManager.SetName(networkStreamEntity, "NetworkStreamDriver");
            SystemAPI.SetSingleton(new NetworkStreamDriver((void*)m_DriverPointers, m_NumNetworkIds, m_FreeNetworkIds, lastEp, m_ConnectionEvents, m_ConnectionEvents.AsReadOnly()));
            SystemAPI.GetSingleton<NetworkStreamDriver>().ResetDriverStore(state.WorldUnmanaged, ref driverStore);

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkTime>();
            state.RequireForUpdate<NetDebug>();

            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<ClientServerTickRateRefreshRequest>();
            m_RefreshTickRateQuery = state.GetEntityQuery(builder);

            m_MigrationIds = new NativeHashMap<uint, int>(8, Allocator.Persistent);

            var migratedNetworkIds = state.EntityManager.CreateEntity(ComponentType.ReadWrite<MigratedNetworkIdsData>());
            state.EntityManager.SetName(migratedNetworkIds, "MigratedNetworkIDds");
            state.EntityManager.SetComponentData(migratedNetworkIds, new MigratedNetworkIdsData() { MigratedNetworkIds = m_MigrationIds });

            var networkIDAllocationData = state.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkIDAllocationData>());
            state.EntityManager.SetName(networkIDAllocationData, "NetworkIDAllocationData");
            state.EntityManager.SetComponentData(networkIDAllocationData, new NetworkIDAllocationData() { FreeNetworkIds = m_FreeNetworkIds, NumNetworkIds = m_NumNetworkIds });
        }


        // 此方法内容应与 HandleDriverEvents.ApproveConnection 的逻辑保持一致
        void AttemptCreateFakeHostConnection(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                // 合并式单 World Host 仍然需要连接 Entity
                // 创建虚拟连接，用于处理进入游戏等流程
                var ent = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
                state.EntityManager.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
                // TODO 是否应默认设置 NetworkStreamInGame
                // 对单 World Host 而言几乎没有关闭它的场景，如果用户依赖此状态判断是否就绪，应改用自己的用户侧信号
                state.EntityManager.GetBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup { Value = ent });

                // 避免使用 0
                int nid = m_NumNetworkIds.Value + 1;
                m_NumNetworkIds.Value = nid;

                var networkId = new NetworkId {Value = nid};
                state.EntityManager.AddComponentData(ent, networkId);
                state.EntityManager.AddComponent<LocalConnection>(ent); // Binary World Server 不添加此组件，因为本地 Client World 不应区别于其他 Client World
                state.EntityManager.SetName(ent, new FixedString64Bytes(FixedString.Format("Host Fake NetworkConnection ({0})", nid)));
            }
        }

        /// <inheritdoc/>
        public void OnDestroy(ref SystemState state)
        {
            m_RandomIndex.Dispose();
            m_NumNetworkIds.Dispose();
            m_FreeNetworkIds.Dispose();
            m_ConnectionEvents.Dispose();
            m_ConnectionUniqueIds.Dispose();
            m_MigrationIds.Dispose();

            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            if (DriverState.Default == networkStreamDriver.DriverState)
            {
                var driverStore = DriverStore;
                foreach (var connection in SystemAPI.Query<RefRO<NetworkStreamConnection>>())
                {
                    driverStore.Disconnect(connection.ValueRO);
                }
                DriverStore.ScheduleUpdateAllDrivers(state.Dependency).Complete();
                DriverStore.Dispose();
            }
            UnsafeUtility.Free((void*)m_DriverPointers, Allocator.Persistent);

            // 强制清理 ReceivedSnapshotByRemoteMask
            foreach (var snapshotAck in SystemAPI.Query<RefRW<NetworkSnapshotAck>>())
            {
                if (snapshotAck.ValueRO.ReceivedSnapshotByRemoteMask.IsCreated)
                    snapshotAck.ValueRW.ReceivedSnapshotByRemoteMask.Dispose();
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var commandBuffer = SystemAPI.GetSingleton<NetworkGroupCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            FixedString128Bytes debugPrefix = $"[{state.WorldUnmanaged.Name}][Connection]";

#if UNITY_EDITOR || NETCODE_DEBUG
            if (!state.WorldUnmanaged.IsServer())
            {
                // 服务器不需要此操作，目前这里只收集客户端统计信息
                // 如果以后也在此收集服务器统计信息，需要重新处理，GhostSendSystem 中也会重置该值
                var numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs;
                ref var netStatsSnapshotSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
                netStatsSnapshotSingleton.ResetWriter(numLoadedPrefabs);
            }
#endif

            if (!SystemAPI.HasSingleton<NetworkProtocolVersion>())
            {
                // 等待 CreateComponentCollection 被调用，否则会创建 GhostCollection 为 0 的 NetworkProtocolVersion
                var data = SystemAPI.GetSingleton<GhostComponentSerializerCollectionData>();
                if (data.CollectionFinalized.Value != 2)
                    return;

                // 必须使用读写访问，因为此调用会把集合标记为 Final，之后不能再注册 RPC
                ref var rpcCollection = ref SystemAPI.GetSingletonRW<RpcCollection>().ValueRW;
                var serializerState = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
                var npv = new NetworkProtocolVersion
                {
                    NetCodeVersion = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion = SystemAPI.TryGetSingleton(out GameProtocolVersion gameProtocolVersion) ? gameProtocolVersion.Version : 0,
                    RpcCollectionVersion = rpcCollection.CalculateVersionHash(),
                    ComponentCollectionVersion = GhostCollectionSystem.CalculateComponentCollectionHash(serializerState),
                };
                netDebug.DebugLog($"[{state.WorldUnmanaged.Name}] NetworkProtocolVersion finalized with: {npv.ToFixedString()}, DefaultVariants:{data.DefaultVariants.Count}, Serializers:{data.Serializers.Length}, SS:{data.SerializationStrategies.Length}, InputBuffers:{data.InputComponentBufferMap.Count}, RPCs:{rpcCollection.Rpcs.Length}, DynamicAssemblyList:{rpcCollection.DynamicAssemblyList}!");
                state.EntityManager.CreateSingleton(npv);
                npv.AssertIsValid();
            }
            var networkProtocolVersion = SystemAPI.GetSingleton<NetworkProtocolVersion>();

            var driverListening = DriverStore.DriversCount > 0 && DriverStore.GetDriverInstanceRO(DriverStore.FirstDriver).driver.Listening;
            if (driverListening)
            {
                for (int i = DriverStore.FirstDriver + 1; i < DriverStore.LastDriver; ++i)
                {
                    driverListening &= DriverStore.GetDriverInstanceRO(i).driver.Listening;
                }
                // 通过检查是否只有部分 Driver 正在监听来检测 Listen 失败
                if (!driverListening)
                {
                    for (int i = DriverStore.FirstDriver + 1; i < DriverStore.LastDriver; ++i)
                    {
                        ref var instance = ref DriverStore.GetDriverInstanceRW(i);
                        if (instance.driver.Listening)
                            instance.StopListening();
                    }
                }
            }

            k_Scheduling.Begin();
            state.Dependency = DriverStore.ScheduleUpdateAllDrivers(state.Dependency);
            k_Scheduling.End();

            ref var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            var timestampMS = NetworkTimeSystem.TimestampMS;

            if (driverListening)
            {
                m_GhostComponentFromEntity.Update(ref state);
                var acceptJob = new ConnectionAcceptJob
                {
                    driverStore = DriverStore,
                    commandBuffer = commandBuffer,
                    connectionEvents = m_ConnectionEvents,
                    serverApprovedConnectionRpcQueue = m_ServerApprovedConnectionRpcQueue,
                    requestProtocolVersionHandshakeQueue = m_RequestProtocolVersionHandshakeRpcQueue,
                    ghostFromEntity = m_GhostComponentFromEntity,
                    protocolVersion = networkProtocolVersion,
                    netDebug = netDebug,
                    debugPrefix = debugPrefix,
                    currentTime = timestampMS,
                    tickRate = tickRate,
                    requireConnectionApproval = networkStreamDriver.RequireConnectionApproval ? (byte) 1 : (byte) 0,
                };
                k_Scheduling.Begin();
                state.Dependency = acceptJob.Schedule(state.Dependency);
                k_Scheduling.End();
            }
            else
            {
                if (!m_RefreshTickRateQuery.IsEmptyIgnoreFilter)
                {
                    if (!SystemAPI.TryGetSingleton(out tickRate))
                        state.EntityManager.CreateSingleton(tickRate);
                    tickRate.ResolveDefaults();
                    var requests = m_RefreshTickRateQuery.ToComponentDataArray<ClientServerTickRateRefreshRequest>(Allocator.Temp);
                    foreach (var req in requests)
                    {
                        req.ApplyTo(ref tickRate);
                        netDebug.DebugLog($"{debugPrefix} Using SimulationTickRate={tickRate.SimulationTickRate} NetworkTickRate={tickRate.NetworkTickRate} MaxSimulationStepsPerFrame={tickRate.MaxSimulationStepsPerFrame} TargetFrameRateMode={tickRate.TargetFrameRateMode} PredictedPhysicsPerTick={tickRate.PredictedFixedStepSimulationTickRatio}.");
                    }
                    SystemAPI.SetSingleton(tickRate);
                    state.EntityManager.DestroyEntity(m_RefreshTickRateQuery);
                }
                m_FreeNetworkIds.Clear();
            }

            // 让索引持续递增，并通过 Server Tick 增加一定随机性，生成的随机结果不会与之前的结果冲突
            m_RandomIndex.Value += networkTime.ServerTick.SerializedData;

            // 此 Singleton 只存在于客户端，用于在连接销毁和重建之间保留该值
            uint clientConnectionUniqueId = 0;
            if (!state.WorldUnmanaged.IsServer() && SystemAPI.TryGetSingletonRW<ConnectionUniqueId>(out var uniqueId))
                clientConnectionUniqueId = uniqueId.ValueRO.Value;

            m_ConnectionUniqueIdFromEntity.Update(ref state);
            m_ApprovedFromEntity.Update(ref state);
            m_ConnectionStateFromEntity.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
            m_RequestDisconnectFromEntity.Update(ref state);
            m_InGameFromEntity.Update(ref state);
            m_EnablePacketLoggingFromEntity.Update(ref state);
            m_OutgoingRpcBufferFromEntity.Update(ref state);
            m_RpcBufferFromEntity.Update(ref state);
            m_CmdBufferFromEntity.Update(ref state);
            m_SnapshotBufferFromEntity.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);

            // FIXME 此处依赖 Entity 上的 Buffer
            var handleJob = new HandleDriverEvents
            {
                commandBuffer = commandBuffer,
                netDebug = netDebug,
                debugPrefix = debugPrefix,
                driverStore = DriverStore,
                networkIdFromEntity = m_NetworkIdFromEntity,
                connectionUniqueIdFromEntity = m_ConnectionUniqueIdFromEntity,
                ghostInstanceFromEntity = m_GhostComponentFromEntity,
                connectionStateFromEntity = m_ConnectionStateFromEntity,
                requestDisconnectFromEntity = m_RequestDisconnectFromEntity,
                requestProtocolVersionHandshakeQueue = m_RequestProtocolVersionHandshakeRpcQueue,
                inGameFromEntity = m_InGameFromEntity,
                enablePacketLoggingFromEntity = m_EnablePacketLoggingFromEntity,
                freeNetworkIds = m_FreeNetworkIds,
                migrationIds = m_MigrationIds,
                connectionEvents = m_ConnectionEvents,
                connectionUniqueIds = m_ConnectionUniqueIds,

                outgoingRpcBuffer = m_OutgoingRpcBufferFromEntity,
                rpcBuffer = m_RpcBufferFromEntity,
                cmdBuffer = m_CmdBufferFromEntity,
                snapshotBuffer = m_SnapshotBufferFromEntity,
                reliableSequencedPipelineStageId = m_reliableSequencedPipelineStageId,

                requireConnectionApproval = networkStreamDriver.RequireConnectionApproval ? (byte)1 : (byte)0,
                protocolVersion = networkProtocolVersion,
                localTime = timestampMS,
                lastServerTick = networkTime.ServerTick,
                tickRate = tickRate,
                randomIndex = m_RandomIndex,
                clientConnectionUniqueId = clientConnectionUniqueId,
                numNetworkId = m_NumNetworkIds,
                connectionApprovedLookup = m_ApprovedFromEntity,
                serverApprovedConnectionRpcQueue = m_ServerApprovedConnectionRpcQueue,
                serverRequestApprovalRpcQueue = m_ServerRequestApprovalRpcQueue,
                ghostFromEntity = m_GhostComponentFromEntity,
                isServer = state.WorldUnmanaged.IsServer(),
            };
#if UNITY_EDITOR || NETCODE_DEBUG
            handleJob.netStats = SystemAPI.GetSingletonRW<GhostStatsCollectionCommand>().ValueRO.Value;
            handleJob.SnapshotStatsWriters = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().allGhostStatsParallelWrites.AsArray();
#endif
            k_Scheduling.Begin();
            state.Dependency = handleJob.ScheduleByRef(state.Dependency);
            k_Scheduling.End();
        }


        [BurstCompile]
        [StructLayout(LayoutKind.Sequential)]
        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public NetworkDriverStore driverStore;
            public NativeList<NetCodeConnectionEvent> connectionEvents;
            public RpcQueue<ServerApprovedConnection, ServerApprovedConnection> serverApprovedConnectionRpcQueue;
            public RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> requestProtocolVersionHandshakeQueue;
            public ClientServerTickRate tickRate;
            public NetworkProtocolVersion protocolVersion;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public uint currentTime;
            public byte requireConnectionApproval;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;

            public void Execute()
            {
                for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; ++i)
                {
                    ref var driver = ref driverStore.GetDriverRW(i);
                    NetworkConnection con;
                    while ((con = driver.Accept()) != default)
                    {
                        // 新连接不应具有任何待处理事件，如果存在则直接关闭
                        var evt = con.PopEvent(driver, out _);
                        if (evt != NetworkEvent.Type.Empty)
                        {
                            con.Disconnect(driver);
                            netDebug.DebugLog(FixedString.Format("[{0}][Connection] Disconnecting stale connection detected as new (has pending event={1}).",debugPrefix, (int)evt));
                            continue;
                        }

                        // TODO 查找是否已有使用相同 IP 地址或其他玩家标识的连接
                        // 仅依赖 IP 的验证较弱，但至少可以排除一部分重复连接
                        Debug.Assert(tickRate.HandshakeApprovalTimeoutMS > 0);
                        var ent = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
                        var connection = new NetworkStreamConnection
                        {
                            Value = con,
                            DriverId = i,
                            CurrentState = ConnectionState.State.Handshake,
                            CurrentStateDirty = false,
                            ConnectionApprovalTimeoutStart = currentTime,
                        };
                        commandBuffer.AddComponent(ent, connection);
                        commandBuffer.AddComponent(ent, new NetworkSnapshotAck
                        {
                            ReceivedSnapshotByRemoteMask = new UnsafeBitArray((int)math.max(1024, tickRate.SnapshotAckMaskCapacity), Allocator.Persistent),
                        });
                        commandBuffer.AddBuffer<PrespawnSectionAck>(ent);
                        var outgoingBuf = commandBuffer.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
                        commandBuffer.AddBuffer<IncomingCommandDataStreamBuffer>(ent);
                        commandBuffer.AppendToBuffer(ent, new LinkedEntityGroup{Value = ent});
                        commandBuffer.SetName(ent, (FixedString64Bytes)$"NetworkConnection (Handshake:{tickRate.HandshakeApprovalTimeoutMS}ms)");

                        requestProtocolVersionHandshakeQueue.Schedule(outgoingBuf, ghostFromEntity, new RequestProtocolVersionHandshake
                        {
                            Data = protocolVersion,
                        });

                        connection.CurrentState = ConnectionState.State.Handshake;
                        connection.CurrentStateDirty = false;
                        connectionEvents.Add(new NetCodeConnectionEvent
                        {
                            Id = default,
                            ConnectionId = connection.Value,
                            State = ConnectionState.State.Handshake,
                            DisconnectReason = default,
                            ConnectionEntity = ent,
                        });
                        netDebug.DebugLog((FixedString512Bytes) $"{debugPrefix} Server accepted new connection {connection.Value.ToFixedString()}, waiting for handshake...");
                    }
                }
            }
        }

        [BurstCompile]
        partial struct HandleDriverEvents : IJobEntity
        {
            public EntityCommandBuffer commandBuffer;
            public NetDebug netDebug;
            public FixedString128Bytes debugPrefix;
            public NetworkDriverStore driverStore;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdFromEntity;
            [ReadOnly] public ComponentLookup<ConnectionUniqueId> connectionUniqueIdFromEntity;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostInstanceFromEntity;
            public ComponentLookup<ConnectionState> connectionStateFromEntity;
            public RpcQueue<RequestProtocolVersionHandshake, RequestProtocolVersionHandshake> requestProtocolVersionHandshakeQueue;
            public ComponentLookup<NetworkStreamRequestDisconnect> requestDisconnectFromEntity;
            public ComponentLookup<NetworkStreamInGame> inGameFromEntity;
            public ComponentLookup<EnablePacketLogging> enablePacketLoggingFromEntity;
            public NativeQueue<int> freeNetworkIds;
            public NativeHashMap<uint, int> migrationIds;
            public NativeList<NetCodeConnectionEvent> connectionEvents;
            public NativeList<uint> connectionUniqueIds;

            public BufferLookup<OutgoingRpcDataStreamBuffer> outgoingRpcBuffer;
            public BufferLookup<IncomingRpcDataStreamBuffer> rpcBuffer;
            public BufferLookup<IncomingCommandDataStreamBuffer> cmdBuffer;
            public BufferLookup<IncomingSnapshotDataStreamBuffer> snapshotBuffer;
            public NetworkPipelineStageId reliableSequencedPipelineStageId;

            public NetworkProtocolVersion protocolVersion;

            public byte requireConnectionApproval;
            public ClientServerTickRate tickRate;
            public uint localTime;
            public NetworkTick lastServerTick;

            // Approval 相关数据
            public uint clientConnectionUniqueId;
            public NativeReference<uint> randomIndex;
            public NativeReference<int> numNetworkId;
            public RpcQueue<ServerApprovedConnection, ServerApprovedConnection> serverApprovedConnectionRpcQueue;
            public RpcQueue<ServerRequestApprovalAfterHandshake, ServerRequestApprovalAfterHandshake> serverRequestApprovalRpcQueue;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;
            [ReadOnly] public ComponentLookup<ConnectionApproved> connectionApprovedLookup;
            public bool isServer;

            [NativeSetThreadIndex] int m_ThreadIndex;

#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<uint> netStats;
            public NativeArray<UnsafeGhostStatsSnapshot> SnapshotStatsWriters;
#endif

            public void Execute(Entity entity, ref NetworkStreamConnection connection, ref NetworkSnapshotAck snapshotAck)
            {
                var disconnectReason = NetworkStreamDisconnectReason.ConnectionClose;
                if (Hint.Unlikely(requestDisconnectFromEntity.TryGetComponent(entity, out var disconnectRequest)))
                {
                    disconnectReason = disconnectRequest.Reason;
                    driverStore.Disconnect(connection);
                    // 断开连接的清理会在下方处理
                }
                else if (!inGameFromEntity.HasComponent(entity))
                {
                    snapshotAck = new NetworkSnapshotAck
                    {
                        LastReceivedRemoteTime = snapshotAck.LastReceivedRemoteTime,
                        LastReceiveTimestamp = snapshotAck.LastReceiveTimestamp,
                        EstimatedRTT = snapshotAck.EstimatedRTT,
                        DeviationRTT = snapshotAck.DeviationRTT,
                        ReceivedSnapshotByRemoteMask = snapshotAck.ReceivedSnapshotByRemoteMask,
                    };
                }

                if (Hint.Unlikely(!connection.Value.IsCreated))
                {
                    netDebug.LogError($"{debugPrefix} Stale NetworkStreamConnection.Value ({connection.Value.ToFixedString()}, DriverId: {connection.DriverId}, VPVR: {connection.ProtocolVersionReceived}) found on {entity.ToFixedString()}! Did you modify `Value` in your code?");
                    return;
                }

                networkIdFromEntity.TryGetComponent(entity, out var networkId);
                HandleApproval(entity, ref connection, ref networkId, ref disconnectReason);

                // 更新状态
                ref var driverInstance = ref driverStore.GetDriverInstanceRW(connection.DriverId);
                ref var driver = ref driverInstance.driver;

                // 取出事件
                NetworkEvent.Type evt;
                while ((evt = driver.PopEventForConnection(connection.Value, out var reader, out var pipelineStage)) != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                        {
                            // 此事件只在客户端触发，服务器会在 Accept() 调用期间绕过它
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            Debug.Assert(!isServer, "Sanity check failed: got connect event, but not on server");
                            Debug.Assert(!snapshotAck.ReceivedSnapshotByRemoteMask.IsCreated);
#endif
                            netDebug.DebugLog($"{debugPrefix} Client connected to driver, sending {protocolVersion.ToFixedString()} Connection[UniqueId:{clientConnectionUniqueId}] to server to begin handshake...");
                            snapshotAck.SnapshotPacketLoss = default;
                            var buf = outgoingRpcBuffer[entity];
                            requestProtocolVersionHandshakeQueue.Schedule(buf, ghostInstanceFromEntity, new RequestProtocolVersionHandshake
                            {
                                Data = protocolVersion,
                                ConnectionUniqueId = clientConnectionUniqueId
                            });
                            connectionEvents.Add(new NetCodeConnectionEvent
                            {
                                Id = default,
                                ConnectionId = connection.Value,
                                State = ConnectionState.State.Handshake,
                                DisconnectReason = disconnectReason,
                                ConnectionEntity = entity,
                            });
                            connection.CurrentState = ConnectionState.State.Handshake;
                            connection.ConnectionApprovalTimeoutStart = localTime;
                            connection.CurrentStateDirty = false;
                            break;
                        }
                        case NetworkEvent.Type.Disconnect:
                            if (reader.Length == 1)
                                disconnectReason = (NetworkStreamDisconnectReason) reader.ReadByte();
                            // 断开连接的清理会在下方处理
                            connection.CurrentState = ConnectionState.State.Disconnected;
                            connection.CurrentStateDirty = false;
                            goto doubleBreak;
                        case NetworkEvent.Type.Data:
                            var msgType = (NetworkStreamProtocol)reader.ReadByte();

                            // 处理连接审批阶段，未完成审批时不继续处理游戏数据
                            if (isServer && connection.IsHandshakeOrApproval)
                            {
                                if (msgType != NetworkStreamProtocol.Rpc)
                                {
                                    netDebug.LogError($"{debugPrefix} Ignoring NetworkStreamProtocol msgType {(byte)msgType} as {connection.Value.ToFixedString()} is in approval stage. Only approval RPCs are allowed.");
                                    continue;
                                }
                            }

                            switch (msgType)
                            {
                                case NetworkStreamProtocol.Command:
                                {
                                    if (!cmdBuffer.HasBuffer(entity))
                                        break;
                                    var buffer = cmdBuffer[entity];
                                    var snapshot = new NetworkTick{SerializedData = reader.ReadUInt()};
                                    uint snapshotMask = reader.ReadUInt();
                                    snapshotAck.UpdateReceivedByRemote(snapshot, snapshotMask, out var numSnapshotErrorsRequiringReset);
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    uint interpolationDelay = reader.ReadUInt();
                                    uint numLoadedPrefabs = reader.ReadUShort();

                                    snapshotAck.UpdateRemoteAckedData(remoteTime, numLoadedPrefabs, interpolationDelay);
                                    var rtt = NetworkSnapshotAck.CalculateRttViaLocalTime(localTime, localTimeMinusRTT);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);
                                    var cmdTickIsFull = reader.ReadByte();
                                    var tickReader = reader;
                                    var cmdTick = new NetworkTick{SerializedData = tickReader.ReadUInt()};
                                    var isValidCmdTick = !snapshotAck.LastReceivedSnapshotByLocal.IsValid ||
                                                         cmdTick.IsNewerThan(snapshotAck.LastReceivedSnapshotByLocal) ||
                                                         (snapshotAck.LastReceivedSnapshotByLocal.Equals(cmdTick) && cmdTickIsFull != 0);
#if UNITY_EDITOR || NETCODE_DEBUG
                                    netStats[0] = lastServerTick.SerializedData;
                                    netStats[1] = (uint)reader.Length - 1u;
                                    if (!isValidCmdTick || buffer.Length > 0)
                                    {
                                        netStats[2] = netStats[2] + 1;
                                    }
                                    if(numSnapshotErrorsRequiringReset != 0)
                                    {
                                        var msg = (FixedString512Bytes)$"{connection.Value.ToFixedString()} reported recoverable snapshot read errors. Thus, we have reset their entire ack history. Note: This incurs a bandwidth and CPU cost, as we must resend all relevant ghost chunks again (i.e. as if this was a new joiner).";
                                        netDebug.LogWarning($"{debugPrefix} {msg}");
                                        TryLog(in entity, msg);
                                    }
#endif
                                    // 不处理比已处理命令更旧的入站命令
                                    if (!isValidCmdTick)
                                        break;
                                    snapshotAck.LastReceivedSnapshotByLocal = cmdTick;
                                    snapshotAck.MostRecentFullCommandTick = cmdTick;
                                    if(cmdTickIsFull == 0)
                                        snapshotAck.MostRecentFullCommandTick.Decrement();
                                    buffer.Clear();
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Snapshot:
                                {
                                    if (Hint.Unlikely(!snapshotBuffer.TryGetBuffer(entity, out var buffer)))
                                        break;
#if UNITY_EDITOR || NETCODE_DEBUG
                                    ref var netStatsSnapshots = ref SnapshotStatsWriters.AsSpan()[m_ThreadIndex];
                                    netStatsSnapshots.SnapshotTotalSizeInBits = (uint)(reader.Length) * 8;
#endif
                                    uint remoteTime = reader.ReadUInt();
                                    uint localTimeMinusRTT = reader.ReadUInt();
                                    snapshotAck.ServerCommandAge = reader.ReadInt();
                                    var rtt = NetworkSnapshotAck.CalculateRttViaLocalTime(localTime, localTimeMinusRTT);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);

                                    // Snapshot 序列 ID
                                    var currentSnapshotSequenceId = reader.ReadByte();

                                    // 在此复制 Reader，因为需要把 Server Tick 传给 GhostReceiveSystem
                                    // 如果读取过远，该操作会失败
                                    var copyOfReader = reader;
                                    var currentSnapshotServerTick = new NetworkTick{SerializedData = copyOfReader.ReadUInt()};

                                    // 跳过旧 Snapshot
                                    var isValid = !snapshotAck.LastReceivedSnapshotByLocal.IsValid || currentSnapshotServerTick.IsNewerThan(snapshotAck.LastReceivedSnapshotByLocal);
                                    UpdatePacketLossStats(ref snapshotAck.SnapshotPacketLoss, isValid, currentSnapshotSequenceId, currentSnapshotServerTick, ref snapshotAck, in entity, buffer);
                                    if (!isValid)
                                        break;
                                    // 这在一定程度上是合理的：如果收到 3 个数据包，只确认最后一个是有效行为
                                    if (snapshotAck.LastReceivedSnapshotByLocal.IsValid)
                                    {
                                        // 移除最近确认的数据包
                                        // 同一帧收到多个数据包时，之前的数据包永远不会被处理，
                                        // 因而不能向服务器声称已经取得对应 Server Tick 的数据
                                        if (buffer.Length > 0)
                                            snapshotAck.ReceivedSnapshotByLocalMask ^= 0x1;
                                        // 移动 Ack 窗口，此处执行位移是正确行为
                                        var shamt = currentSnapshotServerTick.TicksSince(snapshotAck.LastReceivedSnapshotByLocal);
                                        if (shamt < 32)
                                            snapshotAck.ReceivedSnapshotByLocalMask <<= shamt;
                                        else
                                            snapshotAck.ReceivedSnapshotByLocalMask = 0;
                                    }
                                    snapshotAck.ReceivedSnapshotByLocalMask |= 1;
                                    snapshotAck.LastReceivedSnapshotByLocal = currentSnapshotServerTick;
                                    snapshotAck.CurrentSnapshotSequenceId = currentSnapshotSequenceId;

                                    // 限制：覆盖之前的所有 Snapshot，即使它们尚未处理
                                    if (buffer.Length > 0)
                                    {
#if UNITY_EDITOR || NETCODE_DEBUG
                                        netStats[2] = netStats[2] + 1;
#endif
                                        buffer.Clear();
                                    }

                                    // 把新 Snapshot 保存到 Buffer，以便在 GhostReceiveSystem 中处理
                                    buffer.Add(ref reader);
                                    break;
                                }
                                case NetworkStreamProtocol.Rpc:
                                {
                                    uint remoteTime = reader.ReadUInt();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    UnityEngine.Debug.Assert(reader.GetBytesRead() == RpcCollection.k_RpcCommonHeaderLengthBytes);
#endif
                                    var rtt = NetworkSnapshotAck.GetRpcRttFromReliablePipeline(connection, ref driver, ref driverInstance, pipelineStage, reliableSequencedPipelineStageId);
                                    snapshotAck.UpdateRemoteTime(remoteTime, rtt, localTime);
                                    var buffer = rpcBuffer[entity];
                                    buffer.Add(ref reader);
                                    break;
                                }
                                default:
                                    netDebug.LogError(FixedString.Format("Received unknown message type {0}", (byte)msgType));
                                    break;
                            }

                            break;
                        default:
                            netDebug.LogError(FixedString.Format("Received unknown network event {0}", (int)evt));
                            break;
                    }
                }
                doubleBreak:

                // 响应状态变化

                // CurrentStateDirty 是一项临时处理，只用于以下情况：
                // - 客户端的 `Connecting` 状态
                // - 客户端的 `Approval` 状态
                // 大多数位置会有意绕过它，参见分散在各处的事件触发逻辑
                if(Hint.Unlikely(connection.CurrentStateDirty))
                {
                    connection.CurrentStateDirty = false;
                    connectionEvents.Add(new NetCodeConnectionEvent
                    {
                        Id = networkId,
                        ConnectionId = connection.Value,
                        State = connection.CurrentState,
                        DisconnectReason = disconnectReason,
                        ConnectionEntity = entity,
                    });
                }

                // 处理断开连接
                // Transport 不会为本地主动断开的连接触发本地 Disconnect 事件，因此 NetCode 需要通过状态轮询补发事件
                // TODO 未来将通过功能开关 `EnableDisconnectEventOnSelf = true` 支持本地事件
                if (Hint.Unlikely(connection.CurrentState == ConnectionState.State.Disconnected
                                  || driver.GetConnectionState(connection.Value) == NetworkConnection.State.Disconnected))
                {
                    commandBuffer.RemoveComponent<NetworkStreamConnection>(entity);
                    commandBuffer.DestroyEntity(entity);

                    if (cmdBuffer.HasBuffer(entity))
                        cmdBuffer[entity].Clear();

                    if (networkId.Value != default)
                        freeNetworkIds.Enqueue(networkId.Value);

                    if (connectionUniqueIdFromEntity.HasComponent(entity))
                    {
                        var cuid = connectionUniqueIdFromEntity[entity].Value;
                        for (int i = 0; i < connectionUniqueIds.Length; ++i)
                        {
                            if (connectionUniqueIds[i] == cuid)
                            {
                                connectionUniqueIds.RemoveAtSwapBack(i);
                                break;
                            }
                        }
                    }

                    netDebug.DebugLog($"{debugPrefix} {connection.Value.ToFixedString()} closed NetworkId={networkId.Value} Reason={disconnectReason.ToFixedString()}.");
                    connectionEvents.Add(new NetCodeConnectionEvent
                    {
                        Id = networkId,
                        ConnectionId = connection.Value,
                        State = ConnectionState.State.Disconnected,
                        DisconnectReason = disconnectReason,
                        ConnectionEntity = entity,
                    });

                    if (snapshotAck.ReceivedSnapshotByRemoteMask.IsCreated)
                        snapshotAck.ReceivedSnapshotByRemoteMask.Dispose();
                    connection.Value = default;
                    connection.CurrentState = ConnectionState.State.Disconnected;
                    connection.CurrentStateDirty = false;
                }

                // 更新 ConnectionState
                if (connectionStateFromEntity.TryGetComponent(entity, out var existingState))
                {
                    var newState = existingState;
                    newState.DisconnectReason = disconnectReason;
                    newState.CurrentState = connection.CurrentState;
                    newState.NetworkId = networkId.Value;
                    if (Hint.Unlikely(!existingState.Equals(newState)))
                        connectionStateFromEntity[entity] = newState;
                }
            }

            /// <summary>
            /// 以内联方式调用，因为需要尽快更新 NetworkId
            /// </summary>
            private void HandleApproval(Entity entity, ref NetworkStreamConnection connection, ref NetworkId networkId, ref NetworkStreamDisconnectReason disconnectReason)
            {
                if (!connection.IsHandshakeOrApproval) return;

                // 处理 Handshake
                if (isServer && connection.ProtocolVersionReceived != 0 && connection.CurrentState == ConnectionState.State.Handshake)
                {
                    if (requireConnectionApproval == 0)
                    {
                        var buf = outgoingRpcBuffer[entity];
                        ApproveConnection(entity, ref connection, buf, ref networkId);
                    }
                    else
                    {
                        // 开始 Approval 流程
                        connection.CurrentState = ConnectionState.State.Approval;
                        connection.CurrentStateDirty = false;
                        connectionEvents.Add(new NetCodeConnectionEvent
                        {
                            Id = default,
                            ConnectionId = connection.Value,
                            State = ConnectionState.State.Approval,
                            DisconnectReason = default,
                            ConnectionEntity = entity,
                        });
                        netDebug.DebugLog($"{debugPrefix} Server ProtocolVersion handshake successful for {connection.Value.ToFixedString()}, requesting (and awaiting) valid approval RPC from client...");
                        commandBuffer.SetName(entity, (FixedString64Bytes) $"NetworkConnection (Approval:{tickRate.HandshakeApprovalTimeoutMS}ms)");
                        var buf = outgoingRpcBuffer[entity];
                        serverRequestApprovalRpcQueue.Schedule(buf, ghostFromEntity, new ServerRequestApprovalAfterHandshake());
                    }
                }

                // 处理 ConnectionApproved 组件
                if (!networkIdFromEntity.HasComponent(entity) && connectionApprovedLookup.HasComponent(entity))
                {
                    if (isServer)
                    {
                        if (requireConnectionApproval != 0)
                        {
                            switch (connection.CurrentState)
                            {
                                case ConnectionState.State.Approval:
                                    var buf = outgoingRpcBuffer[entity];
                                    ApproveConnection(entity, ref connection, buf, ref networkId);
                                    break;
                                case ConnectionState.State.Handshake:
                                    // 等待 Handshake 完成
                                    break;
                                default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    if(!netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning)
                                        netDebug.LogWarning($"{debugPrefix} Approved {connection.Value.ToFixedString()} but in state {connection.CurrentState.ToFixedString()}.");
#endif
                                    break;
                            }
                        }
                        else
                        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            netDebug.LogWarning($"{debugPrefix} Approved connection {connection.Value.ToFixedString()} but this server does not require connection approval!");
#endif
                        }
                    }
                }

                // 处理超时
                // 客户端也可以让自身超时，但仅限非 Handshake 状态，因为客户端不知道配置的超时时长
                if (Hint.Unlikely(connection.ConnectionApprovalTimeoutStart != 0))
                {
                    var isClientHandshaking = !isServer && connection.CurrentState == ConnectionState.State.Handshake;
                    if (isClientHandshaking) return;
                    var elapsedSinceApprovalStartMS = localTime - connection.ConnectionApprovalTimeoutStart;
                    if (Hint.Unlikely(elapsedSinceApprovalStartMS >= tickRate.HandshakeApprovalTimeoutMS))
                    {
                        Debug.Assert(connection.CurrentState == ConnectionState.State.Handshake || connection.CurrentState == ConnectionState.State.Approval);
                        netDebug.LogError($"{debugPrefix} {connection.Value.ToFixedString()} timed out after {elapsedSinceApprovalStartMS}ms (threshold:{tickRate.HandshakeApprovalTimeoutMS}ms, state:{connection.CurrentState.ToFixedString()})!");
                        disconnectReason = connection.CurrentState == ConnectionState.State.Handshake
                            ? NetworkStreamDisconnectReason.HandshakeTimeout
                            : NetworkStreamDisconnectReason.ApprovalTimeout;
                        driverStore.Disconnect(connection);
                    }
                }
            }

            /// <summary>
            /// 真正完全接受连接的逻辑，仅在 Handshake 和启用时的 Approval 成功后调用一次
            /// </summary>
            private void ApproveConnection(Entity ent, ref NetworkStreamConnection connection, DynamicBuffer<OutgoingRpcDataStreamBuffer> outgoingBuffer, ref NetworkId networkId)
            {
                // 如果是返回的客户端，则重新分配之前的唯一 ID
                uint connectionUniqueId = 0;
                bool isReconnecting = false;
                if (connectionUniqueIdFromEntity.HasComponent(ent))
                {
                    // 仅当 ID 尚未注册时重新分配
                    var clientReportedId = connectionUniqueIdFromEntity[ent].Value;
                    if (!connectionUniqueIds.Contains(clientReportedId))
                        connectionUniqueId = clientReportedId;
                    else
                        Debug.LogWarning($"Client is reporting an already reserved connection unique ID {clientReportedId} but this ID is already registered. Generating a new one.");
                    isReconnecting = true;
                }

                var newNetworkId = 0;

                if ( isReconnecting && connectionUniqueId != 0 )
                {
                    migrationIds.TryGetValue(connectionUniqueId, out newNetworkId);
                }

                if (newNetworkId == 0 && !freeNetworkIds.TryDequeue(out newNetworkId))
                {
                    // 避免使用 0
                    newNetworkId = numNetworkId.Value + 1;
                    numNetworkId.Value = newNetworkId;
                }

                if (connectionUniqueId == 0)
                {
                    if (randomIndex.Value == uint.MaxValue)
                        randomIndex.Value = 0;
                    var random = Mathematics.Random.CreateFromIndex(randomIndex.Value);
                    connectionUniqueId = random.NextUInt();
                    int count = 0;
                    while (connectionUniqueIds.Contains(connectionUniqueId))
                    {
                        Debug.LogWarning($"Unique ID collision for ID {connectionUniqueId}, will generate another one.");
                        randomIndex.Value++;
                        random = Mathematics.Random.CreateFromIndex(randomIndex.Value);
                        connectionUniqueId = random.NextUInt();
                        // 几乎不可能发生 100 次冲突，但仍设置上限以防止无限循环
                        if (count++ > 100)
                        {
                            Debug.LogError($"Failed to generate a non-colliding unique ID for network ID {newNetworkId}, unique ID count {connectionUniqueIds.Length}.");
                            break;
                        }
                    }
                    randomIndex.Value++;
                }
                commandBuffer.AddComponent(ent, new ConnectionUniqueId(){ Value = connectionUniqueId });
                connectionUniqueIds.Add(connectionUniqueId);

                // AttemptCreateFakeHostConnection 的逻辑应与此处保持一致，修改时必须同时复查该方法
                networkId = new NetworkId {Value = newNetworkId};
                commandBuffer.AddComponent(ent, networkId);
                commandBuffer.SetName(ent, new FixedString64Bytes(FixedString.Format("NetworkConnection ({0})", newNetworkId)));
                var serverApprovedConnection = new ServerApprovedConnection();
                serverApprovedConnection.NetworkId = newNetworkId;
                serverApprovedConnection.UniqueId = connectionUniqueId;
                serverApprovedConnection.RefreshRequest.ReadFrom(in tickRate);
                serverApprovedConnectionRpcQueue.Schedule(outgoingBuffer, ghostFromEntity, serverApprovedConnection);
                connection.CurrentState = ConnectionState.State.Connected;
                connection.CurrentStateDirty = false;
                connection.ConnectionApprovalTimeoutStart = 0;
                connectionEvents.Add(new NetCodeConnectionEvent
                {
                    Id = networkId,
                    ConnectionId = connection.Value,
                    State = ConnectionState.State.Connected,
                    DisconnectReason = default,
                    ConnectionEntity = ent,
                });
                netDebug.DebugLog($"{debugPrefix} Server approved connection {connection.Value.ToFixedString()}, assigning NetworkId={newNetworkId} UniqueId={connectionUniqueId} Reconnecting={isReconnecting} State={connection.CurrentState}.");
            }

            /// <summary>
            /// 记录 SnapshotSequenceId（SSId）统计信息，检测丢包、重复包和乱序包
            /// </summary>
            // ReSharper disable once UnusedParameter.Local
            private void UpdatePacketLossStats(ref SnapshotPacketLossStatistics stats, bool snapshotIsConfirmedNewer,
                in byte currentSnapshotSequenceId, NetworkTick currentSnapshotServerTick, ref NetworkSnapshotAck snapshotAck,
                in Entity entity, DynamicBuffer<IncomingSnapshotDataStreamBuffer> buffer)
            {
                if (stats.NumPacketsReceived == 0) snapshotAck.CurrentSnapshotSequenceId = (byte) (currentSnapshotSequenceId - 1);
                stats.NumPacketsReceived++;

                var sequenceIdDelta = snapshotAck.CalculateSequenceIdDelta(currentSnapshotSequenceId, snapshotIsConfirmedNewer);
                if (snapshotIsConfirmedNewer)
                {
                    // 检测丢包
                    var numDroppedPackets = sequenceIdDelta - 1;
                    if (numDroppedPackets > 0)
                    {
                        stats.NumPacketsDroppedNeverArrived += (ulong) numDroppedPackets;
#if NETCODE_DEBUG
                        TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Inferred {numDroppedPackets} snapshots dropped!");
#endif
                    }

                    // NetCode 限制：每个 Tick 只能处理一个 Snapshot
                    if (buffer.Length > 0)
                    {
                        stats.NumPacketsCulledAsArrivedOnSameFrame++;
#if NETCODE_DEBUG
                        TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Clobbering previous snapshot, arrived same frame.");
#endif
                    }

#if NETCODE_DEBUG
                    TryLog(entity, (FixedString512Bytes)$"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Accepted & queued!");
#endif
                    return;
                }

                // 检测乱序包和重复包
                if (sequenceIdDelta == 0)
                {
                    // 除非保留 Ack 历史，否则无法跟踪之前的重复包
                    // 因此这里只记录日志，不进行统计
#if NETCODE_DEBUG
                    TryLog(entity, (FixedString512Bytes) $"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Detected duplicated snapshot packet!");
#endif
                    return;
                }

                stats.NumPacketsCulledOutOfOrder++;
                // 从技术上讲，之前跳过的数据包已被计为丢失，但它刚刚到达
                // 也可能无法预先获知该数据包，因为连接期间的抖动会让系统检测到本就不应收到的丢包
                if (stats.NumPacketsDroppedNeverArrived > 0)
                    stats.NumPacketsDroppedNeverArrived--;
#if NETCODE_DEBUG
                TryLog(entity, (FixedString512Bytes) $"[SSId:{currentSnapshotSequenceId}, ST:{currentSnapshotServerTick.ToFixedString()}] Culled as arrived {Unity.Mathematics.math.abs(sequenceIdDelta)} ServerTicks late!");
#endif
            }


            [Conditional("NETCODE_DEBUG")]
            private void TryLog(in Entity entity, in FixedString512Bytes msg)
            {
#if NETCODE_DEBUG
                if(enablePacketLoggingFromEntity.TryGetComponent(entity, out var comp) && comp.NetDebugPacketCache.IsCreated)
                    comp.NetDebugPacketCache.Log(msg);
#endif
            }
        }
    }
}
