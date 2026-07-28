# Ghost 与快照

了解 Netcode for Entities 中的 [Ghost](#ghosts) 和[快照](#snapshots)，以及如何使用它们同步多人游戏项目中的状态

Netcode for Entities 还支持一种有限的[类远程过程调用操作（RPC）](rpcs.md)，用于处理事件。有关何时使用 Ghost 或 RPC，请参阅 [RPC 页面中的对比](rpcs.md#comparing-ghosts-and-rpcs)

<a id="ghosts"></a>
## Ghost

Ghost 是多人游戏中的网络对象

* Ghost 由服务器拥有并模拟。换言之，服务器拥有所有 Ghost 的最终权威，因此可以生成、销毁和更新 Ghost 实体
* 连接到服务器的每个客户端都保存每个相关服务器 Ghost 的副本。服务器每个网络 Tick 发送一次[快照](#snapshots)，其中包含一部分 Ghost 的当前状态，客户端通过接收快照更新本地表示。客户端随后在两条时间线之一上向游戏模拟的其余部分呈现 Ghost 更新状态，参阅[插值](interpolation.md)和[客户端预测](intro-to-prediction.md)，从而平滑渲染 Ghost 等内容

请注意，客户端不能直接控制或影响 Ghost，因为服务器拥有整个游戏模拟的权威。因此，客户端对 Ghost 进行的任何修改都属于客户端预测，并且新的服务器权威快照数据抵达时可能而且通常会被还原

创建 Ghost 时，需要定义它在[客户端与服务器之间的同步方式](#synchronize-ghost-components-and-fields)。定义完成后如何生成 Ghost，请参阅 [Ghost 生成页面](ghost-spawning.md)

<a id="create-a-ghost"></a>
### 创建 Ghost

在 Unity 编辑器中创建带有 [`GhostAuthoringComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html) 的[预制体](https://docs.unity3d.com/Manual/CreatingPrefabs.html)，即可创建 Ghost

编辑器中的 `GhostAuthoringComponent` 提供一个小型配置界面，用于设置 Netcode for Entities 如何同步该预制体。每个 Ghost 都必须设置 __Name__、__Importance__、__Supported Ghost Mode__、__Default Ghost Mode__ 和 __Optimization Mode__。还建议设置 __MaxSendRate__，以降低绝对带宽消耗。Netcode for Entities 在带宽不足以把全部实体放入单份快照时，使用 __Importance__ 属性决定发送哪些实体；每份快照数据包的大小可以自定义。值越高，Ghost 越可能被发送

可选属性 __MaxSendRate__ 表示该 Ghost 预制体类型所在 Ghost Chunk 的绝对最大发送频率，单位为 Hz，但存在少数特殊例外

**重要说明：**`MaxSendRate` 只表示最高**可能**复制频率，无法保证在所有情况下达到。最终发送速率还取决于 `ClientServerTickRate.NetworkTickRate`、Ghost 实例数量、__Importance__、重要度缩放、`GhostSendSystemData.DefaultSnapshotPacketSize`、结构变化等因素

示例：

* `MaxSendRate` 为 100Hz 的 Ghost 仍会受到 `NetworkTickRate` 限制，后者默认为 60Hz
* 类似地，在 `NetworkTickRate` 为 30Hz 的项目中，`MaxSendRate` 为 60Hz 的 Ghost 实例最高只会以 30Hz 发送
* 该计算只能基于整数个 `ticksSinceLastSent` Tick，因此当 `MaxSendRate` 位于 `NetworkTickRate` 的整数分频之间时，会向下取到下一个可实现的频率。例如，`NetworkTickRate:30Hz`、`MaxSendRate:45` 时，实际最大发送速率为 30Hz

<a id="supported-ghost-mode-options"></a>
### __Supported Ghost Mode__ 选项

* __All__：Ghost 同时支持[插值](interpolation.md)和[预测](intro-to-prediction.md)
* __Interpolated__：Ghost 只支持插值，不能作为预测 Ghost 生成
* __Predicted__：Ghost 只支持预测，不能作为插值 Ghost 生成

<a id="default-ghost-mode-options"></a>
### __Default Ghost Mode__ 选项

* __Interpolated__：Unity 将服务器发来的所有 Ghost 视为插值 Ghost
* __Predicted__：Unity 将服务器发来的所有 Ghost 视为预测 Ghost
* __Owner predicted__：拥有 Ghost 的客户端对其进行预测，其他客户端对其进行插值。选择此属性时，还必须添加 __GhostOwner__，并在代码中设置其 __NetworkId__ 字段。Unity 会将该字段与各客户端的网络 ID 比较，以找出正确所有者

<a id="optimization-mode-options"></a>
### __Optimization Mode__ 选项

* __Dynamic__：默认设置。预期 Ghost 经常变化时使用。无论变化还是不变化，都会针对较小的快照大小进行优化
* __Static__：预期 Ghost 很少变化时使用。Ghost 发生变化时不会针对较小快照优化，但未变化时完全不发送

<a id="structural-changes-on-instantiated-ghosts"></a>
## 已实例化 Ghost 上的结构变化

可以对已经实例化的 Ghost 预制体进行部分结构变化，例如[添加或移除组件](#add-or-remove-components-on-an-instantiated-prefab)，或[添加、移除、销毁子实体](#add-remove-or-destroy-child-entities-on-an-instantiated-prefab)，但存在限制

| 操作 | 是否支持 | 限制 |
|------|----------|------|
| 添加或移除组件或缓冲区 | 是 | 无 |
| 添加或移除复制组件或缓冲区 | 是 | 参阅[添加或移除组件](#add-or-remove-components-on-an-instantiated-prefab) |
| 添加、移除或销毁子实体 | 是 | 参阅[添加、移除或销毁子实体](#add-remove-or-destroy-child-entities-on-an-instantiated-prefab) |

> [!NOTE]
> 严格来说，添加、移除或销毁子实体不属于结构变化，但会影响复制

<a id="add-or-remove-components-on-an-instantiated-prefab"></a>
### 在已实例化预制体上添加或移除组件

可以在已实例化预制体的根实体和子实体上添加或移除任意用户组件，Ghost 的序列化、反序列化和增量压缩仍会正常工作

但是，向已实例化 Ghost 添加组件，即使组件带有 `[GhostField]`，也不会把该组件复制到同一 Ghost 预制体的其他实例。需要复制的组件必须在创作阶段就是预制体的一部分

<a id="add-remove-or-destroy-child-entities-on-an-instantiated-prefab"></a>
### 在已实例化预制体上添加、移除或销毁子实体

不能移除 `LinkedEntityGroup` 缓冲区中的复制子实体，也不能改变其索引，否则可能导致序列化和反序列化错误。不过，可以执行以下操作：

* 销毁 `LinkedEntityGroup` 中任意子实体，无论其是否具有复制组件；但不能重新排序或移除 `LinkedEntityGroup` 中对应的条目
* 从 `LinkedEntityGroup` 缓冲区移除实体，前提是不会导致原始复制子实体重新排序
* 向 `LinkedEntityGroup` 末尾追加实体
    * 通常应避免在 `LinkedEntityGroup` 缓冲区原始条目前插入实体，或在原始条目之间插入实体。不过，可以在最后一个带有复制组件的子项之后插入实体

以下有效与无效配置示例提供了更多细节：

```text
// 有效配置，(*) 表示已销毁实体
root
  child 1 (*) <-- 已复制
  child 2
  child 3     <-- 已复制
  child 4 (*)

// 有效配置，实体追加在末尾
root
  child 1   <-- 已复制
  child 2   <-- 已复制
  =-----=  从此处之后追加或移除
  child 3
  user entity 1
  user entity 2

// 无效配置，在开头插入实体导致索引变化
root
  new user entity 1 <--- 无效，会破坏复制实体索引
  child 1  <-- 已复制
  child 2  <-- 已复制
  child 3

// 无效配置，在原始条目之间添加实体导致索引变化
root
  child 1  <-- 已复制
  new user entity 1 <--- 无效，会破坏复制实体索引
  child 2  <-- 已复制
  child 3
```

<a id="synchronize-ghost-components-and-fields"></a>
## 同步 Ghost 组件与字段

Netcode for Entities 使用 C# 特性配置 Ghost 中需要同步的组件和字段

可以使用以下基础特性：

| 特性 | 用法 | 更多信息 |
|------|------|----------|
| [`GhostFieldAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostFieldAttribute.html) | 在结构字段或属性上使用 `GhostFieldAttribute`，指定需要序列化的组件或缓冲区字段。组件至少有一个字段标记 `[GhostField]` 后，就会成为复制组件，并作为 Ghost 数据的一部分传输 | [使用 GhostFieldAttribute 进行序列化与同步](ghostfield-synchronize.md) |
| [`GhostEnabledBitAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostEnabledBitAttribute.html) | 在 `IEnableableComponent` 结构定义上使用 `GhostEnabledBitAttribute`，指定需要序列化该组件的启用位。组件标记 `[GhostEnabledBit]` 后，其启用位会被复制，并作为 Ghost 数据的一部分传输 | [GhostComponentAttribute](ghostcomponentattribute.md) |
| [`GhostComponentAttribute`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostComponentAttribute.html) | 在 `ComponentType` 结构定义上使用 `GhostComponentAttribute`，可以声明组件存在于预制体的哪些版本、是否为子实体序列化组件，以及组件复制到哪些客户端。重要：添加 `GhostComponentAttribute` 不会使组件字段自动复制，必须分别使用 `GhostFieldAttribute` 标记每个字段 | [GhostComponentAttribute](ghostcomponentattribute.md) |

<a id="snapshots"></a>
## 快照

快照表示服务器上全部 Ghost 在某个网络 Tick 的状态。Netcode for Entities 按 [`NetworkTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_NetworkTickRate) 定义的频率，每个 Tick 向每个已连接客户端发送一份快照。该频率可以与 [`SimulationTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_SimulationTickRate) 不同

如果 `NetworkTickRate` 低于 `SimulationTickRate`，Netcode for Entities 会把连接划分为多个子集，并在当前 Tick 向一个子集中的各连接发送快照；下一 Tick 再向下一个子集发送，依此类推。这称为轮询方式，因为它把 `GhostSendSystem` 负载分散到多个 `SimulationTickRate` Tick

<a id="snapshot-processing"></a>
### 快照处理

Ghost 快照系统将服务器上存在的实体同步到所有客户端。为提升性能，服务器按 [Chunk](https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/components-chunk.html) 处理 Ghost，而不是逐实体处理；接收端客户端则按实体处理

两端无法都按 Chunk 处理，因为服务器某个 Chunk 中的一组实体不一定对应客户端某个 Chunk 中的同一组实体。此外，多个客户端各自拥有不同的实体与 Chunk 布局

<a id="partial-snapshots"></a>
### 部分快照

复制大量 Ghost 或 Ghost 数据时，每 Tick 快照数据大小会受到最大传输单元 MTU 上限约束。因此，一份快照只包含全部 Ghost 的一个子集是常见且符合预期的情况，这种快照称为部分快照

系统会优先添加重要度最高的 Chunk，再每次传输少量 Ghost Chunk，逐步流式发送大型 World，而不是一次发送一个巨大数据包。该过程实际上是一个按重要度排序的优先队列

还可以使用 `MaxSendRate` 减少每份快照的重要度优先队列需要考虑的 Ghost Chunk 数量，从而降低总带宽消耗

可以修改快照最大大小。减小上限会节省带宽，但相对标头开销更高、可用数据更少；增大上限可能导致每份快照需要发送多个 UDP 数据包，从而增加丢包概率

详细信息请参阅[重要度缩放](optimizations.md#importance-scaling)文档

<a id="snapshot-visualization-tool"></a>
### 快照可视化工具

可以使用 Network Debugger 快照可视化工具，了解通过网络发送的内容

前往 __Multiplayer__ > __Open NetDbg__ 打开该工具。工具会在浏览器窗口中打开，并为每份收到的快照显示一条竖条，同时分解显示快照的关键信息

若要查看某份快照的详细信息，请选择对应竖条

<img src="images/snapshot-debugger.png" width="1000" alt="Net Debugger 工具">

> [!NOTE]
> 此工具目前是原型版本

## 其他资源

- [使用 RPC 通信](rpcs.md)
- [`NetworkTickRate` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_NetworkTickRate)
- [`SimulationTickRate` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_SimulationTickRate)
- [使用 GhostField 进行序列化与同步](ghostfield-synchronize.md)
- [使用 `GhostComponentAttribute` 自定义复制行为](ghostcomponentattribute.md)
- [使用 `GhostComponentVariationAttribute` 创建复制模式](ghost-variants.md)
- [生成 Ghost](ghost-spawning.md)
- [Ghost 类型模板](ghost-types-templates.md)
