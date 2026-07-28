# 管理序列化成本

管理序列化成本以优化游戏性能

当 `GhostField` 发生变化时，其数据会被序列化并通过网络同步。如果某个 Job 对 `GhostField` 具有写入权限，Netcode for Entities 会检查该 Job 是否导致数据发生变化。系统会序列化数据并与此前同步的版本比较；如果数据没有变化，则丢弃新的序列化结果

因此，如果数据不会发生变化，应确保没有 Job 对其拥有写入权限

## 降低序列化和反序列化的 CPU 成本

发送和接收 Ghost 数据涉及高开销的 CPU 读写操作，其成本随数据包中序列化的 Ghost 数量线性增长。服务器使用预测式[增量压缩](compression.md)策略分批序列化 Ghost 数据。系统根据最近三个基线，也就是最近三个已确认值，通过类似线性外推的方式得到预测值，再对复制字段与预测值之间的增量进行编码。客户端解码时采用相同策略：使用服务器告知的同一组基线预测新值，然后以该预测值为参照进行解压缩

这种方法对计时器、线性移动、线性递增或递减等可预测的 Ghost 数据很有效。在这些情况下，预测值通常与当前状态值完全一致或非常接近，因此序列化后的增量为零或接近零，能够显著节省带宽

但是，使用三个基线进行预测式增量压缩也有一些缺点：

* 即使 Ghost 数据没有变化，Netcode for Entities 也必须继续向客户端发送该 Ghost 的快照
* 服务器端的 CPU 编码成本略高
* 客户端的 CPU 解码成本也略高，尤其是对于不常变化的 `GhostField`

因此，基于三个基线的压缩主要建议用于可预测字段。对于不可预测的数据，它节省的资源不多，可能更适合[使用单个基线](#使用单个基线)

### 使用单个基线

可以通过 [`GhostAuthoringComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html) 中的 `UseSingleBaseline` 选项，按 Archetype 降低部分编码成本
启用后，服务器会始终为该特定预制体类型使用单个基线进行增量压缩

如果想在不修改全部预制体的情况下测试所有 Ghost 使用单个基线的影响，可以使用 [`GhostSendSystemData.ForceSingleBaseline`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_ForceSingleBaseline) 属性。在开发期间使用此选项，可以同时测试单个基线对游戏带宽和 CPU 的影响

使用单个基线可以降低客户端和服务器的 CPU 使用量，尤其是 Archetype 包含大量组件或很少变化的字段时。其影响通常在客户端更明显，反序列化时间往往可减少约 50%

此外，使用单个基线可以启用一项特定的带宽优化：当任意复制实体在指定时间内没有变化时，可以完全停止重发该 Ghost Chunk。使用三个基线时则必须持续发送 Chunk，以确保始终有三个基线可用

在以下两种常见场景中，`UseSingleBaseline` 选项可以带来显著收益：

* Ghost 预制体适合使用 [`GhostOptimizationMode.Dynamic`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostOptimizationMode.html)，但经常处于不活动状态
* 某种 Ghost 类型的大部分组件数据变化并不遵循线性、可预测的模式，因此使用三个基线得不偿失

> [!NOTE]
> 当 Ghost 使用 `GhostOptimizationMode.Static` 时，预制体始终使用单个基线序列化

## 其他资源

* [压缩](compression.md)
* [Ghost 优化](optimize-ghosts.md)
