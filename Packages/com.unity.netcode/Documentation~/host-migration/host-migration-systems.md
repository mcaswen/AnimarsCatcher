# 主机迁移系统与数据

在项目中设置主机迁移系统，为客户端托管的网络会话启用主机迁移

Netcode for Entities 提供用于将主机迁移数据收集到缓冲区中的 [API](host-migration-api.md)。之后可以通过 Multiplayer Services SDK 将这些数据上传到主机迁移服务，并在迁移后将数据部署到新服务器

请将本页信息与 [Lobby 和 Relay 集成](lobby-relay-integration.md)配合使用，以便在项目中设置主机迁移

<a id="start-the-host-migration-systems"></a>
## 启动主机迁移系统

若要启用主机迁移，请在服务器 World 中创建 `EnableHostMigration` 单例组件。系统会创建默认 `HostMigrationConfig`，其中包含主机迁移的[配置选项](host-migration-api.md#hostmigrationconfig-component-options)，包括自动收集[主机迁移数据](host-migration-intro.md#host-migration-data)的频率

```csharp
var serverWorld = ClientServerBootstrap.ServerWorld;
serverWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
```

<a id="get-host-migration-data-for-uploading"></a>
## 获取待上传的主机迁移数据

每次更新主机迁移数据时，`HostMigrationStats` 单例组件中的时间戳 `LastDataUpdateTime` 也会更新。可以利用它定期自动将主机迁移数据上传到 Lobby，如下例所示

```csharp
var uploadData = new NativeList<byte>(Allocator.Temp);
if (SystemAPI.TryGetSingleton<HostMigrationStats>(out var stats) && stats.LastDataUpdateTime > m_LastUpdateTime)
{
    HostMigration.GetHostMigrationData(ref uploadData);
    var uploadArray = uploadData.AsArray().ToArray();
    LobbyService.Instance.UploadMigrationDataAsync(m_MigrationConfig, uploadArray, new LobbyUploadMigrationDataOptions());
}
```

<a id="deploy-host-migration-data-to-a-new-server"></a>
## 将主机迁移数据部署到新服务器

发生主机迁移事件时，即将成为新主机的客户端需要下载主机迁移数据，并将其部署到新的服务器 World。之后，该 World 需要接管 Lobby 中的主机职责。以下辅助函数使用提供的驱动构造器创建新的服务器 World；该构造器包含 Relay 信息。函数随后将迁移数据部署到该 World，开始监听，并把本地客户端 World 连接到新的服务器实例

```csharp
var migrationData = await LobbyService.Instance.DownloadMigrationDataAsync(m_MigrationConfig, new LobbyDownloadMigrationDataOptions());

var allocation = await RelayService.Instance.CreateAllocationAsync(10);
var hostRelayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(hostRelayData, new RelayServerData());

var arrayData = new NativeArray<byte>(migrationData.Data.Length, Allocator.Temp);
var slice = new NativeSlice<byte>(arrayData);
slice.CopyFrom(migrationData.Data);

if (!HostMigration.MigrateDataToNewServerWorld(driverConstructor, ref arrayData))
{
    Debug.LogError($"将数据迁移到新服务器 World 时，主机迁移失败");
}
```

不同项目销毁和创建 World 的方式可能有所不同。在此示例中，执行代码的客户端上原本没有服务器 World，因此可以从头创建服务器 World。该过程会自动将客户端从 Relay 连接切换到与刚创建的本地服务器 World 之间的本地连接

```csharp
public static bool MigrateDataToNewServerWorld(INetworkStreamDriverConstructor driverConstructor, ref NativeArray<byte> migrationData)
{
    var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
    NetworkStreamReceiveSystem.DriverConstructor = driverConstructor;
    var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");
    NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

    if (migrationData.Length == 0)
        Debug.LogWarning($"主机迁移时未提供主机迁移数据，不会部署任何数据");
    else
        HostMigrationUtility.SetHostMigrationData(serverWorld, migrationData);

    using var serverDriverQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
    var serverDriver = serverDriverQuery.GetSingletonRW<NetworkStreamDriver>();
    if (!serverDriver.ValueRW.Listen(NetworkEndpoint.AnyIpv4))
    {
        Debug.LogError($"NetworkStreamDriver.Listen() 失败");
        return false;
    }

    var ipcPort = serverDriver.ValueRW.GetLocalEndPoint(serverDriver.ValueRW.DriverStore.FirstDriver).Port;

    // 需要重新创建客户端驱动，然后通过 IPC 直接连接到新的服务器 World
    return ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, NetworkEndpoint.LoopbackIpv4.WithPort(ipcPort));
}
```

<a id="connect-clients-to-the-new-host"></a>
## 将客户端连接到新主机

主机迁移事件发生后，每个客户端都需要通过 Relay 服务器连接到新主机；刚成为主机的客户端会直接连接本地服务器。为此，每个客户端都需要在客户端 World 中重新创建网络驱动，以使用新主机提供的新 Relay 分配。以下示例展示了在客户端 World 中重新配置网络驱动的辅助函数

```csharp
var allocation = await RelayService.Instance.JoinAllocationAsync(newJoinCode);
var relayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(new RelayServerData(), relayData);
HostMigration.ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, relayData.Endpoint);
```

角色没有变化的客户端，也就是迁移后仍作为客户端运行的客户端，可以复用现有客户端 World，但需要使用新的分配信息重新创建客户端网络驱动，才能连接到新服务器

```csharp
public static bool ConfigureClientAndConnect(World clientWorld, INetworkStreamDriverConstructor driverConstructor, NetworkEndpoint serverEndpoint)
{
    if (clientWorld == null || !clientWorld.IsCreated)
    {
        Debug.LogError("HostMigration.ConfigureClientAndConnect：提供的客户端 World 无效");
        return false;
    }

    using var clientNetDebugQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
    var clientNetDebug = clientNetDebugQuery.GetSingleton<NetDebug>();
    var clientDriverStore = new NetworkDriverStore();
    driverConstructor.CreateClientDriver(clientWorld, ref clientDriverStore, clientNetDebug);
    using var clientDriverQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
    var clientDriver = clientDriverQuery.GetSingleton<NetworkStreamDriver>();
    clientDriver.ResetDriverStore(clientWorld.Unmanaged, ref clientDriverStore);

    var connectionEntity = clientDriver.Connect(clientWorld.EntityManager, serverEndpoint);
    if (connectionEntity == Entity.Null)
        return false;
    return true;
}
```

## 其他资源

* [主机迁移 API 与组件](host-migration-api.md)
* [Lobby 与 Relay 集成](lobby-relay-integration.md)
