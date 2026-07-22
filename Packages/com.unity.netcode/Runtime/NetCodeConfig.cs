using System;
using System.Text;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// NetCode 配置文件，使包使用者无需编写代码即可调整 NetCode 参数
    /// 可按需创建多个实例
    /// </summary>
    [CreateAssetMenu(menuName = "Multiplayer/NetCodeConfig Asset", fileName = "NetCodeConfig", order = 1)]
    public class NetCodeConfig : ScriptableObject, IComparable<NetCodeConfig>
    {
        /// <summary>
        /// 在 ProjectSettings 的 NetCode 页签中选择的默认 NetCodeConfig 资源
        /// 运行时通过 PreloadedAssets 获取，并由 <see cref="RuntimeInitializeOnLoadMethodAttribute"/> 设置
        /// </summary>
        public static NetCodeConfig Global { get; internal set; }

        /// <summary>
        /// 配置 <see cref="ClientServerBootstrap"/> 使用 <see cref="EnableAutomaticBootstrap"/> 或 <see cref="DisableAutomaticBootstrap"/>
        /// </summary>
        public enum AutomaticBootstrapSetting
        {
            /// <summary>
            /// 启用默认的 <see cref="Unity.Entities.ICustomBootstrap"/> Entities Bootstrap
            /// </summary>
            EnableAutomaticBootstrap = 1,
            /// <summary>
            /// 禁用默认的 <see cref="Unity.Entities.ICustomBootstrap"/> Entities Bootstrap
            /// </summary>
            /// <remarks>只创建 Local World，效果等同于调用 <see cref="ClientServerBootstrap.CreateLocalWorld"/></remarks>
            DisableAutomaticBootstrap = 0,
        }

        /// <summary>
        /// 使用哪一种客户端托管模式
        /// </summary>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        public enum HostWorldMode
#else
        internal enum HostWorldMode
#endif
        {
            /// <summary>
            /// 在客户端上使用本地 Client World 和 Server World 托管服务器，二者通过本地 IPC 连接通信
            /// </summary>
            BinaryWorlds = 0, // TODO 在 N4E 2.0 中改为默认使用 SingleWorld
            // TODO Unified Netcode 的 Host 启动方法应默认使用 Single World

            /// <summary>
            /// 该 World 同时充当客户端和服务端，不创建客户端到服务端的连接，只保留监听 Driver，并为使用方便生成一个虚拟连接实体
            /// </summary>
            SingleWorld = 1,
        }

        /// <summary>
        /// NetCode 辅助设置，允许向 PreloadedAssets 列表添加多个配置，但全局配置只能有一个
        /// </summary>
        public bool IsGlobalConfig;

        /// <summary>
        /// 指定游戏启动时是否触发 ClientServerBootstrap 或其派生类型
        /// 这是项目级设置，可由 OverrideAutomaticNetCodeBootstrap MonoBehaviour 覆盖
        /// </summary>
        [Header("NetCode")]
        [Tooltip("Denotes if the ClientServerBootstrap (or any derived version of it) should be triggered on game boot. Project-wide setting (when this config is applied in the Netcode tab), overridable via the OverrideAutomaticNetCodeBootstrap MonoBehaviour.")] [SerializeField]
        public AutomaticBootstrapSetting EnableClientServerBootstrap = AutomaticBootstrapSetting.EnableAutomaticBootstrap;

#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        /// <summary>
        /// 指定客户端托管服务器使用的 World 模式，Single World 模式创建同时充当客户端和服务端的 World，Binary World 模式创建通过进程内通信 IPC 连接的 Client World 与 Server World
        /// </summary>
        /// <remarks>
        /// 设置后应以该模式为前提开发整个项目，不应仅为临时测试而随意切换，并应提交到项目版本控制
        /// </remarks>
        [Tooltip("Denotes which client-hosted server world mode to use. Single world mode will create a world that acts as both client and server. Binary world mode will create a client and a server world, connected together through intra-process communication (IPC).")]
        [SerializeField]
        public HostWorldMode HostWorldModeSelection;
#else
        internal HostWorldMode HostWorldModeSelection;
#endif

        // TODO 查看 NetConfig 资源时提供打开 NetDbg 的快捷链接
        /// <inheritdoc cref="Unity.NetCode.ClientServerTickRate" path="/summary"/>
        public ClientServerTickRate ClientServerTickRate;
        /// <inheritdoc cref="Unity.NetCode.ClientTickRate"/>
        public ClientTickRate ClientTickRate;
        // TODO World 创建选项
        // TODO Thin Client 选项
        /// <inheritdoc cref="Unity.NetCode.GhostSendSystemData"/>
        public GhostSendSystemData GhostSendSystemData;
        // TODO Importance 配置
        // TODO Relevancy 配置

        // Transport 配置
        /// <inheritdoc cref="NetworkConfigParameter.connectTimeoutMS"/>
        [Tooltip("Time between connection attempts, in milliseconds.")]
        [Min(1)]
        public int ConnectTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.maxConnectAttempts"/>
        [Tooltip("Maximum number of connection attempts to try. If no answer is received from the server after this number of attempts, a <b>Disconnect</b> event is generated for the connection.")]
        [Min(1)]
        public int MaxConnectAttempts;

        /// <inheritdoc cref="NetworkConfigParameter.disconnectTimeoutMS"/>
        [Tooltip("Inactivity timeout for a connection, in milliseconds. If nothing is received on a connection for this amount of time, it is disconnected (a <b>Disconnect</b> event will be generated).\n\nTo prevent this from happening when the game session is simply quiet, set <b>heartbeatTimeoutMS</b> to a positive non-zero value.")]
        [Min(1)]
        public int DisconnectTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.heartbeatTimeoutMS"/>
        [Tooltip("Time after which if nothing from a peer is received, a heartbeat message will be sent to keep the connection alive. Prevents the <b>disconnectTimeoutMS</b> mechanism from kicking when nothing happens on a connection. A value of 0 will disable heartbeats.")]
        [Min(1)]
        public int HeartbeatTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.reconnectionTimeoutMS"/>
        [Tooltip("Time after which to attempt to re-establish a connection if nothing is received from the peer. This is used to re-establish connections for example when a peer's IP address changes (e. g. mobile roaming scenarios).\n\nTo be effective, should be less than <b>disconnectTimeoutMS</b> but greater than <b>heartbeatTimeoutMS</b>.\n\nA value of 0 will disable this functionality.")]
        [Min(1)]
        public int ReconnectionTimeoutMS;

        /// <summary>
        /// 客户端每个 Pipeline Stage 的发送队列容量
        /// 应设为客户端单次更新，即每个渲染帧，预计发送的数据包最大数量
        /// 一般建议内存充足时设为 8，否则使用满足需求的最小值，因为该值会影响 Reliable 和 Fragmentation Pipeline 吞吐量
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.sendQueueCapacity"/>
        [Tooltip(@"Capacity of the send queue (per pipeline-stage) on the client.
This should be the maximum number of packets expected to be sent by the client, per pipeline-stage, in a single update (i.e. each render frame).

Recommended value: 8 if not memory constrained, else minimum, as it can affect Reliable and Fragmentation pipeline throughput.
Default value: 512 i.e. <b>NetworkParameterConstants.SendQueueCapacity</b>")]
        [Min(4)]
        public int ClientSendQueueCapacity;

        /// <summary>
        /// 客户端每个 Pipeline Stage 的接收队列容量
        /// 应设为最坏帧情况下，例如客户端进程卡顿时，客户端预计从服务端接收的在途数据包最大数量
        /// 一般建议设为 64
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.receiveQueueCapacity"/>
        [Tooltip(@"Capacity of the receive queue (per pipeline-stage) on the client.
This should be the maximum number of in-flight packets expected to be received by the client - from the
server - during a worst-case frame (like if the client executable stalls).

Broad recommendation: 64.
Default value: 512 i.e. <b>NetworkParameterConstants.ReceiveQueueCapacity</b>")]
        [Min(8)]
        public int ClientReceiveQueueCapacity;

        /// <summary>
        /// 服务端每个 Pipeline Stage 的发送队列容量
        /// 应按一定倍数，通常为 1，覆盖服务端单次更新即每个渲染帧中，跨所有连接且按每个 Pipeline Stage 计算的预计发送数据包最大数量
        /// 一般建议 2 名玩家约为 64，100 名玩家约为 100，1000 名玩家约为 1000
        /// </summary>
        /// <example>若每台服务器最多支持 512 名玩家，且每个连接的每个 Pipeline Stage 发送 1 个数据包</example>
        /// <seealso cref="NetworkConfigParameter.sendQueueCapacity"/>
        [Tooltip(@"Capacity of the send queue (per pipeline-stage) on the server.
This should be a multiple of the maximum number of packets expected to be sent by the server, across all connections, on a per pipeline-stage basis, in a single update (i.e. each render frame).

For 2 players, ~128. For 100 players, ~512. For 1k players, ~1k.
<i>If memory constrained, use minimum, but note it can affect Reliable and Fragmentation pipeline throughput.
Default value: 512 i.e. <b>NetworkParameterConstants.SendQueueCapacity</b>")]
        [Min(16)]
        public int ServerSendQueueCapacity;

        /// <summary>
        /// 服务端每个 Pipeline Stage 的接收队列容量
        /// 应设为最坏服务端游戏循环更新中，最大支持连接数的客户端预计发往服务端并到达的在途数据包最大数量
        /// 一般建议 2 名玩家约为 64，100 名玩家约为 512，1000 名玩家约为 1200
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.receiveQueueCapacity"/>
        [Tooltip(@"Capacity of the receive queue (per pipeline-stage) on the server.
This should be the maximum number of in-flight packets - expected to be sent across by the maximum supported
number of connected clients - to the server - arriving within a worst-case server game loop update.

Broad recommendations: For 2 players, ~64. For 100 players, ~512. For 1k players, ~1.2k.
Default value: 512 i.e. <b>NetworkParameterConstants.ReceiveQueueCapacity</b>")]
        [Min(64)]
        public int ServerReceiveQueueCapacity;

        /// <inheritdoc cref="NetworkConfigParameter.maxMessageSize"/>
        [Tooltip("Maximum size of a packet that can be sent by the transport.\n\nNote that this size includes any headers that could be added by the transport (e. g. headers for DTLS or pipelines), which means the actual maximum message size that can be sent by a user is slightly less than this value.\n\nTo find out what the size of these headers is, use MaxHeaderSize(NetworkPipeline).\n\nIt is possible to send messages larger than that by sending them through a pipeline with a FragmentationPipelineStage. These headers do not include those added by the OS network stack (like UDP or IP).")]
        [Range(64, NetworkParameterConstants.AbsoluteMaxMessageSize)]
        public int MaxMessageSize;

        internal NetCodeConfig()
        {
            // 注意，ScriptableObject 原地反序列化会覆盖这些值
            Reset();
        }

        /// <summary>
        /// 设置默认值
        /// </summary>
        public void Reset()
        {
            ClientServerTickRate = default;
            ClientServerTickRate.ResolveDefaults();
            ClientServerTickRate.NetworkTickRate = 0; // 特殊情况：配置中允许该值为动态值，即 0

            ClientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            GhostSendSystemData = default;
            GhostSendSystemData.Initialize();

            ResetIfDefault(ref ConnectTimeoutMS, NetworkParameterConstants.ConnectTimeoutMS);
            ResetIfDefault(ref MaxConnectAttempts, NetworkParameterConstants.MaxConnectAttempts);
            ResetIfDefault(ref DisconnectTimeoutMS, NetworkParameterConstants.DisconnectTimeoutMS);
            ResetIfDefault(ref HeartbeatTimeoutMS, NetworkParameterConstants.HeartbeatTimeoutMS);
            ResetIfDefault(ref ReconnectionTimeoutMS, NetworkParameterConstants.ReconnectionTimeoutMS);
            ResetIfDefault(ref ClientReceiveQueueCapacity, 64);
            ResetIfDefault(ref ClientSendQueueCapacity, 64);
            ResetIfDefault(ref ServerReceiveQueueCapacity, NetworkParameterConstants.ReceiveQueueCapacity);
            ResetIfDefault(ref ServerSendQueueCapacity, NetworkParameterConstants.SendQueueCapacity);
            ResetIfDefault(ref MaxMessageSize, NetworkParameterConstants.MaxMessageSize);

            static void ResetIfDefault<T>(ref T value, T defaultValue)
                where T : IEquatable<T>
            {
                if (value.Equals(default))
                    value = defaultValue;
            }
        }

        /// <summary>
        /// 从 Resources 获取现有 NetCodeConfig，若不存在则创建一个
        /// </summary>
        /// <remarks><see cref="RuntimeInitializeLoadType.AfterAssembliesLoaded"/> 保证该方法在 Entities 初始化前调用</remarks>
        /// <returns></returns>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        internal static void RuntimeTryFindSettings()
        {
            if (Application.isEditor)
            {
                void OnQuit()
                {
                    Application.quitting -= OnQuit;
                    Global = default; // 主动重置以防关闭 Domain Reload 时沿用设置；通常下次进入 Play Mode 会重置，但若先测试运行时修改设置的项目，再运行 Editor 测试，则不会自动重置
                }

                Application.quitting += OnQuit;
            }

            var configs = Resources.FindObjectsOfTypeAll<NetCodeConfig>();
            Array.Sort(configs);
            if (configs.Length > 0)
            {
                NetCodeConfig erringConfig = default;
                var errSb = new StringBuilder($"[NetCodeConfig] Discovered {configs.Length} loaded NetcodeConfig files. Using '{configs[0].name}', but the following errors occured:");
                bool isUsingGlobalConfig = false;
                for (var i = 0; i < configs.Length; i++)
                {
                    var config = configs[i];
                    errSb.Append($"\n[{i}] '{config.name}' (global: {config.IsGlobalConfig})");
                    if (i != 0 && config.IsGlobalConfig && isUsingGlobalConfig)
                    {
                        erringConfig = config;
                        errSb.Append($"\t <-- Expected this NOT to have IsGlobalConfig set!");
                    }
                    isUsingGlobalConfig |= config.IsGlobalConfig;
                }

                if (erringConfig)
                {
                    errSb.Append("\nImplies an error during ProjectSettings selection! Please open the ProjectSettings and re-apply the NetCodeConfig!");
                    Debug.LogError(errSb, erringConfig); // 支持 Ping 资源，便于快速跳转到错误位置
                }
            }
            // 构建中可以没有全局配置，也可以包含多个 NetCodeConfig
            Global = configs.Length > 0 ? configs[0] : null;
        }

        /// <summary>
        /// 让查找结果保持确定性
        /// </summary>
        /// <param name="other"><see cref="NetCodeConfig"/> 实例</param>
        /// <returns>配置和名称的排序比较结果</returns>
        public int CompareTo(NetCodeConfig other)
        {
            if (IsGlobalConfig != other.IsGlobalConfig)
                return -IsGlobalConfig.CompareTo(other.IsGlobalConfig);
            return string.Compare(name, other.name, StringComparison.Ordinal);
        }
    }
}
