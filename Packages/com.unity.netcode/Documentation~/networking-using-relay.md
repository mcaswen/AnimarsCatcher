# 在 Netcode for Entities 中使用 Unity Relay

采用没有专用服务器部署的自托管架构时，需要使用 Unity Relay 连接玩家

Netcode for Entities 的默认驱动配置不会直接使用 Relay 建立连接。在这种情况下，用户需要自行正确配置 `NetworkDriverStore`

本页假定读者已经熟悉 Relay，并已获取必要数据。详细信息和代码示例请参阅 [Relay 文档](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)

<a id="configure-networkdriverstore-to-use-relay"></a>
## 配置 `NetworkDriverStore` 以使用 Relay

可以通过以下两种方式配置 `NetworkDriverStore` 使用 Relay：

* 使用[自定义驱动构造器](networking-network-drivers.md#customize-network-driver-creation)
    * Netcode for Entities 的 [Relay 示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01b_RelaySupport)展示了这种配置方式
* 创建 World 后[重置驱动存储](networking-network-drivers.md#reset-the-networkdriverstore-setup)

使用第一种方式时，必须在创建 World 前完成 Relay 服务的设置与连接，并取得分配和对应加入代码

建议在创建客户端和服务器 World 前，先建立并配置全部服务连接，并完成不要求实时连接的其他服务操作，原因如下：

* 工作流的上下文更加集中
* 出现错误时，需要创建和销毁的 World 更少

不过，实际使用场景可能有所不同，本包并未施加严格限制。客户端和服务器 World 可以随时创建和销毁

<a id="set-up-the-driver-using-a-custom-inetworkdriverconstructor"></a>
## 使用自定义 `INetworkDriverConstructor` 设置驱动

可以创建一个简单的驱动构造器，使用 Relay 数据初始化 `NetworkSettings`，再将它传给 `NetworkStreamReceiveSystem.DriverConstructor`

以下示例展示如何设置驱动，使其同时支持本地 IPC 连接和 Relay 连接。IPC 用于自托管，Relay 用于通过 Relay 连接远程或本地服务器

```csharp
/// <summary>
/// 使用 Relay 服务器设置注册客户端和服务器
/// 对客户端而言，如果未设置 Relay 并且模式为 Client/Server，系统会尝试使用 IPCNetworkInterface 设置驱动
/// </summary>
public class RelayDriverConstructor : INetworkStreamDriverConstructor
{
    RelayServerData m_RelayClientData;
    RelayServerData m_RelayServerData;

    public RelayDriverConstructor(RelayServerData serverData, RelayServerData clientData)
    {
        m_RelayServerData = serverData;
        m_RelayClientData = clientData;
    }

    /// <summary>
    /// 根据 Relay 设置注册不同类型的驱动
    /// <para>
    /// 模式          | Relay 设置
    /// Client/Server | 有效 -> 使用 Relay 连接本地服务器
    ///                 无效 -> 使用 IPC 连接本地服务器
    /// Client        | 始终使用 Relay，数据必须有效，否则 Transport 会抛出异常
    /// </para>
    /// <para>
    /// 对于 WebGL，编辑器中的客户端始终优先使用 WebSocket，以尽可能还原 Player 行为
    /// </para>
    /// </summary>
    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
        // Relay 数据无效时通过本地 IPC 连接
        if (ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.ClientAndServer &&
            !m_RelayClientData.Endpoint.IsValid)
        {
            DefaultDriverBuilder.RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
        }
        else
        {
            settings.WithRelayParameters(ref m_RelayClientData);
#if !UNITY_WEBGL
            DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
            DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
        }
    }

    public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        // 第一个驱动是 IPC，在需要时用于进程内客户端与服务器连接
        // IPC 无法使用 Relay，必须在不提供 Relay 数据的情况下设置
        var ipcSettings = DefaultDriverBuilder.GetNetworkServerSettings();
        DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driverStore, netDebug, ipcSettings);
        var relaySettings = DefaultDriverBuilder.GetNetworkServerSettings();
        // 另一个驱动仍使用同一端口，通过 Relay 监听外部连接
        relaySettings.WithRelayParameters(ref m_RelayServerData);
#if !UNITY_WEBGL
        DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, relaySettings);
#else
        DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, relaySettings);
#endif
    }
}
```

<a id="set-up-the-driver-using-networkstreamdriverreset"></a>
## 使用 `NetworkStreamDriverReset` 设置驱动

重置驱动存储与前一个示例非常相似，唯一的区别是可以在创建 World 后执行初始化

```csharp
public void SetupClientWorld(World world, in RelayData relay)
{
    // 此处假定需要强制使用 Relay
    var settings = DefaultDriverBuilder.GetNetworkClientSettings();
    settings.WithRelayParameters(ref m_RelayClientData);
    var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
    var driverStore = new NetworkDriverStore();
    DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
    var networkStreamDriver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
    networkStreamDriver.ResetDriverStore(world, ref driverStore);
}

public void SetupServerWorld(World world, in RelayData relay)
{
    var driverStore = new NetworkDriverStore();
    var netDebug = world.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
    var ipcSettings = DefaultDriverBuilder.GetNetworkServerSettings();
    DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driverStore, netDebug, ipcSettings);
    var relaySettings = DefaultDriverBuilder.GetNetworkServerSettings();
    // 另一个驱动仍使用同一端口，通过 Relay 监听外部连接
    relaySettings.WithRelayParameters(ref m_RelayServerData);
#if !UNITY_WEBGL
    DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, relaySettings);
#else
    DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, relaySettings);
#endif
    var networkStreamDriver = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
    networkStreamDriver.ResetDriverStore(world, ref driverStore);
}
```
