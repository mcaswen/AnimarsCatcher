# Netcode for Entities 多驱动架构

Netcode for Entities 采用多驱动架构，可以同时使用多个 [`NetworkDriver`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.NetworkDriver.html)，并将其存储在 [`NetworkDriverStore`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkDriverStore.html) 中

`NetworkDriver` 配置支持自定义，并采用委托/策略模式实现。Netcode for Entities 提供一套[默认策略实现](#default-driver-setup)。也可以创建实现 [`INetworkStreamDriverConstructor`](https://docs.unity3d.com/Packages/com.unity.netcode.adapter.utp@latest?subfolder=/api/Unity.Netcode.INetworkStreamDriverConstructor.html) 接口的自定义策略类进行替换

实现自定义初始化策略或[重置 `NetworkDriverStore`](#resetting-the-networkdriverstore-setup)的常见场景包括：

- 使用 [Unity Relay](networking-using-relay.md) 进行自托管，并允许远程玩家连接到本地计算机
- 在服务器端监听全部网络接口，同时支持 Web 和独立客户端连接
- 配置驱动使用 DTLS，参阅[安全连接示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/08_SecureConnection)

> [!NOTE]
> 以 Web 为目标平台时，必须使用 Relay 才能开始监听传入连接。但是，通过 WebSocket 将游戏连接到已部署服务器并不要求使用 Relay

<a id="networkdriverstore"></a>
## `NetworkDriverStore`

[`NetworkDriverStore`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkDriverStore.html) 结构用于存储 `NetworkDriver` 实例。默认情况下，创建 World 时由 [`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) 自动配置

`NetworkDriverStore` 最多可以使用三个驱动，每个驱动使用不同的 [`INetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.INetworkInterface.html)。虽然可以同时监听或连接不同地址，但 `NetworkDriverStore` 接口将选项限制为 Netcode for Entities 面向的常见用例：

- 服务器可以通过多个 `NetworkDriver` 监听，通常监听同一个服务器端口
- 客户端主要设计为只使用单个 `NetworkDriver` 和单条连接

<a id="default-driver-setup"></a>
## 默认驱动配置

Netcode for Entities 提供由 [`IPCAndSocketDriverConstructor`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IPCAndSocketDriverConstructor.html) 实现的默认 `NetworkDriver` 配置。每种 World 类型的驱动配置不同，并受 [PlayMode Tool](playmode-tool.md) 设置影响

<a id="default-server-configuration"></a>
### 默认服务器配置

对于服务器 World，`NetworkDriverStore` 包含多个用于监听传入连接的驱动：

| 槽位 | 接口 | 说明 |
|------|------|------|
| Slot 1 | [`IPCNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.IPCNetworkInterface.html) | 用于自托管时将本地客户端实例直接连接到服务器 |
| Slot 2 | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html)，独立平台<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html)，Web 平台 | 用于接受外部连接 |

<a id="default-client-configuration"></a>
### 默认客户端配置

对于客户端 World，`NetworkDriverStore` 始终只使用一个 `NetworkDriver`，但具体接口取决于 [PlayMode Tool](playmode-tool.md) 设置

| 模式 | Network Emulator | 接口 |
|------|------------------|------|
| Client | 开启或关闭 | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html)，独立平台<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html)，Web 平台 |
| Client/Server | 关闭 | [`IPCNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.IPCNetworkInterface.html) |
| Client/Server | 开启 | [`UDPNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.UDPNetworkInterface.html)，独立平台<br/>[`WebsocketNetworkInterface`](https://docs.unity3d.com/Packages/com.unity.transport@latest?subfolder=/api/Unity.Networking.Transport.WebSocketNetworkInterface.html)，Web 平台 |

游戏以 Client/Server 模式运行时，客户端可以通过两种不同方式连接本地服务器，具体取决于是否开启 Network Emulator

<a id="self-hosting-scenario"></a>
#### 自托管场景

Network Emulator 关闭时，Netcode for Entities 假定当前为自托管场景，并使用 `IPCNetworkInterface` 连接服务器。使用该接口时，客户端会优化预测循环，确保：

- 每帧最多执行一个预测 Tick
- 为了时间同步，假定数据包丢失、抖动和往返时间 RTT 均为 0

<a id="client-connection-emulation"></a>
#### 客户端连接模拟

Network Emulator 开启时，Netcode for Entities 会将客户端驱动配置为使用 `WebsocketNetworkInterface`，模拟客户端连接远程服务器，即使服务器实际位于同一台计算机上。该模式主要用于测试网络条件

<a id="customize-network-driver-creation"></a>
## 自定义网络驱动创建

可以创建实现 `INetworkStreamDriverConstructor` 的类，自定义创建 World 时如何设置 `NetworkDriverStore`

`DefaultDriverBuilder` 类提供一组辅助方法，帮助创建和初始化 Netcode for Entities 所需的驱动

```csharp
public class MyCustomDriverConstructor : INetworkStreamDriverConstructor
{
    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
#if !UNITY_WEBGL
        DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
        DefaultDriverBuilder.RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
    }

    public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkServerSettings();
#if !UNITY_WEBGL
        DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
        DefaultDriverBuilder.RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
    }
}
```

若要使用自定义策略，必须在创建 World 前，将自定义类的新实例赋给静态属性 `NetworkStreamReceiveSystem.DriverConstructor`

```csharp
var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
// 使用 try/finally，确保即使出现异常也能恢复原默认构造器
try
{
    NetworkStreamReceiveSystem.DriverConstructor = new MyCustomDriverConstructor();
    var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
    var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
}
finally
{
    NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
}
```

> [!NOTE]
> `DefaultDriverConstructor.RegisterServerDriver` 和 `DefaultDriverConstructor.RegisterClientDriver` 分别实现服务器与客户端的默认策略。特别是客户端策略，它会根据 Network Emulator 设置通过 IPC 或 Socket 连接。创建自定义驱动构造器时，建议始终使用面向具体接口的 Builder 方法，例如 `RegisterServerUdpDriver`，从而精确控制所需接口

<a id="resetting-the-networkdriverstore-setup"></a>
## 重置 `NetworkDriverStore` 配置

创建 World 后，可以使用 `NetworkStreamDriver.ResetDriverStore` 方法修改当前 `NetworkDriverStore` 的驱动配置

必须创建新的 `NetworkDriverStore` 实例，手动配置后将其作为参数传给重置方法

> [!NOTE]
> 存在活动连接时不能重置驱动。只有 World 中不存在 `NetworkStreamConnection` 时才能重置

在某些情况下，重置 `NetworkDriverStore` 比使用自定义驱动构造器更合适。常见用例之一是连接或监听需要异步设置，例如[使用 Relay](networking-using-relay.md)时

另一个常见场景是使用[瘦客户端](thin-clients.md)并且需要异步连接逻辑。Play Mode Tool 原生不支持这种情况，因为它要求同步设置连接

<a id="reset-the-driver-store"></a>
### 重置驱动存储

可以通过多种方式重置驱动存储

- 使用 `INetworkDriverConstructor` 实例委托驱动创建

```csharp
var driverStore = new NetworkDriverStore();
var clientWorld = ClientServerBootstrap.ClientWorld;
var netDebug = clientWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
// 可以使用任意构造器初始化存储
NetworkStreamReceiveSystem.DriverConstructor.CreateClientDriver(clientWorld, ref driverStore, netDebug);
var networkStreamDriver = clientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
networkStreamDriver.ResetDriverStore(clientWorld, ref driverStore);
```

- 直接手动填充驱动

```csharp
var driverStore = new NetworkDriverStore();
var serverWorld = ClientServerBootstrap.ServerWorld;
var settings = DefaultDriverBuilder.GetNetworkServerSettings();
var netDebug = serverWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
// 注册所需驱动
DefaultDriverBuilder.RegisterServerIpcDriver(serverWorld, ref driverStore, netDebug, settings);
DefaultDriverBuilder.RegisterServerUdpDriver(serverWorld, ref driverStore, netDebug, settings);
// 重置
var networkStreamDriver = serverWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
networkStreamDriver.ResetDriverStore(serverWorld, ref driverStore);
```

## 其他资源

* [在 Netcode for Entities 中使用 Unity Relay](networking-using-relay.md)
