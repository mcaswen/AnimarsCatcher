# Lobby 与 Relay 集成

与 Unity Lobby 和 Unity Relay 集成，在 Netcode for Entities 中启用主机迁移

本页内容需要使用 Multiplayer Services SDK（com.unity.services.multiplayer）包，具体版本参阅[主机迁移要求](host-migration-requirements.md)

<a id="configure-lobby-settings-on-the-unity-cloud-dashboard"></a>
## 在 Unity Cloud Dashboard 中配置 Lobby 设置

主机迁移使用的 Lobby 设置需要在以下目标之间取得平衡：

- 缩短主机被判定为失联前的延迟，即 _Disconnect Host Migration Time_
- 留出足够时间完成主机迁移，避免其他玩家因被判定为断开而从 Lobby 中移除，即 _Disconnect Removal Time_

若要为项目和环境配置 Lobby：

* 访问 [cloud.unity.com](https://cloud.unity.com/)
* 单击侧边栏中的 _Products_
* 单击 _Lobby_
* 单击 _Config_
* 确保在下拉菜单中选择了正确的项目
* 选择要查看配置的环境，例如 _production_
* 单击 _Edit config_ 修改值

建议以下列值作为起点：

- Active Lifespan：120 秒
- Disconnect Removal Time：60 秒
- Disconnect Host Migration Time：5 秒

详细信息请参阅[配置文档](https://docs.unity.com/ugs/manual/lobby/manual/config-options)

<a id="retrieve-a-player-id"></a>
## 获取玩家 ID

可以从 `AuthenticationService` 实例获取玩家 ID：

```csharp
var currentPlayerId = AuthenticationService.Instance.PlayerId;
```

该 ID 分配给玩家，在主机迁移后保持不变

<a id="create-a-relay-allocation"></a>
## 创建 Relay 分配

主机创建 Relay 分配，并指定允许的最大连接数，不包括主机自身

```csharp
const maxPlayers = 4;
const maxConnections = maxPlayers - 1;
var allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
var relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
```

准备连接的玩家使用 Relay [加入代码](https://docs.unity.com/ugs/en-us/manual/relay/manual/join-codes)加入

<a id="create-a-lobby"></a>
## 创建 Lobby

初始主机应创建 Lobby，并将 Relay 加入代码保存为 `Data` 属性：

```csharp
CreateLobbyOptions options = new CreateLobbyOptions();
options.Data = new Dictionary<string, DataObject>()
{
    {"relayHost", new DataObject(DataObject.VisibilityOptions.Member,
        AuthenticationService.Instance.PlayerId)},
    {"relayJoinCode", new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode)}
};
options.Player = new Player(id: AuthenticationService.Instance.PlayerId, allocationId: allocationId);
var lobby = await LobbyService.Instance.CreateLobbyAsync("name", maxPlayers, options);
```

`AllocationId` 指 Relay 分配，使用 Relay 时必须提供该值

<a id="join-a-lobby"></a>
## 加入 Lobby

Lobby 支持多种加入方式：

* 通过加入代码
* 通过快速加入
* 通过 ID

通过 ID 加入时，通常使用[查询](https://docs.unity.com/ugs/en-us/manual/lobby/manual/query-for-lobbies)发现 ID。查询可以使用任意已建立索引的属性进行筛选和排序，例如 `Name`

以下代码展示如何获取加入代码，例如用于在游戏内显示：

```csharp
var code = lobby.LobbyCode;
```

使用手动输入的代码加入：

```csharp
var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync("CODE");
```

<a id="join-a-relay-allocation"></a>
## 加入 Relay 分配

玩家加入 Lobby 时，应读取 Relay 加入代码：

```csharp
var relayJoinCode = lobby.Data["relayJoinCode"].Value;
```

然后加入 Relay 分配：

```csharp
var allocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
```

主机迁移后，非主机玩家不应立即使用已经过期的 Relay 加入代码，而应等待 `relayJoinCode` 属性发生变化。可以忽略更新事件，直到 `relayHost` 属性与 Lobby 主机一致

<a id="configuring-network-drivers"></a>
## 配置网络驱动

使用扩展工具将分配转换为 `RelayServerData` 结构，并指定所需连接类型，如 dtls、udp 或 wss

```csharp
var connectionType = "dtls";
var relayServerData = allocation.ToRelayServerData(connectionType);
```

该结构用于构造客户端和服务器驱动，最好通过自定义 `INetworkStreamDriverConstructor` 实现处理：

```csharp
public class MyDriverConstructor : INetworkStreamDriverConstructor
{
    RelayServerData m_RelayClientData;
    RelayServerData m_RelayServerData;

    public MyDriverConstructor(RelayServerData serverData, RelayServerData clientData)
    {
        m_RelayServerData = serverData;
        m_RelayClientData = clientData;
    }

    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
        if (ClientServerBootstrap.ServerWorld == null || !ClientServerBootstrap.ServerWorld.IsCreated)
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug, ref m_RelayClientData);
        else
            DefaultDriverBuilder.RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
    }

    public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug, ref m_RelayServerData);
    }
}

var clientData = new RelayServerData();
var serverData = new RelayServerData();
NetworkStreamReceiveSystem.DriverConstructor = new MyDriverConstructor(serverData, clientData);
```

对于主机，将 `allocation.ToRelayServerData` 的结果赋给 `serverData`；对于客户端，则赋给 `clientData`。必须在创建 World 前完成该操作

<a id="heartbeat-a-lobby"></a>
## 向 Lobby 发送心跳

主机负责至少每 30 秒发送一次心跳 Ping，以保持 Lobby 活跃。迁移后，新主机应接管该职责

```csharp
await LobbyService.Instance.SendHeartbeatPingAsync("lobbyId");
```

不活跃的 Lobby 会从查询结果中消失，直到重新激活，以防止新成员发现并加入不活跃的 Lobby。Lobby 不活跃一小时后会被永久删除

<a id="subscribe-to-events"></a>
## 订阅事件

以下代码会创建一个与 Lobby 的实时通知通道，在主机变更等任意 Lobby 变化发生时通知客户端。它还会建立实时 WebSocket 连接，该连接终止时会作为断开信号：

```csharp
var callbacks = new LobbyEventCallbacks();
callbacks.LobbyChanged += OnLobbyChanged;
await LobbyService.Instance.SubscribeToLobbyEventsAsync("lobbyId", callbacks);
```

<a id="obtain-host-migration-data"></a>
## 获取主机迁移数据信息

上传迁移数据分为两个步骤。第一步是从 Lobby 获取信息结构：

```csharp
MigrationDataInfo info = await LobbyService.Instance.GetMigrationDataInfoAsync("lobbyId");
var expires = info.Expires; // 在此期限前刷新
```

该数据只有几分钟有效期，必须定期刷新。过期时间可以从 `Expires` 属性获取

<a id="upload-host-migration-data"></a>
### 上传主机迁移数据

若要上传迁移数据，请将信息结构与字节数组传给 `UploadMigrationDataAsync` 函数：

```csharp
byte[] data = new byte[1_024];
await LobbyService.Instance.UploadMigrationDataAsync(info, data);
```

该方法使用 `UnityWebRequest` 执行 HTTP PUT 请求

<a id="detect-a-host-migration"></a>
## 检测主机迁移

主机迁移通过通用的 `LobbyChanged` 回调传达。`ILobbyChanges` 参数表示变更中是否包含 `HostId` 变化：

```csharp
void OnLobbyChanged(ILobbyChanges changes)
{
    changes.ApplyToLobby(lobby);
    if (changes.HostId.Changed)
    {
        var newHostId = changes.HostId.Value;
    }
}
```

<a id="download-migration-data"></a>
## 下载迁移数据

新主机若要下载迁移数据，先按照上述步骤获取主机迁移数据信息，再将信息结构传给 `DownloadMigrationDataAsync` 函数：

```csharp
byte[] data = await LobbyService.Instance.DownloadMigrationDataAsync(info);
```

该方法使用 `UnityWebRequest` 执行 HTTP GET 请求

<a id="force-a-host-migration"></a>
## 强制主机迁移

会话主机可以在不离开会话的情况下主动选择新主机：

```csharp
await LobbyService.Instance.UpdateLobbyAsync(
    "lobbyId", new UpdateLobbyOptions() { HostId = "newHostId" });
```

上一任主机会留在会话中，并降级为普通玩家

<a id="additional-relay-considerations"></a>
## 其他 Relay 注意事项

主机迁移不会改变 Relay 集成本身，但迁移时需要考虑以下几个方面

主机迁移后，必须重新执行完整的 Relay 分配和加入流程。此前使用的所有 Relay Allocation ID 与 Relay 加入代码都会失效。此外，无法保证新的 Relay 分配仍位于同一台服务器，甚至无法保证位于同一区域

<a id="quality-of-service-qos"></a>
### 服务质量（QoS）

调用 `CreateAllocationAsync` 时，如果 Relay 区域参数为 null，也就是默认值，系统会执行 [QoS](https://docs.unity.com/ugs/en-us/manual/relay/manual/qos) 测量，为分配选择距离最近的区域。这会增加一些启动延迟，最多约 500ms。主机迁移后，新主机应重新执行该过程

也可以将区域保存在内存或会话中。这样可以在假设上一地区对剩余玩家仍是合理选择的前提下，降低主机迁移时的延迟

<a id="re-allocation-and-wait-condition"></a>
### 重新分配与等待条件

主机断开时，其 Relay 分配和 Relay 加入代码会终止且无法复用。请注意，Relay 加入代码与 Lobby 加入代码不同。新主机必须创建新的 Relay 分配并获取新的加入代码，再用新代码覆盖 Lobby 中的旧代码

这会产生竞态条件：其他玩家可能在新的 Relay 加入代码准备完成前就收到主机变更通知。此外，旧代码仍然存在于 Lobby 中，但必须忽略。为解决该竞态条件，Relay 加入代码旁会保存一个属性，用于标识创建该分配的玩家 ID。在此 ID 与 Lobby 主机 ID 一致前，玩家应忽略 Relay 加入代码且不要连接

<a id="keeping-allocationid-field-up-to-date"></a>
### 保持 `allocationId` 字段最新

每个 Lobby 成员都有一个可选字段，用于存储其 Relay `allocationId`。开始或加入会话时必须设置该字段；主机迁移后所有 `allocationId` 都会变化，也必须更新该字段

## 其他资源

* [Unity Lobby 文档](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
* [Unity Relay 文档](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
* [主机迁移要求](host-migration-requirements.md)
* [主机迁移 API 与组件](host-migration-api.md)
* [主机迁移系统与数据](host-migration-systems.md)
