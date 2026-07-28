# 连接服务器与客户端

Netcode for Entities 使用 [Unity Transport 包](https://docs.unity3d.com/Packages/com.unity.transport@latest)管理连接，并将每条连接保存为实体。每个连接实体都有一个 [`NetworkStreamConnection`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) 组件，其中保存该连接的 Transport 句柄。无论是服务器断开用户，还是客户端主动请求断开，连接关闭后都会销毁该实体

不使用 [`AutoCommandTarget`](command-stream.md#automatically-handling-commands-autocommandtarget)，或者需要更精细地手动控制时，每条连接上的 [`CommandTarget`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTarget.html) 必须指向用于保存所接收玩家命令的实体。游戏代码负责持续维护该实体引用

游戏可以在连接上添加 `NetworkStreamInGame` 组件，将其标记为 InGame。该操作不会自动发生，必须由游戏主动完成。在连接获得 `NetworkStreamInGame` 之前，客户端不会发送命令，服务器也不会发送快照

要请求断开连接，应在连接实体上添加 `NetworkStreamRequestDisconnect` 组件。不支持通过 Driver 直接断开

<a id="incoming-buffers"></a>

### 接收缓冲区

每条连接最多有三个接收缓冲区，分别对应命令、RPC 和快照三类数据流，其中快照缓冲区仅存在于客户端：

- [`IncomingRpcDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingRpcDataStreamBuffer.html)
- [`IncomingCommandDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingCommandDataStreamBuffer.html)
- [`IncomingSnapshotDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.IncomingSnapshotDataStreamBuffer.html)

客户端收到服务器快照后，消息会先进入缓冲区，再由 [`GhostReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostReceiveSystem.html) 处理

RPC 和命令采用相同原则：消息先由 [`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) 收集，再由各自的 RPC 或命令接收系统消费

> [!NOTE]
> 服务器连接没有 `IncomingSnapshotDataStreamBuffer`

<a id="outgoing-buffers"></a>

### 发送缓冲区

每条连接最多有两个发送缓冲区，分别用于 RPC 和命令，其中命令缓冲区仅存在于客户端：

- [`OutgoingRpcDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.OutgoingRpcDataStreamBuffer.html)
- [`OutgoingCommandDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.OutgoingCommandDataStreamBuffer.html)

命令产生后先进入发送缓冲区，客户端会按固定间隔，也就是每个新 Tick，刷新该缓冲区

RPC 消息采用相同原则：对应发送系统先收集并编码消息，将其写入缓冲区；随后 [`RpcSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.RpcSystem.html) 按固定间隔刷新 RPC 队列，并把多条消息合并到一个最大传输单元（MTU）内

<a id="connection-flow"></a>

## 连接流程

游戏启动时，Netcode for Entities 不会自动让客户端连接服务器，也不会自动让服务器监听特定端口。默认情况下，`ClientServerBootstrap` 只负责创建客户端和服务器 World，由开发者决定双方何时以及如何打开通信通道

可以采用以下方式：

- [通过 `NetworkStreamDriver` 手动让服务器开始监听，或让客户端连接服务器](#manually-listen-or-connect)
- [使用 `AutoConnectPort` 和对应的 `DefaultConnectAddress` 自动连接与监听](#using-the-autoconnectport)
- [分别在客户端或服务器 World 创建 `NetworkStreamRequestConnect` 或 `NetworkStreamRequestListen` 请求](#controlling-the-connection-flow-using-networkstreamrequest)

> [!NOTE]
> 无论采用哪种服务器连接方式，都强烈建议在连接期间确保 `Application.runInBackground` 为 `true`
>
> 可以直接设置 `Application.runInBackground = true;`，也可以在 **Project Settings** > **Player** > **Resolution and Presentation** 中进行项目级配置。否则应用失去焦点时，例如玩家切换到其他窗口，Netcode 将无法继续 Tick，多人游戏会停滞并很可能断开连接
>
> 服务器通常应始终启用该选项。`WarnAboutApplicationRunInBackground` 会为客户端和服务器提供相关错误警告

<a id="manually-listen-or-connect"></a>

### 手动监听或连接

要建立连接，先获取客户端和服务器 World 中都存在的 [`NetworkStreamDriver`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamDriver.html) 单例，然后调用其 `Connect` 或 `Listen`

手动监听和连接的代码示例请参阅 [DOTS 示例仓库](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/Assets/Samples/HelloNetcode/1_Basics/01_BootstrapAndFrontend/Frontend/Frontend.cs#L80)

<a id="using-the-autoconnectport"></a>

### 使用 `AutoConnectPort`

`ClientServerBootstrap` 的 [`AutoConnectPort`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_AutoConnectPort) 可以配合以下两个地址属性，让服务器和客户端在初始化时分别自动监听与连接：

- [`DefaultConnectAddress`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_DefaultConnectAddress)
- [`DefaultListenAddress`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html#Unity_NetCode_ClientServerBootstrap_DefaultListenAddress)

要配置 `AutoConnectPort`，需要创建自定义 [Bootstrap](client-server-worlds.md#bootstrap)，并在创建 World 前把 `AutoConnectPort` 设为非 0 值。例如：

```csharp
public class AutoConnectBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        // 启用自动连接
        AutoConnectPort = 7979;

        // 根据 Player 构建类型或 Editor 的 PlayMode Tools 设置，创建默认客户端和服务器 World
        CreateDefaultClientServerWorlds();
        return true;
    }
}
```

服务器开始监听 `DefaultListenAddress:AutoConnectPort`，`DefaultListenAddress` 默认是 `NetworkEndpoint.AnyIpv4`。客户端开始连接 `DefaultConnectAddress:AutoConnectPort`，`DefaultConnectAddress` 默认是 `NetworkEndpoint.Loopback`

> [!NOTE]
> 在 Editor 中，[PlayMode Tool](playmode-tool.md) 可以覆盖 `AutoConnectAddress` 和 `AutoConnectPort`。但当 `AutoConnectPort` 为 0 时，不会应用 PlayMode Tool 的覆盖值，此时需要手动触发连接

<a id="controlling-the-connection-flow-using-networkstreamrequest"></a>

### 使用 `NetworkStreamRequest` 控制连接流程

除了直接调用 [`NetworkStreamDriver`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamDriver.html) 的方法，还可以创建请求实体：

- 创建 [`NetworkStreamRequestConnect`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamRequestConnect.html) 单例，请求连接目标服务器地址与端口
- 创建 [`NetworkStreamRequestListen`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamRequestListen.html) 单例，请求服务器开始监听目标地址与端口

```csharp
// 在客户端 World 创建包含 NetworkStreamRequestConnect 的实体，稍后由 NetworkStreamReceiveSystem 消费
var connectRequest = clientWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
clientWorld.EntityManager.SetComponentData(connectRequest,
    new NetworkStreamRequestConnect { Endpoint = serverEndPoint });

// 在服务器 World 创建包含 NetworkStreamRequestListen 的实体，稍后由 NetworkStreamReceiveSystem 消费
var listenRequest = serverWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestListen));
serverWorld.EntityManager.SetComponentData(listenRequest,
    new NetworkStreamRequestListen { Endpoint = serverEndPoint });
```

[`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) 会在运行时消费这些请求

> [!NOTE]
> 如果遇到运行时错误，请打开 PlayMode Tools 窗口并重新进入 Play Mode
>
> 如果 World 已经存在，说明 Bootstrap 正在自动创建它们。如果服务器 World 已开始监听，或者客户端 World 已开始连接，说明自动连接已经启用。此时需要修改 Bootstrap 并禁用自动连接，才能使用手动连接流程

<a id="network-simulator"></a>

### 网络模拟器

Unity Transport 提供了 [`SimulatorUtility`](playmode-tool.md#networksimulator)，可以通过 Netcode for Entities 包进行配置。在 Editor 中打开 **Multiplayer** > **PlayMode Tools** 即可使用

强烈建议经常在启用模拟器的情况下测试玩法，因为这种环境更接近真实网络条件

<a id="listening-for-client-connection-events"></a>

## 监听客户端连接事件

`NetworkStreamDriver` 单例提供只读集合 `public NativeArray<NetCodeConnectionEvent>.ReadOnly ConnectionEventsForTick`，客户端和服务器都可以遍历它并响应客户端连接事件

这些事件只保留一个 `SimulationSystemGroup` Tick，并分别在 `NetworkStreamConnectSystem` 和 `NetworkStreamListenSystem` 中重置。如果系统在上述系统的 Job 执行后运行，就会在事件产生的同一 Tick 收到通知；如果在这些 Job 执行前查询集合，则读到的是上一个 Tick 的值

```csharp
// 连接事件监听系统示例
[UpdateAfter(typeof(NetworkReceiveSystemGroup))]
[BurstCompile]
public partial struct NetCodeConnectionEventListener : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var connectionEventsForClient =
            SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
        foreach (var evt in connectionEventsForClient)
        {
            UnityEngine.Debug.Log($"[{state.WorldUnmanaged.Name}] {evt.ToFixedString()}!");
        }
    }
}
```

> [!NOTE]
> 服务器采用固定 Delta Time，因此每个渲染帧中 `SimulationSystemGroup` 可能运行任意次数，也可能一次都不运行
>
> 因此，`ConnectionEventsForTick` 只能在 `SimulationSystemGroup` 内的系统中读取。在该组之外访问它，可能只看到当前 Tick 的事件而漏掉之前 Tick，也可能在某个没有模拟 Tick 的渲染帧中重复收到事件
>
> 不要在 `InitializationSystemGroup`、`PresentationSystemGroup` 或任何 `MonoBehaviour` Unity 回调中访问 `ConnectionEventsForTick`

<a id="netcodeconnectionevents-on-the-client"></a>

### 客户端上的 `NetCodeConnectionEvent`

| 连接状态 | 触发规则 |
|---|---|
| `Unknown` | 永不触发 |
| `Connecting` | 当前客户端触发一次。`NetworkStreamReceiveSystem` 注册 `Connect` 调用时触发，可能比调用 `Connect` 晚一帧 |
| `Handshake` | 当前客户端触发一次。客户端进入 Transport Driver 内部 `Connected` 状态时触发。随后客户端需要等待 Netcode 自动握手完成，参阅 `NetworkProtocolVersion` 与 `RequestProtocolVersionHandshake`。该过程取决于 Ping，通常只需几个 Tick |
| `Approval` | 仅启用[连接审批](#connection-approval)时，当前客户端触发一次。在与服务器成功握手后出现。启用审批会让客户端连接服务器多花几帧 |
| `Connected` | 当前客户端触发一次。服务器向该客户端发送 `NetworkId` 时触发 |
| `Disconnected` | 当前客户端触发一次。主动断开、超时或被服务器断开时触发，并设置 `DisconnectReason` |

> [!NOTE]
> 客户端不会收到其他客户端的事件。客户端 World 中产生的事件只属于自身连接

> [!NOTE]
> `Handshake` 和 `Approval` 阶段都可能失败，因此受 `ClientServerTickRate.HandshakeApprovalTimeoutMS` 超时限制，默认值为 5000 ms

<a id="netcodeconnectionevents-on-the-server"></a>

### 服务器上的 `NetCodeConnectionEvent`

| 连接状态 | 触发规则 |
|---|---|
| `Unknown` | 永不触发 |
| `Connecting` | 服务器不知道客户端何时开始连接，因此永不触发 |
| `Handshake` | 每个客户端触发一次。服务器监听 Driver 接受 Transport 连接后立即进入该状态，并保持到交换完 `NetworkProtocolVersion` 信息。自 1.3 起，握手不再是瞬时过程，也可能超时 |
| `Approval` | 仅启用审批流程时，每个客户端触发一次。在 Netcode 内部握手成功后出现；如果不要求审批，此时客户端原本会进入 `Connected`。参阅 `NetworkStreamDriver.RequireConnectionApproval` |
| `Connected` | 每个获准客户端触发一次。服务器接受连接并为该客户端分配 `NetworkId` 的帧触发，发生在 `Handshake` 和可选 `Approval` 之后 |
| `Disconnected` | 每个连接后又断开的客户端触发一次。在服务器收到断开事件或状态的帧触发，并设置 `DisconnectReason` |

> [!NOTE]
> 服务器成功执行 `Bind` 或开始 `Listen` 时不会产生事件，应使用现有 API 查询这些状态

<a id="connection-approval"></a>

## 连接审批

服务器可以要求审批每条客户端连接。审批适合验证尝试连接服务器的客户端，可以用于玩家准入控制，例如白名单、黑名单和密码保护服务器，也可以用于身份验证，例如要求玩家提交匹配服务响应中包含的秘密令牌，确保只有成功匹配的玩家能够加入服务器

启用连接审批后，连接流程会发生以下变化：

- 在 `Handshake` 和 `Approval` 阶段，客户端只能发送实现 `IApprovalRpcCommand` 的 RPC 供服务器处理
- 所有客户端从 `Handshake` 进入 `Approval`，而不是直接进入 `Connected`
- 服务器必须为每条连接的实体添加 `ConnectionApproved` 组件，手动批准连接
- 只有连接审批成功后才会分配 `NetworkId`；拒绝审批时会断开客户端
- 审批流程受 [`ClientServerTickRate.HandshakeApprovalTimeoutMS`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_HandshakeApprovalTimeoutMS) 限制，可能因超时而失败

再次强调：在 `Handshake` 和 `Approval` 阶段，客户端可以发送多条 RPC，但每条都必须实现 `IApprovalRpcCommand`。这些 RPC 负载可以包含认证令牌、玩家身份或其他用于验证客户端是否允许继续连接的数据。服务器验证通过后，在网络连接实体上添加 `ConnectionApproved`，连接流程就会继续

必须在客户端和服务器上都把 `NetworkStreamDriver.RequireConnectionApproval` 设为 `true`，审批流程才能正确工作

启用连接审批：

```csharp
if (isServer)
{
    using var drvQuery = server.EntityManager.CreateEntityQuery(
        ComponentType.ReadWrite<NetworkStreamDriver>());
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ep);
}
else
{
    using var drvQuery = client.EntityManager.CreateEntityQuery(
        ComponentType.ReadWrite<NetworkStreamDriver>());
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
    drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, ep);
}
```

连接审批处理可以按以下方式配置：

```csharp
// 审批 RPC，此处包含服务器要验证的假设负载
public struct ApprovalFlow : IApprovalRpcCommand
{
    public FixedString512Bytes Payload;
}

// 标记已经发送审批 RPC，避免重复发送
public struct ApprovalStarted : IComponentData
{
}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
    WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct ClientConnectionApprovalSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<RpcCollection>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // 查找尚未完全连接的连接，并发送审批消息
        foreach (var (connection, entity) in
            SystemAPI.Query<RefRW<NetworkStreamConnection>>()
                .WithNone<NetworkId>()
                .WithNone<ApprovalStarted>()
                .WithEntityAccess())
        {
            var sendApprovalMsg = ecb.CreateEntity();
            ecb.AddComponent(sendApprovalMsg, new ApprovalFlow { Payload = "ABC" });
            ecb.AddComponent<SendRpcCommandRequest>(sendApprovalMsg);
            ecb.AddComponent<ApprovalStarted>(entity);
        }

        ecb.Playback(state.EntityManager);
    }
}

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerConnectionApprovalSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // 处理尚未完成审批的客户端消息
        foreach (var (receiveRpc, approvalMsg, entity) in
            SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRW<ApprovalFlow>>()
                .WithEntityAccess())
        {
            var connectionEntity = receiveRpc.ValueRO.SourceConnection;
            if (approvalMsg.ValueRO.Payload.Equals("ABC"))
            {
                ecb.AddComponent<ConnectionApproved>(connectionEntity);

                // 销毁已经处理的 RPC 消息
                ecb.DestroyEntity(entity);
            }
            else
            {
                // 审批失败时断开连接
                ecb.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);
            }
        }

        ecb.Playback(state.EntityManager);
    }
}
```
