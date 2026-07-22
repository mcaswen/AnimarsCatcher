using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
#if ENABLE_MANAGED_UNITYTLS
using Unity.Networking.Transport.TLS;
#endif
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;


namespace Unity.NetCode
{
    /// <summary>
    /// 用于构造 <see cref="NetworkDriverStore.NetworkDriverInstance"/> 和默认 <see cref="NetworkSettings"/>，
    /// 并将其注册到 <see cref="NetworkDriverStore"/> 的默认辅助方法实现
    /// </summary>
    public static class DefaultDriverBuilder
    {
        internal const int DefaultPayloadCapacity = 16 * 1024;
        const int MaxFrameTimeMS = 100;
        const int DefaultWindowSize = 32;

        /// <summary>
        /// 返回 <see cref="IPCAndSocketDriverConstructor"/> 构造器实例
        /// </summary>
        public static INetworkStreamDriverConstructor DefaultDriverConstructor => new IPCAndSocketDriverConstructor();

        /// <inheritdoc cref="GetNetworkClientSettings"/>
        //[Obsolete("Renamed `GetNetworkClientSettings` (RemovedAfter 2.0). (UnityUpgradable) -> GetNetworkClientSettings(*)", false)]
        public static NetworkSettings GetNetworkSettings() => GetNetworkClientSettings();

        /// <summary>
        /// 返回一组 Client World 默认设置，其中会使用 PlayMode Tools 配置的 NetworkSimulator 参数
        /// </summary>
        /// <returns>新的 <see cref="NetworkSettings"/> 实例</returns>
        public static NetworkSettings GetNetworkClientSettings()
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: DefaultWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DefaultPayloadCapacity);

            AddNetcodePackageNetworkConfigParameters(ref settings, isServer:false);
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                NetworkSimulatorSettings.SetSimulatorSettings(ref settings);
            }
#endif
            return settings;
        }

        /// <inheritdoc cref="GetNetworkServerSettings()"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> GetNetworkServerSettings(*)", false)]
        public static NetworkSettings GetNetworkServerSettings(int playerCount = 0)
        {
            return GetNetworkServerSettings();
        }

        /// <summary>
        /// 返回一组内部默认设置，其中会使用 PlayMode Tools 配置的 NetworkSimulator 参数
        /// </summary>
        /// <returns>描述网络配置的参数</returns>
        public static NetworkSettings GetNetworkServerSettings()
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: DefaultWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DefaultPayloadCapacity);
            AddNetcodePackageNetworkConfigParameters(ref settings, isServer:true);
            return settings;
        }

        /// <summary>
        /// 辅助方法：把 NetCode 包专用的全部 <see cref="NetCodeConfig.Global"/> 设置
        /// 添加到 <see cref="NetworkConfigParameter"/> 结构体
        /// </summary>
        /// <param name="settings">要注入配置的设置</param>
        /// <param name="isServer">是否使用 Server World 设置</param>
        public static void AddNetcodePackageNetworkConfigParameters(ref NetworkSettings settings, bool isServer)
        {
            var config = NetCodeConfig.Global;
            // 尚未添加配置时强制获取默认值
            if (!settings.TryGet(out NetworkConfigParameter ncp))
                ncp = settings.GetNetworkConfigParameters();
            if (config)
            {
                ncp.connectTimeoutMS = config.ConnectTimeoutMS;
                ncp.maxConnectAttempts = config.MaxConnectAttempts;
                ncp.disconnectTimeoutMS = config.DisconnectTimeoutMS;
                ncp.heartbeatTimeoutMS = config.HeartbeatTimeoutMS;
                ncp.reconnectionTimeoutMS = config.ReconnectionTimeoutMS;
                ncp.maxMessageSize = config.MaxMessageSize;
                ncp.receiveQueueCapacity = isServer ? config.ServerReceiveQueueCapacity : config.ClientReceiveQueueCapacity;
                ncp.sendQueueCapacity = isServer ? config.ServerSendQueueCapacity : config.ClientSendQueueCapacity;
            }

            // 使用此方法而不是直接传入原始结构体，因为 UTP 新增字段后此构造方式会自动采用，而原始结构体不会
            settings.WithNetworkConfigParameters(
#if UNITY_EDITOR || NETCODE_DEBUG
                maxFrameTimeMS: MaxFrameTimeMS,
#endif
                connectTimeoutMS: ncp.connectTimeoutMS,
                maxConnectAttempts: ncp.maxConnectAttempts,
                disconnectTimeoutMS: ncp.disconnectTimeoutMS,
                heartbeatTimeoutMS: ncp.heartbeatTimeoutMS,
                reconnectionTimeoutMS: ncp.reconnectionTimeoutMS,
                maxMessageSize: ncp.maxMessageSize,
                receiveQueueCapacity: ncp.receiveQueueCapacity,
                sendQueueCapacity: ncp.sendQueueCapacity
            );
        }

        /// <summary>
        /// 创建适用于客户端的 NetworkDriver 的辅助方法
        /// Driver 使用指定的 <paramref name="netIf"/>，并采用内部默认值配置
        /// 参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <typeparam name="T">要使用的 <see cref="INetworkInterface"/> 类型</typeparam>
        /// <param name="netIf">用于创建 Driver 的 <see cref="INetworkInterface"/> 实例</param>
        /// <returns>新的 <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateClientNetworkDriver<T>(T netIf) where T : unmanaged, INetworkInterface
        {
            return CreateClientNetworkDriver(netIf, GetNetworkClientSettings());
        }

        /// <summary>
        /// 创建适用于客户端的 NetworkDriver 的辅助方法
        /// Driver 使用指定的 <see cref="INetworkInterface"/>，并采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <typeparam name="T">要使用的 <see cref="INetworkInterface"/> 类型</typeparam>
        /// <param name="netIf">用于创建 Driver 的 <see cref="INetworkInterface"/> 实例</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <returns>新的 <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateClientNetworkDriver<T>(T netIf, NetworkSettings settings) where T : unmanaged, INetworkInterface
        {
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                driverInstance.driver = NetworkDriver.Create(netIf, settings);
                CreateClientSimulatorPipelines(ref driverInstance);
            }
            else
#endif
            {
                driverInstance.driver = NetworkDriver.Create(netIf, settings);
                CreateClientPipelines(ref driverInstance);
            }
            return driverInstance;
        }

        /// <inheritdoc cref="CreateServerNetworkDriver{T}(T)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> CreateServerNetworkDriver<T>(*)", false)]
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf, int playerCount = 0) where T : unmanaged, INetworkInterface
        {
            return CreateServerNetworkDriver(netIf);
        }

        /// <summary>
        /// 使用指定 <paramref name="netIf"/> 创建 Server NetworkDriver 的辅助方法
        /// Driver 采用内部默认值配置，参见 <see cref="GetNetworkServerSettings"/>
        /// </summary>
        /// <typeparam name="T">要使用的 <see cref="INetworkInterface"/> 类型</typeparam>
        /// <param name="netIf">用于创建 Driver 的 <see cref="INetworkInterface"/> 实例</param>
        /// <returns>新的 <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf) where T : unmanaged, INetworkInterface
        {
            return CreateServerNetworkDriver(netIf, GetNetworkServerSettings());
        }

        /// <summary>
        /// 使用指定 <paramref name="netIf"/> 创建 Server NetworkDriver 的辅助方法
        /// Driver 采用 <paramref name="settings"/> 配置
        /// </summary>
        /// <typeparam name="T">要使用的 <see cref="INetworkInterface"/> 类型</typeparam>
        /// <param name="netIf">用于创建 Driver 的 <see cref="INetworkInterface"/> 实例</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <returns>新的 <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf, NetworkSettings settings) where T : unmanaged, INetworkInterface
        {
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance
            {
                driver = NetworkDriver.Create(netIf, settings)
            };
            CreateServerPipelines(ref driverInstance);

            return driverInstance;
        }

        /// <summary>
        /// 判断 Client World 应优先使用基于 Socket 的网络接口（UDP 或 WebSocket），
        /// 还是 <see cref="IPCNetworkInterface"/> 的辅助方法
        /// 仅当 <see cref="ClientServerBootstrap.RequestedPlayType"/> 设为客户端/服务器模式、
        /// 当前进程中存在 Server World，且编辑器或开发构建中的 <see cref="NetworkSimulatorSettings"/> 已禁用时，才优先使用 IPC 连接
        /// </summary>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <returns>Client World 应使用实现 Socket 接口的 NetworkDriver 时返回 true</returns>
        /// <remarks>不应使用此方法配置 Server Driver；在服务器构建中，此方法始终返回 true</remarks>
        public static bool ClientUseSocketDriver(NetDebug netDebug)
        {
#if !UNITY_CLIENT
#if UNITY_EDITOR || NETCODE_DEBUG
            // 启用模拟器时始终强制使用 Socket，虽然 IPC 同样可用，但 Socket 是首选
            if (NetworkSimulatorSettings.Enabled)
            {
                netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] Network simulator enabled. Forcing client to use a socket network driver, rather than an IPC.");
                return true;
            }
#endif
            // 定义 UNITY_CLIENT 时始终设置客户端 PlayMode
            if (ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Client)
            {
                return true;
            }
            netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] RequestedPlayType is ClientAndServer Or Server, so looking for a server world instance in the same process.");
            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
            {
                netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] Found server world instance. Thus, preferring IPC network interface.");
                return false;
            }
#endif
            return true;
        }

        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册使用以下任一种网络接口的 NetworkDriver 实例：
        /// <list type="bullet">
        /// <item>客户端与 Server World 同处一个进程时，使用单个 IPCNetworkInterface NetworkDriver</item>
        /// <item>目标为独立平台时，使用单个 UDPNetworkInterface Driver</item>
        /// <item>目标为 WebGL 时，使用单个 WebSocketNetworkInterface</item>
        /// </list>
        /// 这些 Driver 采用内部默认值配置，参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            RegisterClientDriver(world, ref driverStore, netDebug, GetNetworkClientSettings());
        }

        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册使用以下任一种网络接口的 NetworkDriver 实例：
        /// <list type="bullet">
        /// <item>客户端与 Server World 同处一个进程时，使用单个 IPCNetworkInterface NetworkDriver</item>
        /// <item>目标为独立平台时，使用单个 UDPNetworkInterface Driver</item>
        /// <item>目标为 WebGL 时，使用单个 WebSocketNetworkInterface</item>
        /// </list>
        /// 这些 Driver 采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            if (ClientUseSocketDriver(netDebug))
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
                RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
            }
            else
            {
                RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
            }
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="UDPNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        public static void RegisterClientUdpDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterClientUdpDriver] Creating the client default UDP socket network interface driver.");
            var driverInstance = DefaultDriverBuilder.CreateClientNetworkDriver(new UDPNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }
#endif
        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="WebSocketNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// 构造出的 Driver 不使用 Reliable Pipeline Stage，因为 WebSocket 本身已经可靠，
        /// 且 <see cref="NetworkDriverStore.NetworkDriverInstance.reliablePipeline"/> 实例为 <see cref="NullPipelineStage"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        public static void RegisterClientWebSocketDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                driverInstance.driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
                // WebSocket 不需要 Reliable Pipeline，严格来说也不需要 Fragmented Stage
                // 但为了兼容与非 WebGL Player 的跨平台连接，仍需保留它们
                CreateClientSimulatorPipelines(ref driverInstance);
            }
            else
#endif
            {
                driverInstance.driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
                // WebSocket 不需要 Reliable Pipeline，严格来说也不需要 Fragmented Stage
                // 但为了兼容与非 WebGL Player 的跨平台连接，仍需保留它们
                CreateClientPipelines(ref driverInstance);
            }
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }
        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="IPCNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        public static void RegisterClientIpcDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterClientIpcDriver] Creating the client default IPC network interface driver.");
            var driverInstance = DefaultDriverBuilder.CreateClientNetworkDriver(new IPCNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.IPC, driverInstance);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug);
        }

        /// <summary>
        /// 向 <paramref name="driverStore"/> 注册多个使用不同 <see cref="INetworkInterface"/> 的 NetworkDriver 实例：
        /// <list type="bullet">
        /// <item>`ClientServerBootstrap.RequestedPlayType` 为客户端/服务器模式时，注册一个使用 `IPCNetworkInterface` 的 Driver</item>
        /// <item>当前构建目标是独立平台（非 WebGL）或 Dedicated Server 时，注册一个使用 `UDPNetworkInterface` 的 Driver</item>
        /// <item>当前构建目标是 WebGL 时，注册一个使用 `WebSocketNetworkInterface` 的 Driver</item>
        /// </list>
        /// 这些 Driver 采用内部默认值配置，参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, GetNetworkServerSettings());
        }

        /// <summary>
        /// 向 <paramref name="driverStore"/> 注册多个 NetworkDriver 实例：<br/>
        /// <list type="bullet">
        /// <item>`ClientServerBootstrap.RequestedPlayType` 为客户端/服务器模式时，注册一个使用 `IPCNetworkInterface` 的 Driver</item>
        /// <item>当前构建目标是独立平台（非 WebGL）或 Dedicated Server 时，注册一个使用 `UDPNetworkInterface` 的 Driver</item>
        /// <item>当前构建目标是 WebGL 时，注册一个使用 `WebSocketNetworkInterface` 的 Driver</item>
        /// </list>
        /// 这些 Driver 采用传入的 <paramref name="settings">NetworkSettings</paramref> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            RegisterServerIpcDriver(world, ref driverStore, netDebug, settings);
#if !UNITY_WEBGL || UNITY_EDITOR
            RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
            RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
        }

        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="IPCNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerIpcDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerIpcDriver] Creating the server default IPC network interface driver.");
            var ipcDriver = CreateServerNetworkDriver(new IPCNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.IPC, ipcDriver);
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="UDPNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerUdpDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerUdpDriver] Creating the server default socket network interface driver.");
            var socketDriver = CreateServerNetworkDriver(new UDPNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.Socket, socketDriver);
        }
#endif

        /// <summary>
        /// 在 <paramref name="driverStore"/> 中注册 <see cref="WebSocketNetworkInterface"/> NetworkDriver 实例
        /// 该实例采用传入的 <paramref name="settings"/> 配置
        /// 构造出的 Driver 不使用 Reliable Pipeline Stage，因为 WebSocket 本身已经可靠，
        /// 且 <see cref="NetworkDriverStore.NetworkDriverInstance.reliablePipeline"/> 实例为 <see cref="NullPipelineStage"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="settings">描述网络配置的参数列表</param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerWebSocketDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            NetworkSettings settings)
        {
            Assert.IsTrue(ClientServerBootstrap.RequestedPlayType != ClientServerBootstrap.PlayType.Client);
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerWebSocketDriver] Creating the server WebSocket network interface driver.");
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance
            {
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings)
            };
            // WebSocket 不需要 Reliable Pipeline，严格来说也不需要 Fragmented Stage
            // 但为了兼容与非 WebGL Player 的跨平台连接，仍需保留它们
            CreateServerPipelines(ref driverInstance);
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }

        /// <summary>
        /// 为客户端创建默认网络 Pipeline，包括 Reliable、Unreliable 和 Unreliable Fragmented
        /// </summary>
        /// <param name="driverInstance">要配置的 <see cref="NetworkDriverStore.NetworkDriverInstance"/> 实例</param>
        public static void CreateClientPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(typeof(NullPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(typeof(FragmentationPipelineStage));
        }

        /// <summary>
        /// 为服务器创建默认网络 Pipeline，包括 Reliable、Unreliable 和 Unreliable Fragmented
        /// </summary>
        /// <param name="driverInstance">要配置的 <see cref="NetworkDriverStore.NetworkDriverInstance"/> 实例</param>
        public static void CreateServerPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(typeof(NullPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(typeof(FragmentationPipelineStage));
        }

#if UNITY_EDITOR || NETCODE_DEBUG || UNITY_INCLUDE_TESTS
        /// <summary>
        /// 仅用于配置 Client Driver，创建支持 Network Simulator 的网络 Pipeline，
        /// 包括 Reliable、Unreliable 和 Unreliable Fragmented
        /// </summary>
        /// <param name="driverInstance"></param>
        public static void CreateClientSimulatorPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(
                typeof(SimulatorPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(
                typeof(ReliableSequencedPipelineStage),
                typeof(SimulatorPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(SimulatorPipelineStage));
        }
#endif
#if ENABLE_MANAGED_UNITYTLS
        /// <summary>
        /// 注册 NetworkDriver 实例并保存到 <paramref name="driverStore"/>：<br/>
        ///     - Client World 与 Server World 同处一个进程时，使用单个 <see cref="IPCNetworkInterface"/> NetworkDriver<br/>
        ///     - 其他情况使用单个 <see cref="UDPNetworkInterface"/> Driver<br/>
        /// 这些 Driver 采用默认设置配置，参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="caCertificate">已签名的服务器证书</param>
        /// <param name="serverName">服务器证书中的通用名称</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref FixedString4096Bytes caCertificate, ref FixedString512Bytes serverName)
        {
            var settings = GetNetworkClientSettings();
            settings = settings.WithSecureClientParameters(caCertificate: ref caCertificate, serverName: ref serverName);
            RegisterClientDriver(world, ref driverStore, netDebug, settings);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug, ref FixedString4096Bytes, ref FixedString4096Bytes)"/>
        //[Obsolete("Removed default parameter `GetNetworkClientSettings` (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            ref FixedString4096Bytes certificate, ref FixedString4096Bytes privateKey, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, ref certificate, ref privateKey);
        }

        /// <summary>
        /// 向 <paramref name="driverStore"/> 注册多个 NetworkDriver 实例：<br/>
        /// <list type="bullet">
        /// <item>ClientServerBootstrap.RequestedPlayType 为客户端/服务器模式时，注册一个使用 IPCNetworkInterface 的 Driver</item>
        /// <item>除 WebGL 外的所有目标注册一个使用 UDPNetworkInterface 的 Driver；WebGL 和编辑器中注册一个使用 WebSocketNetworkInterface 的 Driver</item>
        /// </list>
        /// 这些 Driver 采用默认设置配置，参见 <see cref="GetNetworkServerSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="certificate"></param>
        /// <param name="privateKey"></param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref FixedString4096Bytes certificate, ref FixedString4096Bytes privateKey)
        {
            var settings = GetNetworkServerSettings();
            settings = settings.WithSecureServerParameters(certificate: ref certificate, privateKey: ref privateKey);
            RegisterServerDriver(world, ref driverStore, netDebug, settings);
        }
#endif
        /// <summary>
        /// 注册 NetworkDriver 实例并保存到 <paramref name="driverStore"/>：<br/>
        ///     - Client World 与 Server World 同处一个进程时，使用单个 <see cref="IPCNetworkInterface"/> NetworkDriver<br/>
        ///     - 其他情况使用单个 <see cref="UDPNetworkInterface"/> Driver<br/>
        /// 这些 Driver 采用默认设置配置，参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="relayData">通过 Relay Server 建立连接所需的服务器信息</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData)
        {
            var settings = GetNetworkClientSettings();
            if (ClientUseSocketDriver(netDebug))
            {
                settings = settings.WithRelayParameters(ref relayData);
            }
            RegisterClientDriver(world, ref driverStore, netDebug, settings);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug, ref RelayServerData)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, ref relayData);
        }

        /// <summary>
        /// 向 <paramref name="driverStore"/> 注册多个使用不同 <see cref="INetworkInterface"/> 的 NetworkDriver 实例：
        /// <list type="bullet">
        /// <item>ClientServerBootstrap.RequestedPlayType 为客户端/服务器模式时，注册一个使用 IPCNetworkInterface 的 Driver</item>
        /// <item>当前构建目标是独立平台（非 WebGL）或 Dedicated Server 时，注册一个使用 UDPNetworkInterface 的 Driver</item>
        /// <item>当前构建目标是 WebGL 时，注册一个使用 WebSocketNetworkInterface 的 Driver</item>
        /// </list>
        /// 这些 Driver 采用内部默认值配置，参见 <see cref="GetNetworkClientSettings"/>
        /// </summary>
        /// <param name="world">用于判断当前运行在 Client World 还是 Server World</param>
        /// <param name="driverStore">NetworkDriver 存储</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        /// <param name="relayData">通过 Relay Server 建立连接所需的服务器信息</param>
        /// <remarks>WebGL 构建不可用，编辑器中始终可用</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData)
        {
            var settings = GetNetworkServerSettings();
            RegisterServerIpcDriver(world, ref driverStore, netDebug, settings);
            settings = settings.WithRelayParameters(ref relayData);
#if !UNITY_WEBGL || UNITY_EDITOR
            RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#endif
        }
    }

    /// <summary>
    /// 默认 NetCode Driver 构造器，根据当前 <see cref="ClientServerBootstrap.RequestedPlayType"/> 和平台，
    /// 初始化 Server World 使用多个 <see cref="INetworkInterface"/>，Client World 使用单个 <see cref="INetworkInterface"/>
    /// 具体规则如下：
    /// - 服务器：编辑器中同时使用 <see cref="IPCNetworkInterface"/> 和 <see cref="UDPNetworkInterface"/> NetworkDriver，
    ///   构建中仅使用单个 <see cref="UDPNetworkInterface"/> Driver<br/>
    /// - 客户端：<br/>
    ///     - Client World 与 Server World 同处一个进程时，使用单个 <see cref="IPCNetworkInterface"/> NetworkDriver<br/>
    ///     - 其他情况使用单个 <see cref="UDPNetworkInterface"/> Driver<br/>
    /// 在编辑器和开发构建中，如果启用 Network Simulator，则强制客户端使用 <see cref="UDPNetworkInterface"/> NetworkDriver
    /// <b>要让客户端在客户端/服务器模式下使用 IPC 网络接口，必须先创建 Server World，
    /// 即先对其调用 `NetworkStreamDriver.Listen`，再尝试连接</b>
    /// </summary>
    public struct IPCAndSocketDriverConstructor : INetworkStreamDriverConstructor
    {
        /// <summary>
        /// 创建适用于客户端连接服务器的新 <see cref="NetworkDriver"/>，并注册到目标 <see cref="NetworkDriverStore"/>
        /// NetworkDriver 实例会根据 <see cref="ClientServerBootstrap.RequestedPlayType"/> 以及同一进程中是否存在 Server 实例，
        /// 选择使用 Socket 或 IPC 网络接口<br/>
        /// WebGL 构建中的客户端默认使用 <see cref="WebSocketNetworkInterface"/>
        /// </summary>
        /// <param name="world">创建 Driver 的目标 World</param>
        /// <param name="driverStore">注册 Driver 的 <see cref="NetworkDriverStore"/> 实例</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkClientSettings());
        }

        /// <summary>
        /// 创建一个或多个可监听入站连接的 NetworkDriver，并注册到目标 <see cref="NetworkDriverStore"/>
        /// 默认始终创建一个使用 Socket 网络接口的 <see cref="NetworkDriver"/>
        /// 在 WebGL 构建中，服务器使用 <see cref="WebSocketNetworkInterface"/> 与客户端通信<br/>
        /// 在编辑器或客户端/服务器 Player 构建中，如果 <see cref="ClientServerBootstrap.RequestedPlayType"/> 设为
        /// <see cref="ClientServerBootstrap.PlayType.ClientAndServer"/>，还会创建第二个使用 IPC 网络接口的 <see cref="NetworkDriver"/>，
        /// 用于尽量降低同一进程内客户端连接的延迟
        /// </summary>
        /// <param name="world">创建 Driver 的目标 World</param>
        /// <param name="driverStore">注册 Driver 的 <see cref="NetworkDriverStore"/> 实例</param>
        /// <param name="netDebug">用于记录错误和调试信息的 <see cref="netDebug"/> Singleton</param>
        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
#if UNITY_EDITOR || !UNITY_WEBGL
            DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkServerSettings());
#else
            throw new NotSupportedException(
                "It is not valid to use the `IPCAndSocketDriverConstructor` as default constructor for configure the Server NetworkDriverStore for WebGL build.\n" +
                "For self-hosting scenario (client/server mode) using WebGL player, in order to be able to listen for incoming connections you need to use Unity.Relay. Therefore,\n" +
                "you must create a custom INetworkStreamDriverConstructor implementation that will setup the server driver using NetworkSettings that include the necessary relay data.\n" +
                "Please refer to the Netcode For Entities documentation, in particular `Configure NetworkDriverStore to use Unity.Relay` section for more details about how to do it.");
#endif
        }
    }
}
