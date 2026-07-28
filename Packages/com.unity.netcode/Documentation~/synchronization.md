# 同步状态与输入

在多人游戏中使用 Ghost、命令和 RPC 同步服务器与客户端之间的状态和输入

| **主题**                                        | **说明**                                      |
|:------------------------------------------------|:----------------------------------------------|
| **[使用 Ghost 进行同步](ghosts.md)** | 使用 Ghost 以一致且可自定义的方式在服务器与客户端之间同步并复制状态 |
| **[使用 RPC 通信](rpcs.md)** | 使用远程过程调用（RPC）传递高层游戏流程事件，并从客户端向服务器发送一次性的非预测命令 |
| **[使用命令流处理输入](command-stream.md)** | 当 [`NetworkStreamConnection`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) 被标记为游戏中状态时，客户端会持续向服务器发送命令流。该数据流包括全部输入，以及对最近收到快照的确认 |
