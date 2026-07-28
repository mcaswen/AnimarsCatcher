# 主机迁移 API 与组件

了解主机迁移 API、组件和组件选项

<a id="host-migration-api"></a>
## 主机迁移 API

| `HostMigrationData` 类 | 说明 |
|-|-|
| `Get(fromWorld, toData)` | 从给定服务器 World 的主机迁移系统获取当前主机迁移数据，并将其复制到原生列表。必要时会调整列表大小以容纳数据 |
| `Set(fromData, toWorld)` | 在目标服务器 World 中部署给定迁移数据。通常是从 Lobby 服务下载的数据，但需要手动创建 World |

<a id="host-migration-components"></a>
## 主机迁移组件

| 组件 | 说明 |
|-|-|
| `NetworkStreamIsReconnected` | 此组件会添加到客户端和服务器上的全部连接，使它们能够响应重新连接。新主机上生成的 Ghost 也会收到此组件，因此可以查询该组件并执行所需修复 |
| `EnableHostMigration` | 启用主机迁移系统。启用后，系统会按照 `HostMigrationConfig` 指定的间隔收集主机迁移数据，并更新 `HostMigrationStats` 中的最近更新时间 |
| `HostMigrationInProgress` | 用于检测主机迁移正在进行以及迁移已经完成 |
| `HostMigrationConfig` | 单例组件，公开主机迁移系统中可修改的一些选项 |
| `HostMigrationStats` | Lobby 操作的部分统计信息，例如数据 Blob 大小。此组件还包含主机迁移数据的最近更新时间，可用于判断何时需要再次上传 |

<a id="hostmigrationconfig-component-options"></a>
`HostMigrationConfig` 组件包含以下选项：

* `StoreOwnGhosts`：启用或禁用保存主机上由本地客户端拥有的 Ghost。主机断开时，该客户端也会消失，因此这些数据可能不需要保存，默认为 false
* `MigrationTimeout`：等待 Ghost 预制体加载的最长时间，默认为 10 秒
* `ServerUpdateInterval`：更新主机迁移数据的间隔，默认为 2 秒。设为 0 秒表示每次系统更新都收集数据

`HostMigrationStats` 组件包含以下信息：

* `GhostCount`：主机迁移数据中包含的 Ghost 数量
* `PrefabCount`：主机迁移数据中包含的 Ghost 预制体数量
* `UpdateSize`：最近一次序列化的主机迁移数据 Blob 大小
* `TotalUpdateSize`：主机迁移系统迄今收集的数据总大小
* `LastDataUpdateTime`：最近一次更新主机迁移数据的时间

## 其他资源

* [主机迁移简介](host-migration-intro.md)
* [为项目添加主机迁移](add-host-migration.md)
