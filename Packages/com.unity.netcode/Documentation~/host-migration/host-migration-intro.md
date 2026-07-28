# 主机迁移简介

了解 Netcode for Entities 中主机迁移的基础知识，以及它是否适合当前项目

Netcode for Entities 的主机迁移使用 [Unity Gaming Services](https://unity.com/solutions/gaming-services)，使客户端托管的网络会话在失去主机后仍能继续。主机迁移可用于处理各种主动或意外中断，包括网络断开、电源故障或主机退出应用

<a id="host-migration-basics"></a>
## 主机迁移基础

主机迁移是指以尽量小的玩法中断，将主机角色的职责从一个客户端转移到另一个客户端。在 Netcode for Entities 中，主机迁移需要使用 [Unity Gaming Services](https://unity.com/solutions/gaming-services)，具体包括 [Unity Lobby 服务](https://docs.unity.com/ugs/manual/lobby/manual/unity-lobby-service)、[Unity Relay](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction) 和 [Unity Authentication](https://docs.unity.com/ugs/en-us/manual/authentication/manual/overview)。因此，项目必须关联到 [Unity Cloud Dashboard](https://cloud.unity.com/) 中的项目

完整的主机迁移要求请参阅[主机迁移要求页面](host-migration-requirements.md)。还可以参阅 [Asteroids 主机迁移示例](host-migration-sample.md)，了解主机迁移的实现方式

<a id="host-migration-process"></a>
## 主机迁移过程

启用主机迁移后，主机会定期序列化可同步游戏状态的快照，其中包括已连接客户端列表、已添加组件、已加载场景，以及全部 Ghost 与 Ghost 预制体信息。主机迁移数据会安全上传到当前连接的 Lobby，每份新快照都会覆盖上一份

当主机离开或断开连接，并且 Relay 连接丢失时，会触发主机迁移。Lobby 通知所有已连接客户端，选择其中一个客户端作为新主机。新主机会请求新的 [Relay 分配](https://docs.unity.com/ugs/en-us/manual/relay/manual/connection-flow#1)，并使用新的 Relay 分配信息更新 Lobby 数据。其他客户端收到 Lobby 更新后，即可加入新的 Relay 分配

主机角色迁移后，新主机会从 Lobby 下载最近的快照，并根据该游戏状态创建新的服务器 World。系统实例化 Ghost 并部署其 Ghost 组件数据，使它们恢复到最近一次发送快照时的状态。当客户端连接抵达后，Lobby 会识别哪些客户端此前已连接，以及哪些 Ghost 归它们所有，从而为所有客户端保持游戏状态

<a id="host-migration-data"></a>
### 主机迁移数据

主机迁移期间，以下数据会被保存并在新主机上恢复：

* 服务器连接实体上的全部用户组件，以及 [`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) 组件是否存在。客户端上的连接实体没有特殊处理
* 全部 Ghost 及其 Ghost 组件。系统会保存和恢复 Ghost 组件的完整组件数据，而不只是 Ghost 字段
* 至少有一个变量标记了 `GhostField` 特性的仅服务器组件
* 当前网络 Tick 与经过时间值
* 只支持通常包含在[快照](ghost-snapshots.md)中的数据，例如组件和动态缓冲区。原生容器不会包含在迁移数据中

<a id="detecting-connection-loss"></a>
### 检测连接丢失

已连接玩家会最先检测到与服务器的连接丢失，但这不会立即触发主机迁移。向 [Lobby 服务](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)发出的断开信号才会触发新主机选举和主机迁移。断开信号来自以下两个事件中先发生的一个：

* Relay 连接超时，默认 10 秒
* Lobby WebSocket 连接断开

必须订阅 Lobby 事件，WebSocket 连接才会保持活动并发送断开事件

<a id="managing-end-to-end-latency"></a>
### 管理端到端延迟

完整的主机迁移过程涉及多个延迟和超时。其中一些值可以在代码中自定义，一些可以通过 Unity Dashboard 配置，还有一些是固定常量

| 说明                    | 默认值 | 最小值 | 是否可配置 |
|-------------------------|--------|--------|------------|
| Relay 保活              | 10 秒  | 10 秒  | 否 |
| Lobby 玩家移除延迟      | 120 秒 | 5 秒   | 是，在 Unity Dashboard 中配置 |
| Lobby 主机选举延迟      | 120 秒 | 5 秒   | 是，在 Unity Dashboard 中配置 |

<a id="host-migration-sequence"></a>
## 主机迁移时序

下图展示了三名玩家进行主机迁移时的高层交互

![主机迁移时序图](../images/host-migration-sequence.png)

图中步骤如下：

1. P1 使用连接信息创建 Lobby，连接信息为 [Relay 加入代码](https://docs.unity.com/ugs/en-us/manual/relay/manual/join-codes)或直接 IP 地址与端口
1. P1 开始定期上传迁移数据
1. P2 加入 Lobby 并读取连接信息
1. P2 连接 P1
1. P3 加入 Lobby 并读取连接信息
1. P3 连接 P1
1. Lobby 检测到 P1 已断开，并由服务将主机改为 P2
1. Lobby 通知 P2 主机已变更
1. P2 下载并应用上一任主机留下的迁移数据
1. P2 开始定期上传迁移数据
1. Lobby 通知 P3 主机已变更
1. P3 连接 P2，主机迁移完成

## 其他资源

* [Asteroids 主机迁移示例](host-migration-sample.md)
* [Unity Lobby 文档](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
* [Unity Relay 文档](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
* [Unity Authentication 文档](https://docs.unity.com/ugs/en-us/manual/authentication/manual/overview)
* [`NetworkStreamInGame` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html)
