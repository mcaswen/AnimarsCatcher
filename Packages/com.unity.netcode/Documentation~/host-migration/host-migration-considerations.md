# 支持主机迁移的项目设计注意事项

服务器数据迁移到新服务器时，设置 Ghost、玩家和连接的常规流程会发生变化，可能需要特别处理才能正常工作。未迁移的内容会在 Unity 场景加载到服务器 World 后从默认值开始，而常规场景会保持客户端接管主机职责前的配置。重新连接的客户端可能不需要像新客户端一样初始化，因为其数据可能已经迁移，其他类似流程也需要相应处理

<a id="networkstreamingame-considerations"></a>
## `NetworkStreamInGame` 注意事项

客户端上的 [`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) 需要特别处理。迁移后，服务器会在全部 SubScene 加载完成后，将已有连接和新连接置于游戏中。在至少一个连接进入游戏前，不会重新生成任何 Ghost；该连接通常是本地服务器启动后立即连接的本地客户端。但是在客户端侧，可能很难判断何时可以安全地开始传输 Ghost 快照，因此需要手动添加 `NetworkStreamInGame`。若要以不同方式处理重新连接，可以检查客户端连接是否添加了 `NetworkStreamIsReconnected` 组件。如果重新连接和新连接采用相同连接流程，则无需额外处理。例如，服务器可以向客户端发送类似 `LoadLevel` 的 RPC，客户端处理后自行进入游戏

<a id="migrated-data-can-be-invalid-on-the-new-server"></a>
## 迁移数据在新服务器上可能无效

如果 Ghost 组件中存在实体变量，或任何在主机之间迁移后无法保持有效引用的数据，则需要在迁移后修复这些实例。可以查询带有 `IsMigrated` 标签的 Ghost 实体上的特定组件，以识别重新生成的 Ghost。为使这些变量反映新主机状态，可能还需要改用或补充其他数据。例如，可以通过其他用于标识实体的组件数据查找实体，如 ID 或名称，但不要使用 Ghost ID，原因参见下方说明

<a id="waiting-until-migration-is-completed"></a>
## 等待迁移完成

某些系统可能在新服务器 World 创建后立即开始运行并初始化变量，而这些变量之后会在主机迁移数据部署到 World 时被覆盖或失效。例如，某个系统可能通过查询特定实体来确保满足特定条件。主机迁移数据本来已经满足这些条件，但系统可能在迁移数据准备完成前强制执行条件，导致内容重复或出现其他无效状态

为确保主机迁移后状态稳定，这些系统应检查 World 中是否存在 `HostMigrationInProgress` 单例组件。如果存在，系统可以提前退出，直到该组件消失。此组件会在 World 创建后立即生成，因此能够确保系统只在迁移数据部署并稳定后继续运行

<a id="dealing-with-the-clients-player-entity"></a>
## 处理客户端玩家实体

客户端重新连接到新主机时，可能需要特别处理其在上一主机会话中拥有的玩家实体。由于玩家已包含在主机迁移数据中，无需再次生成；而常规流程通常会在客户端连接并完成与服务器的初始初始化或握手后生成玩家。可以通过多种方式处理这一差异。例如，如果服务器在连接实体上保存客户端信息，如 `PlayerSpawned` 标签组件，该信息也会一同迁移；检测到该标签后，就可以跳过该客户端的玩家初始化。之后 `GhostOwner` 组件也会更新，输入无需额外干预即可正常工作
