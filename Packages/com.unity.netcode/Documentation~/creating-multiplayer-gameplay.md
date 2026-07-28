# 创建多人游戏玩法

在 Netcode for Entities 中创建多人游戏玩法

| **主题**                        | **说明**                         |
| :------------------------------ | :------------------------------- |
| **[连接服务器与客户端](network-connection.md)** | Netcode for Entities 使用 [Unity Transport 包](https://docs.unity3d.com/Packages/com.unity.transport@latest)管理连接。每个连接都存储为一个实体，名称为 `NetworkConnection [nid]`；每个连接实体都具有一个 [NetworkStreamConnection](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) 组件，其中包含该连接的 `Transport` 句柄 |
| **[同步状态与输入](synchronization.md)** | 说明 Netcode 如何同步 Ghost 状态与输入/命令，列出支持的类型，并介绍如何标记需要通过 Netcode 最终一致性模型复制的字段与组件 |
| **[时间同步](time-synchronization.md)** | Netcode 使用服务器权威模型，即服务器根据距上次更新所经过的时间执行固定时间步。因此，为使该模型正常工作，客户端需要始终与服务器时间保持一致 |
| **[插值与外推](interpolation.md)** | 在游戏中使用插值和外推，尽量减小不良网络状况对玩法的影响 |
| **[预测](prediction.md)** | 使用预测处理游戏中的延迟 |
| **[物理](physics.md)** | Netcode 包与 Unity Physics 进行了一定程度的集成，使网络游戏更容易使用物理。该集成能够处理具有物理效果的插值 Ghost，并支持具有物理效果的预测 Ghost |
| **[主机迁移](host-migration/host-migration.md)** | 当前主机离开时，使用主机迁移将主机角色转移给同一会话中的客户端 |
