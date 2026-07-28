# Ghost 组

使用 Ghost 组同步多个 Ghost 实例的复制时序，避免常见的游戏状态错误

<a id="ghost-group-usage"></a>
## Ghost 组用法

<a id="configure-a-ghost-group"></a>
### 配置 Ghost 组

若要创建 Ghost 组，需要先定义 Ghost 组根节点，再定义该根节点的子项

1. 在创作阶段，通过 **GhostAuthoringComponent** Inspector 中的 **Ghost Group** 开关，为一个 Ghost 预制体添加 [`GhostGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostGroup.html) 缓冲区
   这会将该预制体定义为 Ghost 组根节点，并允许其他 Ghost 实例加入该组
2. 对每个 Ghost 组子实例执行以下操作
    1. 为该子项添加 [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) 组件
    2. 将子 Ghost 的 `Entity` 添加到根节点的 `GhostGroup` 缓冲区

<a id="ghost-group-behaviour"></a>
### Ghost 组行为

* 每当发送根 Ghost 实体时，系统保证同时发送加入该根节点的全部 Ghost 组子项
* Ghost 组根节点隐式定义为具有 `GhostGroup` 缓冲区组件、但没有 `GhostChildEntity` 组件的 Ghost 实例

<a id="ghost-group-limitations"></a>
### Ghost 组限制

* 每个组只能有一个根 Ghost 实体
* Ghost 组不支持嵌套，即一个 Ghost 组条目不能同时属于多个 Ghost 组
* Ghost 组子实体位于组内时不支持独立设置 `Relevancy`。它们继承根 Ghost 实体的相关性，将子项标记为不相关不会产生效果

> [!NOTE]
> 截至 2025 年 3 月存在一个已知问题：如果相关的 Ghost 组根节点变为不相关，其子项目前不会随之变为不相关，而会残留在客户端

* Ghost 组不支持 `GhostOptimizationMode.Static`。也不能在根节点为 `Dynamic` 时将子项标记为 `Static`，反之亦然。即使创作时设为 `Static`，全部 `GhostGroup` Ghost 也会被强制设为 `Dynamic`
* 子 Ghost 属于组时，其 `Importance`、`GhostImportance`、`Importance Scaling` 和 `Max Send Rate` 均会被忽略，只使用 `GhostGroup` 根节点所在 Ghost Chunk 的值
* `GhostGroup` 序列化相对较慢，因为系统必须逐个访问每个子项所在的 Chunk。这与复制 `Unity.Transforms.Child` 子 Ghost 实体组件上的 `GhostField` 较慢类似。使用 Ghost 组时应评估其潜在性能影响
* 序列化 `GhostGroup` 条目可能使单份快照超过默认的 `NetworkParameterConstants.MaxMessageSize`，从而增加快照数据包分片的频率

> [!NOTE]
> 出于性能原因，错误使用 `GhostGroup` 时系统不会报告错误

<a id="ghost-group-example-use-case"></a>
## Ghost 组示例用例

假设第一人称射击游戏中有一个 `Player` 角色控制器 Ghost，它可以拾取、丢弃和携带三个独立的 `Gun` Ghost 实例
玩家携带枪械时，每把枪通过模拟父子关系的方式附着在角色身体的不同位置，并可以驱动角色的手部动画状态

资源可能如下所示：

```txt
“Player”Ghost 预制体
Importance:100, Max Send Rate:60, Owner Predicted, Has Owner, Dynamic,
RelevantWithinRadius:1km, DynamicBuffer<GhostGroup> (Count:0)
```

```txt
“Gun”Ghost 预制体
Importance:10, MaxSendRate:10, Owner Predicted, Has Owner, Static,
RelevantWithinRadius:200m
```

<a id="without-ghost-groups"></a>
### 不使用 Ghost 组

不使用 Ghost 组时，游戏过程中可能出现以下问题：

* 其他玩家观察你拾取并开枪时，会先看到角色手部动画更新，随后枪才实际移动到手中
* 手持枪械可能无法准确跟随玩家手部的位置和旋转
* 其他玩家甚至可能看到开火特效从地面上的枪，或正在被拾取的枪上出现
* 远处玩家可能看起来没有持枪，因为相关性和/或重要度差异导致枪械延迟生成
* 如果客户端 `Gun` 系统错误地假定 `Gun` 实体的 `HoldingPlayer` 实体引用始终存在，可能抛出异常。例如，上一份快照已经删除玩家，但该 `Gun` 的删除尚未复制，反之亦然

<a id="with-ghost-groups"></a>
### 使用 Ghost 组

在此示例中使用 Ghost 组：

1. 在 **GhostAuthoringComponent** Inspector 中勾选 **GhostGroup**，为 `Player` Ghost 添加 `GhostGroup` 缓冲区
2. 运行时拾取枪械实例时，将该 `Gun` Ghost 实体添加到 `Player` 的 `GhostGroup` 缓冲区
3. 为该 `Gun` 实例添加 [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) 组件
4. 丢弃枪械时，以相同方式将其从 `Player` 的 `GhostGroup` 缓冲区移除，并从已经丢弃的枪上移除 [`GhostChildEntity`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostChildEntity.html) 组件

这样会使 `Player` Ghost 实例成为 Ghost 组根节点，每个被拾取的 `Gun` Ghost 实例成为 Ghost 组子项
之后，每次复制 `Player` Ghost 实例时，系统也会复制每个 `Gun` Ghost 实例，从而避免[不使用 Ghost 组](#without-ghost-groups)一节所述问题

这还意味着：

* 每个 `Gun` 的有效 `Importance` 变为 100，`Max Send Rate` 变为 60，与 `Player` Ghost 组根节点相同
* 每个 `Gun` Ghost 实例不再使用静态优化，因此不会被强制设为 `UseSingleBaseline:true`
* 只有 Ghost 组根节点与连接相关时，对应 `Gun` 实例才会被视为与该连接相关；当 `Player` 自身生成时，每把枪也会立即生成，也就是进入各连接自身 `Player` 的 1km 范围后生成

## 其他资源

* [Ghost 与快照](ghost-snapshots.md)
* [Ghost 相关性](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/optimizations.html#relevancy)
* [重要度缩放](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/optimizations.html#importance-scaling)
