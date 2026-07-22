using Unity.Entities;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Collections.LowLevel.Unsafe;
using System;
using Unity.Networking.Transport.Relay;

namespace Unity.NetCode
{
    /// <summary>
    /// 保存 <see cref="NetworkDriverStore"/> 引用的 Singleton，
    /// 用于便捷地监听新连接或连接服务器
    /// 还提供获取 <see cref="NetworkStreamConnection"/> 远端地址及其底层 Transport 状态的快捷方法
    /// </summary>
    public unsafe struct NetworkStreamDriver : IComponentData
    {
        internal struct Pointers
        {
            public NetworkDriverStore DriverStore;
            public ConcurrentDriverStore ConcurrentDriverStore;
        }
        internal NetworkStreamDriver(void* driverStore, NativeReference<int> numIds, NativeQueue<int> freeIds, NetworkEndpoint endPoint, NativeList<NetCodeConnectionEvent> connectionEventsList, NativeArray<NetCodeConnectionEvent>.ReadOnly connectionEventsForTick)
        {
            m_DriverPointer = driverStore;
            //DriverStore = driverStore;
            //ConcurrentDriverStore = driverStore.ToConcurrent();
            LastEndPoint = endPoint;
            DriverState = NetworkStreamReceiveSystem.DriverState.Default;
            m_NumNetworkIds = numIds;
            m_FreeNetworkIds = freeIds;
            ConnectionEventsList = connectionEventsList;
            ConnectionEventsForTick = connectionEventsForTick;
            RequireConnectionApprovalInternal = 0;
        }

        private void* m_DriverPointer;

        /// <summary>
        /// 指向底层 <see cref="DriverStore"/> 的指针，可用于访问原始 Transport API<br/>
        /// <b>警告：执行 Driver 操作时，必须以读写方式获取 <see cref="NetworkStreamDriver"/></b>
        /// </summary>
        /// <remarks>
        /// <see cref="NetworkDriverStore"/> 具有特定使用模式，参见下方 for 循环示例，请谨慎使用<br/>
        /// 复制如此大的结构体开销很高，应优先使用 <c>ref var driverStore = ref networkStreamDriver.RefRW.DriverStore;</c> 语法
        /// </remarks>
        public ref NetworkDriverStore DriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).DriverStore;

        /// <summary>
        /// <see cref="NetworkDriverStore"/> 并发版本，即 <see cref="ConcurrentDriverStore"/> 的引用，
        /// 用于在 Job 中发送和接收消息
        /// </summary>
        public ref ConcurrentDriverStore ConcurrentDriverStore => ref UnsafeUtility.AsRef<Pointers>(m_DriverPointer).ConcurrentDriverStore;

        /// <summary>
        /// 便捷属性，记录最近一次调用 <see cref="Listen"/> 或 <see cref="Connect"/> 时使用的 DriverStore
        /// </summary>
        /// <remarks>
        /// <para>由于 <see cref="IPCNetworkInterface"/> 的存在，每个 <see cref="NetworkStreamDriver"/>
        /// 实际使用的 Endpoint 可能不同</para>
        /// <para>参见 <see cref="SanitizeConnectAddress"/> 和 <see cref="SanitizeListenAddress"/></para>
        /// </remarks>
        public NetworkEndpoint LastEndPoint { get; internal set; }

        internal NetworkStreamReceiveSystem.DriverState DriverState { get; private set; }

        private NativeReference<int> m_NumNetworkIds;
        private NativeQueue<int> m_FreeNetworkIds;

        /// <summary>
        /// 要求 Driver Store 中所有 Driver 的全部入站连接都经过连接审批流程
        /// 如果关闭，连接会立即获批并从 Connecting 进入 Handshake 状态
        /// <br/>仅服务器使用，客户端上始终为 false
        /// </summary>
        public bool RequireConnectionApproval
        {
            get => RequireConnectionApprovalInternal == 1;
            set
            {
                for (var i = DriverStore.FirstDriver; i < DriverStore.LastDriver; ++i)
                {
                    ref readonly var driverInstance = ref DriverStore.GetDriverInstanceRO(i);
                    if (driverInstance.driver.IsCreated && driverInstance.driver.Bound)
                    {
                        UnityEngine.Debug.LogError("Attempting to set RequireConnectionApproval while network driver has already been started. This must be done before connecting/listening.");
                        return;
                    }
                }
                RequireConnectionApprovalInternal = value ? (byte)1 : (byte)0;
            }
        }
        internal byte RequireConnectionApprovalInternal;

        /// <summary>
        ///     <para>
        ///         保存 NetCode 在当前 <see cref="SimulationSystemGroup" /> Tick 触发的全部
        ///         <see cref="NetCodeConnectionEvent" />，
        ///         使用户代码能够订阅连接与断开事件，包括适用时的 <see cref="ConnectionState.State.Handshake" />
        ///         和 <see cref="ConnectionState.State.Approval" />
        ///         更多信息请参阅 Network Connection 页面
        ///         (https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/network-connection.html)
        ///     </para>
        ///     <para>
        ///         这是自清理列表，因此没有消费 API
        ///         换言之，无需显式移除集合中的条目，这也是它只读的原因
        ///         这还意味着事件只在单个 <see cref="SimulationSystemGroup" /> Tick 内有效，必须在此 Group 内轮询
        ///     </para>
        ///     <para>
        ///         此集合会在 <see cref="NetworkGroupCommandBufferSystem" /> 中清空并重新填充，
        ///         该系统也会回放用于创建和销毁 <see cref="NetworkStreamConnection" /> NetworkConnection Entity 的 ECB
        ///         因此，如果通过 <see cref="UpdateAfterAttribute" /> 在 `NetworkGroupCommandBufferSystem` 之后查询集合，
        ///         会得到当前 Tick 的事件数据；如果在它之前轮询，事件数据始终会落后一个 Tick
        ///     </para>
        ///  <code>
        ///      [BurstCompile]
        ///      void ISystem.OnUpdate(ref SystemState state)
        ///      {
        ///          foreach (var evt in SystemAPI.GetSingleton&lt;NetworkStreamDriver&gt;().ConnectionEventsForTick)
        ///          {
        ///              UnityEngine.Debug.Log($"[{state.WorldUnmanaged.Name}] {evt.ToFixedString()}!");
        ///          }
        ///      }</code>
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         只要以读写方式获取 <see cref="NetworkStreamDriver" /> Singleton，就可以把此集合安全传入 Job
        ///     </para>
        ///     <para>
        ///         Client World 也会触发这些事件，但仅针对自身客户端
        ///         即每个客户端都不会收到其他客户端的相关事件
        ///         可参考 PlayerList NetcodeSamples 示例
        ///         (https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/PlayerList)
        ///         其中展示了实际传递玩家加入与离开事件的 RPC 逻辑，包括显示名称和
        ///         <see cref="NetworkStreamDisconnectReason" />
        ///     </para>
        /// </remarks>
        public NativeArray<NetCodeConnectionEvent>.ReadOnly ConnectionEventsForTick { get; internal set; }

        /// <summary>
        ///     <see cref="NetCodeConnectionEvent"/> 的原始列表，参见 <see cref="ConnectionEventsForTick"/>
        /// </summary>
        internal NativeList<NetCodeConnectionEvent> ConnectionEventsList { get; }

        /// <summary>
        /// 检查 Endpoint 是否可供指定 Driver 类型监听
        /// 当前会强制执行 <see cref="IPCNetworkInterface"/> 的规则
        /// </summary>
        /// <param name="endpoint">要验证并清洗的地址</param>
        /// <param name="driverId">[FirstDriver, LastDriver) 范围内的 Driver ID</param>
        /// <returns>地址对 Driver 有效或可以成功清洗时返回可供监听的有效地址，否则返回无效地址</returns>
        private NetworkEndpoint SanitizeListenAddress(in NetworkEndpoint endpoint, int driverId)
        {
            if (DriverStore.GetDriverType(driverId) != TransportType.IPC)
                return endpoint;
            // 此调试日志用于提醒调用方传入的是 ANY 地址，因此每个 Driver 的监听端口会不同
            // 处理本地 IPC 连接时需要针对这种情况进行特殊处理
            if (endpoint.Port == 0)
            {
                UnityEngine.Debug.Log($"Driver with ID {driverId} uses IPCNetworkInterface. The endpoint used for listening is using Port == 0. A random port will be assigned to this interface. In order to connect to this endpoint, you will need to retrieve the local address. You can use the NetworkStreamDriver.GetLocalEndPoint({driverId}) to retrieve the assigned address.");
            }
            if(!endpoint.IsAny && !endpoint.IsLoopback)
            {
                UnityEngine.Debug.LogWarning($"Driver with ID {driverId} uses IPCNetworkInterface. It must listen to Any:XXX or Loopback:XXX but endpoint is {endpoint.ToFixedString()}. Forcing listening to ANY:{endpoint.Port}");
                if(endpoint.Family == NetworkFamily.Ipv6)
                    return NetworkEndpoint.AnyIpv6.WithPort(endpoint.Port);
                return NetworkEndpoint.AnyIpv4.WithPort(endpoint.Port);
            }

            return endpoint;
        }

        /// <summary>
        /// 检查尝试连接的地址对指定 Driver 类型是否有效
        /// </summary>
        /// <param name="endpoint">要清洗的 Endpoint</param>
        /// <param name="driverId">要检查的 Driver</param>
        /// <returns>应传给 Connect 的地址</returns>
        /// <remarks>
        /// 此函数始终返回有效地址
        /// </remarks>
        #if UNITY_EDITOR || !UNITY_CLIENT
        private NetworkEndpoint SanitizeConnectAddress(in NetworkEndpoint endpoint, int driverId)
        {
            if (endpoint.IsLoopback)
                return endpoint;

            if (DriverStore.GetDriverType(driverId) == TransportType.IPC)
            {
                // 使用 IPC Driver 时地址必须是 Loopback，此处强制执行该约束
                UnityEngine.Debug.LogWarning(
                    $"Trying to connect to a server at address {endpoint.ToFixedString()} using an IPCNetworkInterface. IPC interfaces only support loopback address. Forcing using the NetworkEndPoint.Loopback address; family (IPV4/IPV6) and port will be preserved");
                if (endpoint.Family == NetworkFamily.Ipv4)
                    return NetworkEndpoint.LoopbackIpv4.WithPort(endpoint.Port);
                return NetworkEndpoint.LoopbackIpv6.WithPort(endpoint.Port);
            }
            return endpoint;
        }
        #endif

        /// <summary>
        /// 通知 <see cref="NetworkDriverStore"/> 中全部已注册 Driver 开始监听入站连接
        /// </summary>
        /// <param name="endpoint">要使用的本地地址，底层 Socket 会绑定到此地址</param>
        /// <returns>Driver 是否开始监听</returns>
        public bool Listen(NetworkEndpoint endpoint)
        {
            // 检查至少已经创建第一个 Driver，此条件已经足够
            if (!DriverStore.m_Driver0.IsCreated)
                throw new InvalidOperationException($"You cannot call Listen on a NetworkStreamDriver for which the DriverStore have been not created. Please ensure the NetworkDriverStore is setup before calling the Listen method.");

            // 切换到服务器模式，开始监听全部 Driver 接口
            var errors = new FixedList32Bytes<int>();
            // 可以监听指定地址和端口，但 IPC Driver 存在限制：IP 地址应为 Any 或 Loopback，且端口不能为 0
            // 由于可能存在多个 Driver，这里强制 IPC 网络接口绑定并监听 ANY:Port，
            // 如果提供了真实 IP，则绑定并监听 Loopback:Port
            // 此时绑定 Any:0 或 Loopback:0 也应视为无效
            for(int i=DriverStore.FirstDriver; i<DriverStore.LastDriver;++i)
            {
                var tempAddress = SanitizeListenAddress(endpoint, i);
                // 如果 Endpoint 无法清洗，SanitizeListenAddress 会返回无效地址
                if(!tempAddress.IsValid)
                {
                    errors.Add(i);
                    continue;
                }
                ref var driverInstance = ref DriverStore.GetDriverInstanceRW(i);
                if(driverInstance.driver.Bind(tempAddress) != 0 || driverInstance.driver.Listen() != 0)
                    errors.Add(i);
            }
            if(!errors.IsEmpty)
            {
                // Network Stream Receive System 会检测并修复不一致状态
                return false;
            }
            // FIXME 如果这不是 Driver Store 的引用会有问题，Listen 和 Connect 的状态变化也存在相同问题
            LastEndPoint = endpoint;
            return true;
        }

        /// <summary>
        /// 向远端 <paramref name="endpoint"/> 地址发起连接
        /// </summary>
        /// <param name="entityManager"><paramref name="ent"/> 等于 <see cref="Entity.Null"/> 时，用于创建新 Entity 的 EntityManager</param>
        /// <param name="endpoint">要连接的远端地址</param>
        /// <param name="ent">用于创建连接的可选 Entity，未设置时会创建新 Entity</param>
        /// <returns>持有 <see cref="NetworkStreamConnection"/> 的 Entity，Endpoint 无效时返回默认值</returns>
        /// <exception cref="InvalidOperationException">Driver 尚未创建或注册了多个 Driver 时抛出</exception>
        public Entity Connect(EntityManager entityManager, NetworkEndpoint endpoint, Entity ent = default)
        {
            if (!DriverStore.m_Driver0.IsCreated)
                throw new InvalidOperationException($"You cannot call Connect on a NetworkStreamDriver for which the DriverStore have been not created. Please ensure the NetworkDriverStore is setup before calling the Connect method.");

            var netDebugQuery = entityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetDebug>());
            var netDebug = netDebugQuery.GetSingleton<NetDebug>();

            var isIpEndpoint = endpoint.Family == NetworkFamily.Ipv4 || endpoint.Family == NetworkFamily.Ipv6;
            if (!endpoint.IsValid || (isIpEndpoint && endpoint.Port == 0))
            {
                // 无法连接任意端口，必须提供有效地址
                netDebug.LogError($"Trying to connect to the address {endpoint.ToFixedString()} that has port == 0. For connection, a port !=0 is required");
                return default;
            }

            // 仍按传入值保存最近一次连接 Endpoint
            LastEndPoint = endpoint;

            if (ent == Entity.Null)
                ent = entityManager.CreateEntity();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (DriverStore.DriversCount == 0)
                throw new InvalidOperationException("Cannot connect to the server. NetworkDriver not created");
            if (DriverStore.DriversCount != 1)
                throw new InvalidOperationException("Too many NetworkDriver created for the client. Only one NetworkDriver instance should exist");
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkSnapshotAck>();
            using var query = entityManager.CreateEntityQuery(builder);
            if (!query.IsEmpty)
                throw new InvalidOperationException("Connection to server already initiated, only one connection allowed at a time.");
#endif
#if UNITY_EDITOR || !UNITY_CLIENT
            endpoint = SanitizeConnectAddress(endpoint, DriverStore.FirstDriver);
#endif
            ref var driver = ref DriverStore.GetDriverRW(NetworkDriverStore.FirstDriverId);
            var connection = driver.Connect(endpoint);
            var state = driver.GetConnectionState(connection).ToNetcodeState(hasHandshaked: false, hasApproval: false);

            entityManager.AddComponent(ent, NetworkStreamConnection.GetEssentialComponentsForConnection());
            entityManager.AddComponentData(ent, new NetworkStreamConnection
            {
                Value = connection,
                DriverId = 1,
                CurrentState = state,
                CurrentStateDirty = true, // 最多延迟一帧触发 `Connecting` 的 `NetCodeConnectionEvent`
                                          // 使其创建与销毁时机和其他事件保持一致
            });
            if (entityManager.HasComponent<ConnectionState>(ent))
            {
                entityManager.SetComponentData(ent, new ConnectionState()
                {
                    CurrentState = state
                });
            }
            entityManager.AddComponentData(ent, new NetworkSnapshotAck());
            entityManager.AddBuffer<OutgoingRpcDataStreamBuffer>(ent);
            entityManager.AddBuffer<OutgoingCommandDataStreamBuffer>(ent);
            entityManager.AddBuffer<IncomingSnapshotDataStreamBuffer>(ent);
            entityManager.GetBuffer<LinkedEntityGroup>(ent).Add(new LinkedEntityGroup{Value = ent});
            netDebug.DebugLog($"[{entityManager.WorldUnmanaged.Name}][Connection] Connect called: Connection={connection.ToFixedString()}, State={state}.");
            return ent;
        }

        /// <summary>
        /// 远端连接地址，即此连接可见的公网 IP 地址
        /// </summary>
        /// <param name="connection">连接</param>
        /// <returns>
        /// 使用 Relay 时返回当前 Relay Host 地址，否则返回远端 Endpoint 地址
        /// </returns>
        /// <remarks>
        /// 注意，此方法与 NetworkDriver.GetRemoteEndpoint 的行为略有不同
        /// 使用 Relay 时，<see cref="NetworkDriver.GetRemoteEndpoint"/> 不一定返回有效地址，
        /// 因为连接建立后地址会变成 RelayAllocationId
        /// 此方法提供一致行为：始终返回连接当前已连接或正在连接的地址
        /// </remarks>
        public NetworkEndpoint GetRemoteEndPoint(NetworkStreamConnection connection)
        {
            // TODO 内部方法标记为 readonly 后，以只读方式获取，避免复制
            ref var driver = ref DriverStore.GetDriverRW(connection.DriverId);
            if (driver.CurrentSettings.TryGet(out RelayNetworkParameter relayParams))
                return relayParams.ServerData.Endpoint;
            return driver.GetRemoteEndpoint(connection.Value);
        }

        /// <summary>
        /// 检查指定连接是否通过 Relay 连接远端 Endpoint
        /// </summary>
        /// <param name="connection">连接</param>
        /// <returns>
        /// 连接是否正在使用 Relay
        /// </returns>
        public bool UseRelay(NetworkStreamConnection connection)
        {
            // TODO 内部方法标记为 readonly 后，以只读方式获取，避免复制
            ref var driver = ref DriverStore.GetDriverRW(connection.DriverId);
            return driver.CurrentSettings.TryGet(out RelayNetworkParameter _);
        }

        /// <summary>
        /// 获取 <see cref="NetworkDriverStore"/> 中第一个 Driver 使用的本地 Endpoint，
        /// 即远端 Peer 用于访问此 Driver 的 Endpoint
        /// 等价于以 <see cref="NetworkDriverStore.FirstDriverId">NetworkDriverStore.FirstDriverId</see>
        /// 为参数调用 <see cref="GetLocalEndPoint(int)"/>
        /// </summary>
        /// <returns>第一个 Driver 的本地 Endpoint</returns>
        public NetworkEndpoint GetLocalEndPoint()
        {
            return GetLocalEndPoint(NetworkDriverStore.FirstDriverId);
        }

        /// <summary>
        /// 获取 Driver 使用的本地 Endpoint，即远端 Peer 用于访问此 Driver 的 Endpoint
        /// <br/>
        /// 存在多个 Driver 时，例如同时使用 IPC 和 Socket 连接，<see cref="NetworkDriverStore"/> 中会有多个 Driver
        /// </summary>
        /// <param name="driverId">Driver ID，参见 <see cref="NetworkDriverStore.GetDriverRO"/></param>
        /// <returns>Driver 的本地 Endpoint</returns>
        public NetworkEndpoint GetLocalEndPoint(int driverId)
        {
            // TODO 内部方法标记为 readonly 后，以只读方式获取，避免复制
            return DriverStore.GetDriverRW(driverId).GetLocalEndpoint();
        }

        /// <summary>
        /// 底层 Transport 连接的当前状态
        /// </summary>
        /// <param name="connection">连接</param>
        /// <returns>底层 Transport 连接的当前状态</returns>
        /// <remarks>
        /// 与 <see cref="ConnectionState.State"/> 不同，粒度也更粗
        /// </remarks>
        public NetworkConnection.State GetConnectionState(NetworkStreamConnection connection)
        {
            return DriverStore.GetConnectionState(connection);
        }

        internal DriverMigrationSystem.DriverStoreState StoreMigrationState()
        {
            DriverStore.ScheduleFlushSendAllDrivers(default).Complete();
            var driverStoreState = new DriverMigrationSystem.DriverStoreState();
            driverStoreState.DriverStore = DriverStore;
            driverStoreState.LastEp = LastEndPoint;
            driverStoreState.NextId = m_NumNetworkIds.Value;
            driverStoreState.FreeList = m_FreeNetworkIds.ToArray(Allocator.Persistent);
            m_FreeNetworkIds.Clear();

            DriverState = NetworkStreamReceiveSystem.DriverState.Migrating;
            return driverStoreState;
        }

        /// <summary>
        /// 释放当前实例及关联的 <see cref="ConcurrentDriverStore"/>，以重置当前 <see cref="DriverStore"/>
        /// 可在 World 创建后、调用 <see cref="Listen"/> 或 <see cref="Connect"/> 前，
        /// 使用此方法重新创建并配置 Driver
        /// </summary>
        /// <example>
        /// <code>
        /// var driverStore = new NetworkDriverStore();
        /// var constructor = NetworkStreamReceiveSystem.DriverConstructor;
        /// constructor.CreateServerDriver(serverWorld, ref driverStore, netDebug);
        /// var driver = EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton&lt;NetworkStreamDriver&gt;();
        /// driver.ResetDriverStore(driverStore);
        /// var listenEndPoint = NetworkEndpoint.AnyIpv4.WithPort(MyPort);
        /// driver.Listen(listenEndPoint);
        /// </code>
        /// </example>
        /// <param name="world">NetworkStreamDriver Singleton 所属的 World</param>
        /// <param name="driverStore">要使用的新 Driver Store</param>
        public void ResetDriverStore(WorldUnmanaged world, ref NetworkDriverStore driverStore)
        {
            if (UnsafeUtility.AddressOf(ref driverStore) == UnsafeUtility.AddressOf(ref DriverStore))
            {
                // 尝试把同一实例赋值给自身时跳过
                // 这可以视为错误，但无法检测 NetworkDriverStore 被复制到栈上再赋值的情况
                return;
            }
            if (world.IsClient() && DriverStore.DriversCount > 1)
                throw new InvalidOperationException($"Cannot assign the NetworkDriverStore to the NetworkStreamDriver for world {world.Name}. Client must configure the driver store to use ONLY ONE network driver, but the {nameof(driverStore)} instance passed as argument has been configured to use {driverStore.DriversCount} network drivers.");

            // 如果 Driver 不是默认状态，即已经注册 Driver 且第一个接口已经创建，则可以释放当前 Driver
            // 例如服务器可以通过释放 Driver 停止监听，这实际上是停止监听的唯一方式
            // 无论如何，存在连接时都不能释放 Driver
            if (DriverStore.IsCreated)
            {
                using var connectionQuery = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                if (!connectionQuery.IsEmpty)
                    throw new InvalidOperationException($"Cannot assign the NetworkDriverStore to the NetworkStreamDriver for world {world.Name} because there are NetworkStreamConnection entities.\nPlease ensure you are setting up the drivers after you disconnected all the connections and have them properly cleanup by the NetworkStreamReceiveSystem. This will usually require at least one world update (because NetworkStreamConnection are cleanup component).");
            }

            // 始终重置当前 Driver Store，如果当前实例已销毁则此操作不产生效果
            DriverStore.Dispose();
            // 通过补充空 Driver 完成 Driver Store 初始化，无需调用 Begin，此处只负责结束 Driver 创建流程
            // 同样禁止修改现有 Driver Store，例如在 Driver 完成 Finalize 后调用 RegisterDriver
            driverStore.FinalizeDriverStore();
            DriverStore = driverStore;
            ConcurrentDriverStore = driverStore.ToConcurrent();
        }
    }
}
