# 实体清单

本文列出 Netcode 包使用的所有主要实体及其组件

<a id="connection"></a>

## 连接实体

每条网络连接都会创建一个连接实体。可以把它理解为网络 Socket，但它还包含其他 Netcode 系统所需的数据和配置

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**NetworkStreamConnection**](xref:Unity.NetCode.NetworkStreamConnection) | 用于收发数据的 Unity Transport `NetworkConnection` | 单 World Host 的本地连接除外 |
| [**NetworkSnapshotAck**](xref:Unity.NetCode.NetworkSnapshotAck) | 用于追踪已经收到的数据 | |
| [**CommandTarget**](xref:Unity.NetCode.CommandTarget) | 指向读取或写入命令的实体，目标实体必须包含 `ICommandData` 组件 | |
| [**LocalConnection**](xref:Unity.NetCode.LocalConnection) | 标识连接是否属于本地客户端或 Host | 客户端托管服务器的 Host World 可以包含多个连接实体，但只有一个带 `LocalConnection`；仅服务器 World 不包含它，包括采用双 World 模式的客户端托管服务器 |
| [**IncomingRpcDataStreamBuffer**](xref:Unity.NetCode.IncomingRpcDataStreamBuffer) | 已接收 RPC 命令的缓冲区，由 `RpcSystem` 处理，仅供内部使用 | |
| [**IncomingCommandDataStreamBuffer**](xref:Unity.NetCode.IncomingCommandDataStreamBuffer) | 已接收命令的缓冲区，由生成的 `CommandReceiveSystem` 处理，仅供内部使用 | 仅服务器 |
| [**OutgoingCommandDataStreamBuffer**](xref:Unity.NetCode.OutgoingCommandDataStreamBuffer) | `CommandSendSystem` 生成并将发送给服务器的命令缓冲区，仅供内部使用 | 仅客户端 |
| [**IncomingSnapshotDataStreamBuffer**](xref:Unity.NetCode.IncomingSnapshotDataStreamBuffer) | 已接收快照的缓冲区，由 `GhostReceiveSystem` 处理，仅供内部使用 | 仅客户端 |
| [**OutgoingRpcDataStreamBuffer**](xref:Unity.NetCode.OutgoingRpcDataStreamBuffer) | 等待 `RpcSystem` 发送的 RPC 缓冲区，仅供内部使用；业务代码应通过 `RpcQueue` 或 `IRpcCommand` 写入 RPC | |
| [**NetworkId**](xref:Unity.NetCode.NetworkId) | 唯一标识连接；不存在该组件表示连接流程尚未完成 | 连接完成时自动添加 |
| [**NetworkStreamInGame**](xref:Unity.NetCode.NetworkStreamInGame) | 表示连接应开始收发快照与命令；添加前只处理 RPC | 由游戏逻辑添加，以开始发送快照和命令 |
| [**NetworkStreamRequestDisconnect**](xref:Unity.NetCode.NetworkStreamRequestDisconnect) | 表示游戏逻辑请求关闭连接 | 由游戏逻辑在断开时添加 |
| [**NetworkStreamSnapshotTargetSize**](xref:Unity.NetCode.NetworkStreamSnapshotTargetSize) | 通知服务器 `GhostSendSystem` 使用非默认快照包大小 | 由游戏逻辑在修改快照包大小时添加 |
| [**GhostConnectionPosition**](xref:Unity.NetCode.GhostConnectionPosition) | 基于距离的重要度系统用它根据玩家距离缩放 Ghost 重要度 | 由游戏逻辑添加，用于指定该连接的玩家位置 |
| [**PrespawnSectionAck**](xref:Unity.NetCode.PrespawnSectionAck) | 服务器用它追踪客户端已经加载哪些 SubScene | 仅服务器 |
| [**EnablePacketLogging**](xref:Unity.NetCode.EnablePacketLogging) | 为单条连接启用数据包 Dump | 仅在启用数据包 Dump 时 |

<a id="ghost"></a>

## Ghost 实体

Ghost 是服务器上复制到客户端的实体。它始终由 Ghost Prefab 实例化，除了下列控制其行为的组件，还包含用户定义数据

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**Ghost**](xref:Unity.NetCode.Ghost) | 将实体标识为 Ghost | |
| [**GhostType**](xref:Unity.NetCode.GhostType) | Ghost 所属类型 | |
| **GhostCleanup** | 仅供 Netcode for Entities 内部使用，用于追踪服务器上的 Ghost Despawn | 仅服务器 |
| [**SharedGhostType**](xref:Unity.NetCode.SharedGhostType) | `GhostType` 的共享组件版本，确保不同 Ghost 类型不会共用同一 Chunk | |
| [**SnapshotData**](xref:Unity.NetCode.SnapshotData) | 保存服务器快照元数据的缓冲区 | 仅客户端 |
| [**SnapshotDataBuffer**](xref:Unity.NetCode.SnapshotDataBuffer) | 保存服务器原始快照数据的缓冲区 | 仅客户端 |
| [**SnapshotDynamicDataBuffer**](xref:Unity.NetCode.SnapshotDynamicDataBuffer) | 保存服务器发来 Buffer 原始快照数据的缓冲区 | 仅客户端且 Ghost 包含 Buffer |
| [**PredictedGhost**](xref:Unity.NetCode.PredictedGhost) | 标识预测 Ghost；服务器上的所有 Ghost 都视为预测 Ghost 并包含该组件 | 仅预测 Ghost |
| [**GhostDistancePartition**](xref:Unity.NetCode.GhostDistancePartition) | 使用基于距离的重要度时添加到所有包含 `LocalTransform` 的 Ghost | 仅基于距离的重要度 |
| [**GhostDistancePartitionShared**](xref:Unity.NetCode.GhostDistancePartitionShared) | 与上述分区对应的共享组件 | 仅基于距离的重要度 |
| [**GhostPrefabMetaData**](xref:Unity.NetCode.GhostPrefabMetaData) | 转换期间添加的 Ghost 元数据，用于配置序列化；Ghost 实例不需要，Prefab 需要，当前只会从预生成对象移除 | 非预生成对象 |
| [**GhostChildEntity**](xref:Unity.NetCode.GhostChildEntity) | 禁用该实体的独立序列化，因为它属于 Ghost Group，将作为 Group 的一部分序列化 | 仅 Ghost Group 子实体 |
| [**GhostGroup**](xref:Unity.NetCode.GhostGroup) | 添加到可作为 Ghost Group 根的 Ghost，必须在转换阶段添加到 Prefab | 仅 Ghost Group 根实体 |
| [**PredictedGhostSpawnRequest**](xref:Unity.NetCode.PredictedGhostSpawnRequest) | 表示该实例不是服务器下发的 Ghost，而是预测生成请求，客户端预计服务器随后会权威生成它；客户端 Prefab 实体引用会自动添加该组件，因此客户端自行生成的对象默认按预测生成处理 | |
| [**GhostOwner**](xref:Unity.NetCode.GhostOwner) | 通过 Network ID 标识 Ghost 拥有者 | 可选 |
| [**GhostOwnerIsLocal**](xref:Unity.NetCode.GhostOwnerIsLocal) | 可启用标签，用于追踪带 Owner 的 Ghost 是否由本地客户端或 Host 拥有；服务器 World 中行为未定义 | 可选 |
| [**AutoCommandTarget**](xref:Unity.NetCode.AutoCommandTarget) | 当 Ghost 属于当前连接、`AutoCommandTarget.Enabled` 为 `true` 且 Ghost 处于预测模式时，自动发送其全部 `ICommandData` | 可选 |
| [**SubSceneGhostComponentHash**](xref:Unity.NetCode.SubSceneGhostComponentHash) | SubScene 中所有预生成 Ghost 的组合哈希，用于排序与分组，是共享组件 | 仅预生成对象 |
| [**PreSpawnedGhostIndex**](xref:Unity.NetCode.PreSpawnedGhostIndex) | 预生成 Ghost 在 SubScene 内的唯一索引 | 仅预生成对象 |
| [**PrespawnGhostBaseline**](xref:Unity.NetCode.PrespawnGhostBaseline) | 预生成 Ghost 在 Scene 数据中的快照，用作后备 Baseline | 仅预生成对象 |
| [**GhostPrefabRuntimeStrip**](xref:Unity.NetCode.GhostPrefabRuntimeStrip) | 转换为客户端和服务器数据时添加到 Prefab 与预生成对象，触发运行时组件剥离 | 客户端和服务器 Scene 中尚未初始化的 Prefab |
| **PrespawnSceneExtracted** | 在 Editor 中打开 SubScene 编辑时，存在于 Scene Section 实体上的内部组件 | 仅 Editor |
| [**PreSerializedGhost**](xref:Unity.NetCode.PreSerializedGhost) | 为 Ghost 启用预序列化，根据 Ghost 设置在转换期间添加 | 仅使用预序列化的 Ghost |
| [**SwitchPredictionSmoothing**](xref:Unity.NetCode.SwitchPredictionSmoothing) | 预测模式切换期间临时添加，用于在预测与插值之间切换时按过渡时长平滑 Transform | 仅正在切换预测模式的 Ghost |
| [**PrefabDebugName**](xref:Unity.NetCode.PrefabDebugName) | 用于调试的 Prefab 名称 | 仅启用 `NETCODE_DEBUG` 时的 Prefab |

<a id="placeholder-ghost"></a>

### 占位 Ghost

收到 Ghost 但尚未到生成时机时，客户端会创建占位实体保存数据。占位 Ghost 仅存在于客户端

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**GhostInstance**](xref:Unity.NetCode.GhostInstance) | 将实体标识为 Ghost | |
| [**PendingSpawnPlaceholder**](xref:Unity.NetCode.PendingSpawnPlaceholder) | 将 Ghost 标识为占位对象，而不是已正式生成的 Ghost | |
| [**SnapshotData**](xref:Unity.NetCode.SnapshotData) | 保存服务器快照元数据的缓冲区 | 仅客户端 |
| [**SnapshotDataBuffer**](xref:Unity.NetCode.SnapshotDataBuffer) | 保存服务器原始快照数据的缓冲区 | |
| [**SnapshotDynamicDataBuffer**](xref:Unity.NetCode.SnapshotDynamicDataBuffer) | 保存服务器发来 Buffer 原始快照数据的缓冲区 | 仅包含 Buffer 的 Ghost |

<a id="client-only-physics-proxy"></a>

### 仅客户端物理代理

可以为预测和模拟 Ghost 上的 Collider 生成运动学副本并保持同步，使“参与物理模拟”的 Ghost 与仅客户端物理 World 中的对象交互，例如粒子、碎片和装饰性环境破坏

这种同步只能单向进行。根据定义，仅客户端物理 World 不能反过来影响服务器权威 Ghost

| 组件 | 说明 |
|---|---|
| [**CustomPhysicsProxyDriver**](xref:Unity.NetCode.CustomPhysicsProxyDriver) | 引用驱动代理的 Ghost，并配置 Ghost 与代理的同步方式 |

<a id="rpc"></a>

## RPC 实体

发送 RPC 时，业务代码会创建带发送请求的 RPC 实体。收到 RPC 后，系统会创建包含 RPC 组件和接收请求 `ReceiveRpcCommandRequest` 的实体

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**IRpcCommand**](xref:Unity.NetCode.IRpcCommand) | `IRpcCommand` 接口的具体实现 | |
| [**SendRpcCommandRequest**](xref:Unity.NetCode.SendRpcCommandRequest) | 表示需要发送该 RPC，发送后销毁 RPC 实体 | 由游戏逻辑添加，仅用于发送，自动删除 |
| [**ReceiveRpcCommandRequest**](xref:Unity.NetCode.ReceiveRpcCommandRequest) | 表示该 RPC 实体刚刚被接收并创建 | 自动添加，仅用于接收；游戏代码必须处理后删除，否则会持续向 World 泄漏实体，参阅 `WarnAboutStaleRpcSystem` |

<a id="netcode-rpcs"></a>

### Netcode 内部 RPC

| 组件 | 说明 |
|---|---|
| **ServerApprovedConnection** | 仅在连接期间发送的特殊 RPC |
| **RequestProtocolVersionHandshake** | 仅在连接期间发送的特殊 RPC |
| **ServerRequestApprovalAfterHandshake** | 仅在连接期间发送的特殊 RPC |
| **ClientServerTickRateRefreshRequest** | 仅在连接期间发送的特殊 RPC |
| **StartStreamingSceneGhosts** | 客户端加载 SubScene 后发送给服务器，要求服务器开始发送该 Scene 的预生成 Ghost |
| **StopStreamingSceneGhosts** | 客户端即将卸载 SubScene 时发送给服务器，要求服务器停止发送该 Scene 中的预生成 Ghost |

<a id="commanddata"></a>

### CommandData 实体

每条接收客户端命令的连接都需要一个实体保存命令数据。该实体可以是 Ghost、连接实体本身或其他实体

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**ICommandData**](xref:Unity.NetCode.ICommandData) | `ICommandData` 接口的具体实现，可以添加到任意实体；连接的 `CommandTarget` 必须指向包含它的实体 | |
| [**CommandDataInterpolationDelay**](xref:Unity.NetCode.CommandDataInterpolationDelay) | 可选组件，用于读取插值延迟并在服务器上实现延迟补偿；预测客户端也存在，但其插值延迟始终为 0 | 由游戏逻辑添加，仅预测模式 |

<a id="scenesection"></a>

## SceneSection 实体

使用预生成 Ghost 时，Netcode 会向包含这些 Ghost 的 `SceneSection` 实体添加组件

| 组件 | 说明 | 存在条件 |
|---|---|---|
| [**SubSceneWithPrespawnGhosts**](xref:Unity.NetCode.SubSceneWithPrespawnGhosts) | 转换期间添加，用于追踪哪些 Section 包含预生成 Ghost | |
| [**SubSceneWithGhostCleanup**](xref:Unity.NetCode.SubSceneWithGhostCleanup) | 用于追踪 Scene 卸载 | 已处理的 Section |
| [**PrespawnsSceneInitialized**](xref:Unity.NetCode.PrespawnsSceneInitialized) | 表示 Section 已处理的标签 | 已处理的 Section |
| [**SubScenePrespawnBaselineResolved**](xref:Unity.NetCode.SubScenePrespawnBaselineResolved) | 表示 Section 已解析 Baseline 的标签，此时处于部分初始化状态 | 部分处理的 Section |

<a id="netcode-created-singletons"></a>

## Netcode 创建的单例

<a id="predictedghostspawnlist"></a>

### PredictedGhostSpawnList

保存所有正在等待服务器 Ghost 的预测生成对象。编写将传入 Ghost 与预测生成对象匹配的逻辑时需要该单例

| 组件 | 说明 |
|---|---|
| [**PredictedGhostSpawnList**](xref:Unity.NetCode.PredictedGhostSpawnList) | 用于查找预测生成列表的标签 |
| [**PredictedGhostSpawn**](xref:Unity.NetCode.PredictedGhostSpawn) | 所有预测生成 Ghost 的列表 |

<a id="ghost-collection"></a>

### Ghost Collection

| 组件 | 说明 |
|---|---|
| [**GhostCollection**](xref:Unity.NetCode.GhostCollection) | 标识包含 Ghost Prefab 的单例 |
| [**GhostCollectionPrefab**](xref:Unity.NetCode.GhostCollectionPrefab) | 所有可实例化 Ghost Prefab 的列表 |
| [**GhostCollectionPrefabSerializer**](xref:Unity.NetCode.GhostCollectionPrefabSerializer) | 所有 Ghost Prefab 的序列化器列表；索引与 `GhostCollectionPrefab` 一致，但 Prefab 加载期间可能暂时较短；每项引用 `GhostCollectionComponentIndex` 列表中的一个范围 |
| [**GhostCollectionComponentType**](xref:Unity.NetCode.GhostCollectionComponentType) | 给定类型可使用的 `GhostComponentSerializer.State` 序列化器集合，内部用于配置 `GhostCollectionPrefabSerializer` |
| [**GhostCollectionComponentIndex**](xref:Unity.NetCode.GhostCollectionComponentIndex) | Prefab 序列化器索引、子实体索引与 `GhostComponentSerializer.State` 索引之间的映射，避免为使用同一组件的每个 Prefab 复制完整序列化状态 |
| [**GhostComponentSerializer.State**](xref:Unity.NetCode.GhostComponentSerializer.State) | 某组件类型与 Variant 的序列化状态，包含序列化函数指针；存在 Variant 时，同一组件类型可以有多个条目 |

<a id="spawn-queue"></a>

### Spawn Queue

| 组件 | 说明 |
|---|---|
| [**GhostSpawnQueueComponent**](xref:Unity.NetCode.GhostSpawnQueueComponent) | Ghost 生成队列标识 |
| [**GhostSpawnBuffer**](xref:Unity.NetCode.GhostSpawnBuffer) | 生成队列中的 Ghost 列表；由 `GhostReceiveSystem` 写入，`GhostSpawnSystem` 读取；运行在两者之间的分类系统可以修改生成类型，并将传入 Ghost 与预测生成 Ghost 匹配 |
| [**SnapshotDataBuffer**](xref:Unity.NetCode.SnapshotDataBuffer) | `GhostSpawnBuffer` 中新 Ghost 的原始快照数据 |

<a id="networkprotocolversion"></a>

### NetworkProtocolVersion

| 组件 | 说明 |
|---|---|
| [**NetworkProtocolVersion**](xref:Unity.NetCode.NetworkProtocolVersion) | RPC、Ghost 组件序列化器、Netcode 版本和游戏版本组成的网络协议版本；连接时验证客户端与服务器版本是否一致 |

<a id="prespawnghostidallocator"></a>

### PrespawnGhostIdAllocator

| 组件 | 说明 |
|---|---|
| [**PrespawnGhostIdRange**](xref:Unity.NetCode.PrespawnGhostIdRange) | 与某个 SubScene 关联的 Ghost ID 范围，服务器用它把该 SubScene 的预生成 Ghost 映射到正确 Ghost ID |

<a id="prespawnsceneloaded"></a>

### PrespawnSceneLoaded

该单例是一种没有 Prefab 资产的特殊 Ghost

| 组件 | 说明 |
|---|---|
| [**PrespawnSceneLoaded**](xref:Unity.NetCode.PrespawnSceneLoaded) | 服务器已加载且包含预生成 Ghost 的 Scene 集合，会作为 Ghost 复制到客户端 |

<a id="migrationticket"></a>

### MigrationTicket

| 组件 | 说明 |
|---|---|
| [**MigrationTicket**](xref:Unity.NetCode.MigrationTicket) | 使用 World 迁移时在新 World 中创建，用于触发迁移恢复阶段 |

<a id="smoothingaction"></a>

### SmoothingAction

| 组件 | 说明 |
|---|---|
| [**SmoothingAction**](xref:Unity.NetCode.SmoothingAction) | 注册平滑操作时创建的单例，用于启用平滑系统 |

<a id="networktimesystemdata"></a>

### NetworkTimeSystemData

| 组件 | 说明 |
|---|---|
| **NetworkTimeSystemData** | 保存网络时间系统状态的内部单例 |
| **NetworkTimeSystemStats** | 追踪应用到预测和插值 Tick 的时间缩放，并向 Network Debugger 报告统计数据的内部单例 |

<a id="networktime"></a>

### NetworkTime

| 组件 | 说明 |
|---|---|
| [**NetworkTime**](xref:Unity.NetCode.NetworkTime) | 包含客户端与服务器模拟循环所有时间特征的单例组件 |

<a id="netdebug"></a>

### NetDebug

| 组件 | 说明 |
|---|---|
| [**NetDebug**](xref:Unity.NetCode.NetDebug) | 用于调试日志和管理日志级别的单例；与 UnityEngine 内置日志一样，可在正式构建中工作 |

<a id="networkstreamdriver"></a>

### NetworkStreamDriver

| 组件 | 说明 |
|---|---|
| [**NetworkStreamDriver**](xref:Unity.NetCode.NetworkStreamDriver) | 保存 `NetworkDriverStore` 引用的单例，用于便捷地监听新连接或连接服务器 |

<a id="rpccollection"></a>

### RpcCollection

<a id="ghostpredictionsmoothing"></a>

### GhostPredictionSmoothing

| 组件 | 说明 |
|---|---|
| **GhostPredictionSmoothing** | 注册用于修正预测误差的平滑操作的单例 |

<a id="ghostpredictionhistorystate"></a>

### GhostPredictionHistoryState

| 组件 | 说明 |
|---|---|
| **GhostPredictionHistoryState** | 保存所有预测 Ghost 最近一次预测完整 Tick 状态的内部单例 |

<a id="ghostsnapshotlastbackuptick"></a>

### GhostSnapshotLastBackupTick

| 组件 | 说明 |
|---|---|
| **GhostSnapshotLastBackupTick** | 保存最近一次存在快照备份的完整 Tick，仅存在于客户端 World |

<a id="ghoststats"></a>

### GhostStats

| 组件 | 说明 |
|---|---|
| **GhostStats** | 表示 Network Debugger 工具是否已连接 |
| **GhostStatsCollectionCommand** | 命令的内部统计数据 |
| **GhostStatsCollectionSnapshot** | 追踪快照收发数据的内部统计数据 |
| **GhostStatsCollectionPredictionError** | 记录各 Ghost 与组件类型组合的预测统计数据 |
| **GhostStatsCollectionMinMaxTick** | 内部 Tick 范围统计数据 |
| **GhostStatsCollectionData** | 保存内部数据池及统计系统其他状态 |

<a id="ghostsendsystemdata"></a>

### GhostSendSystemData

| 组件 | 说明 |
|---|---|
| [**GhostSendSystemData**](xref:Unity.NetCode.GhostSendSystemData) | 包含 `GhostSendSystem` 全部可调设置的单例实体 |

<a id="spawnedghostentitymap"></a>

### SpawnedGhostEntityMap

| 组件 | 说明 |
|---|---|
| [**SpawnedGhostEntityMap**](xref:Unity.NetCode.SpawnedGhostEntityMap) | 保存所有已生成 Ghost 的 `SpawnedGhost` 标识到 `Entity` 引用映射的单例 |

<a id="user-create-singletons-settings"></a>

## 用户创建的配置单例

<a id="clientservertickrate"></a>

### ClientServerTickRate

| 组件 | 说明 |
|---|---|
| [**ClientServerTickRate**](xref:Unity.NetCode.ClientServerTickRate) | 服务器 Tick 率设置；服务器配置的值会自动发送并应用到客户端 |

<a id="clienttickrate"></a>

### ClientTickRate

| 组件 | 说明 |
|---|---|
| [**ClientTickRate**](xref:Unity.NetCode.ClientTickRate) | 不受服务器控制的客户端 Tick 设置，例如插值时间；应使用 `NetworkTimeSystem.DefaultClientTickRate`，不要直接使用字段默认值 |

<a id="lagcompensationconfig"></a>

### LagCompensationConfig

| 组件 | 说明 |
|---|---|
| [**LagCompensationConfig**](xref:Unity.NetCode.LagCompensationConfig) | 配置服务器延迟补偿所用的 `PhysicsWorldHistory`；没有该单例时不会运行 `PhysicsWorldHistory` |

<a id="gameprotocolversion"></a>

### GameProtocolVersion

| 组件 | 说明 |
|---|---|
| [**GameProtocolVersion**](xref:Unity.NetCode.GameProtocolVersion) | 连接时用于协议验证的游戏专属版本；不存在时使用 0，但仍会验证 Netcode 版本、Ghost 组件和 RPC |

<a id="ghostimportance"></a>

### GhostImportance

| 组件 | 说明 |
|---|---|
| [**GhostImportance**](xref:Unity.NetCode.GhostImportance) | 控制重要度设置的单例组件 |

<a id="ghostdistancedata"></a>

### GhostDistanceData

| 组件 | 说明 |
|---|---|
| [**GhostDistanceData**](xref:Unity.NetCode.GhostDistanceData) | 基于距离的重要度配置；没有该单例时不使用基于距离的重要度 |

<a id="predicted-physics"></a>

### 预测物理

| 组件 | 说明 |
|---|---|
| [**PredictedPhysicsNonGhostWorld**](xref:Unity.NetCode.PredictedPhysicsNonGhostWorld) | 指定用于模拟仅客户端物理实体的物理 World 的单例组件 |

<a id="netcodedebugconfig"></a>

### NetCodeDebugConfig

| 组件 | 说明 |
|---|---|
| [**NetCodeDebugConfig**](xref:Unity.NetCode.NetCodeDebugConfig) | 创建该单例可为所有连接配置日志级别和数据包 Dump；若只为部分连接启用 Dump，使用连接上的 `EnablePacketLogging` |

<a id="disableautomaticprespawnsectionreporting"></a>

### DisableAutomaticPrespawnSectionReporting

| 组件 | 说明 |
|---|---|
| [**DisableAutomaticPrespawnSectionReporting**](xref:Unity.NetCode.DisableAutomaticPrespawnSectionReporting) | 禁用客户端已加载 SubScene 的自动追踪；创建该单例后，必须自行实现逻辑，确保服务器不会发送客户端尚未加载的预生成 Ghost |
