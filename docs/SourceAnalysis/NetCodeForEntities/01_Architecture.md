# NetCode for Entities 1.9.0 架构分析

[返回源码分析目录](../README.md) | [返回项目架构总览](../../Architecture/README.md)

> 分析版本：`com.unity.netcode 1.9.0`
>
> Unity 版本：`6000.2.7f2`
>
> 嵌入源码：`Packages/com.unity.netcode`
>
> 包指纹：`7906a2acb8d5b3605b4a3c98b2df8475fc1b9402`
>
> 阅读日期：`2026-07-21`

## 1. 结论先行

NetCode for Entities 不是一个独立于 ECS 的网络对象框架。它把网络连接、输入、快照历史、预测状态和复制元数据都表示为 Entity 数据，再用一组具有严格更新顺序的 System 驱动。Transport 只负责字节传输，NetCode 在其上建立 Tick 时间轴、连接状态机、Command、RPC 和 Ghost 快照三条数据通道。

整个架构可以概括为五层：

1. `ClientServerBootstrap` 创建 Client、Server 和 Thin Client World，并给各 World 安装不同的 Rate Manager
2. `NetworkStreamReceiveSystem` 管理 Transport 驱动、连接生命周期、握手和入站数据分流
3. Command、RPC、Snapshot 三种协议分别解决连续输入、离散消息和持续状态复制
4. Ghost 烘焙与源码生成器在编译期确定可复制字段、序列化器、Prefab 变体和运行时元数据
5. 客户端依据 `NetworkTime` 在插值时间线显示远端状态，并在预测时间线上执行回滚和重模拟

最复杂的部分不是 Socket 或 RPC，而是 `Snapshot` 子系统。它同时处理 Ghost 类型注册、Prefab 同步、相关性与优先级、基线差分、动态 Buffer、生成和销毁、预测与插值分类、历史快照以及回滚状态恢复。

## 2. 本次阅读范围

本次阅读覆盖嵌入包中的全部源码目录并建立文件索引：

- `Runtime` 共 231 个 C# 文件，包括运行时、Authoring、源码生成器源码和生成模板
- `Editor` 共 38 个 C# 文件，包括 Ghost Inspector、播放模式工具、可视化和 Profiler
- `Tests` 共 97 个 C# 文件，包括连接、Command、RPC、Snapshot、预测、预生成 Ghost、物理回溯和主机迁移测试

运行时源码约五万行。体量最大的模块依次是 `Snapshot`、`SourceGenerators`、`Connection`、`Authoring`、`PredictionTicking`、`Command` 和 `Rpc`。本文重点沿真实执行链路分析这些模块，并对 Physics、HostMigration、StateSave、Editor 和 Tests 的边界进行归纳。

本文不逐项解释每个公开 API。需要确认字段级行为时，应以当前嵌入源码和对应测试为准。

## 3. 程序集与目录边界

核心运行时程序集是 [`Unity.NetCode`](../../../Packages/com.unity.netcode/Runtime/Unity.NetCode.asmdef)。它依赖 Entities、Burst、Collections、Transport、Scenes、Transforms 等基础包，并允许 Unsafe 代码。连接、时间、Command、RPC、Ghost 复制、预测、主机迁移和状态保存均在这个程序集内。

外围程序集承担明确的桥接职责：

- [`Unity.NetCode.Hybrid`](../../../Packages/com.unity.netcode/Runtime/Hybrid/Unity.NetCode.Hybrid.asmdef) 提供托管组件和 GameObject 表现桥接，不属于核心协议
- [`Unity.NetCode.Authoring.Hybrid`](../../../Packages/com.unity.netcode/Runtime/Authoring/Hybrid/Unity.NetCode.Authoring.Hybrid.asmdef) 提供 `GhostAuthoringComponent`、Baker 和 Prefab 元数据生成
- [`Unity.NetCode.Physics`](../../../Packages/com.unity.netcode/Runtime/Physics/Unity.NetCode.Physics.asmdef) 在安装 Unity Physics 时启用，提供预测物理组和碰撞世界历史
- `Unity.NetCode.Physics.Hybrid` 提供物理配置的 Authoring 桥接
- `Unity.NetCode.Editor`、`Unity.NetCode.Editor.Drawers` 和 `Unity.NetCode.Profiler.Editor` 只在编辑器中工作

这个划分说明核心协议不依赖项目 GameObject 表现。项目代码应优先依赖公开的 `Unity.NetCode` 数据和 System Group，不应通过反射或程序集友元关系访问包内 `internal` 类型。

## 4. 编译期架构

### 4.1 源码生成器

NetCode 的序列化不是在运行时扫描字段。Roslyn 生成器 [`NetCodeSourceGenerator`](../../../Packages/com.unity.netcode/Runtime/SourceGenerators/Source~/NetCodeSourceGenerator/Generators/NetCodeSourceGenerator.cs) 在每个引用 `Unity.NetCode` 的程序集编译时运行。

生成流程如下：

```text
C# 语法树
  -> NetCodeSyntaxReceiver 收集候选 struct 与 Variant
  -> 语义模型确认 IComponentData / IBufferElementData / ICommandData /
     IInputComponentData / IRpcCommand
  -> TypeInformationBuilder 展开字段、属性、量化和发送规则
  -> ComponentFactory / CommandFactory / InputFactory / RpcFactory
  -> 模板生成 Serializer、注册系统、发送与接收系统、输入复制系统
  -> Roslyn AddSource 加入当前程序集编译
```

生成器只先用语法做低成本筛选，随后使用语义模型确认接口和 Attribute。`[GhostField]`、`[GhostComponent]`、Variant、Buffer、Enableable bit 和量化信息最终都会进入生成代码。若业务程序集包含网络类型，却没有显式引用 Burst、Collections 或 Mathematics，生成器会直接报告缺失程序集引用。

这也是项目曾出现生成 RPC Serializer Burst 警告的相关位置：业务类型会在自己的 asmdef 内生成静态 Serializer 和执行入口，程序集边界、泛型实例化和 Burst 入口识别都会影响最终编译结果。具体警告仍应结合生成文件和 Burst 编译日志判断，不能只凭业务 RPC 声明归因。

### 4.2 生成的主要产物

不同声明产生的代码职责不同：

- `IComponentData` 或 `IBufferElementData` 加 `[GhostField]` 后生成 Ghost Component Serializer 和注册信息
- `ICommandData` 生成命令序列化器以及客户端发送、服务端接收系统
- `IInputComponentData` 额外生成隐藏的 `InputBufferData<T>`，以及输入组件与命令 Buffer 之间的复制系统
- `IRpcCommand` 生成 RPC Serializer、请求处理系统和 Burst 可调用执行函数
- `GhostComponentVariation` 生成替代序列化策略，并用稳定 Hash 参与 Prefab 元数据

运行时不会重新推导这些规则。Ghost Collection 只消费生成器注册好的 Serializer 和烘焙好的 Prefab 元数据。

## 5. World 创建与系统骨架

[`ClientServerBootstrap`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerBootstrap.cs) 实现 `ICustomBootstrap`，负责从全部系统类型中筛选并创建不同 World：

- Server World 使用 `WorldFlags.GameServer`
- Client World 使用 `WorldFlags.GameClient`
- Thin Client World 只保留轻量客户端模拟
- Single World Host 在该版本仍属于实验路径

World 创建后，包内 `ConfigureServerWorldSystem` 和 `ConfigureClientWorldSystem` 会安装对应 Rate Manager，并把全局 `NetCodeConfig` 应用到 TickRate、发送预算和客户端预测配置。项目自定义 Bootstrap 负责决定创建哪些 World，但不需要重复安装这些内部系统。

运行时主要顺序可以压缩为：

```text
Unity PlayerLoop
└─ SimulationSystemGroup
   ├─ BeginSimulationEntityCommandBufferSystem
   ├─ GhostSpawnSystemGroup
   │  └─ 完成上一轮已经分类且到达时间线的 Ghost 生成
   ├─ NetworkReceiveSystemGroup
   │  ├─ Connect / Listen 请求
   │  ├─ NetworkStreamReceiveSystem
   │  ├─ CommandReceiveSystemGroup
   │  └─ 连接与入站协议处理
   ├─ GhostSimulationSystemGroup
   │  ├─ GhostCollection 与 Prespawn
   │  ├─ GhostReceiveSystem
   │  ├─ GhostUpdateSystem
   │  ├─ GhostSpawnClassificationSystemGroup
   │  ├─ GhostInputSystemGroup
   │  └─ CommandSendSystemGroup
   ├─ PredictedSimulationSystemGroup
   │  ├─ PredictedFixedStepSimulationSystemGroup
   │  ├─ 项目预测玩法系统
   │  └─ GhostPredictionHistorySystem
   ├─ 普通 Simulation 与 FixedStep 系统
   ├─ GhostSendSystem                 Server OrderLast
   └─ RpcSystem                       Simulation OrderLast
```

`GhostSimulationSystemGroup` 和 `PredictedSimulationSystemGroup` 都是 `SimulationSystemGroup` 的早期子组，但前者明确排在后者之前。网络接收又排在 Ghost Simulation 之前，因此新快照可以在同一帧影响随后的预测循环。

## 6. Tick 与时间模型

[`NetworkTick`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTick.cs) 用一个 `uint` 保存 Tick，并保留有效位。所有 Tick 比较都通过封装方法处理回绕，不能把原始 `uint` 当普通递增整数直接比较。

[`NetworkTime`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTime.cs) 同时暴露三条时间信息：

- `ServerTick` 是当前模拟或预测的服务器 Tick
- `InputTargetTick` 是客户端输入应写入的目标 Tick
- `InterpolationTick` 是远端插值 Ghost 当前显示的历史 Tick

它还记录 Tick Fraction、批处理步长和预测循环标记，例如是否处于预测循环、是否为首次或最终预测 Tick、是否为补赶 Tick。

服务端 Rate Manager 依据 `SimulationTickRate` 累积帧时间。落后时可以在一帧内执行多个模拟步骤，也可以受 `MaxSimulationStepsPerFrame` 和 `MaxSimulationStepBatchSize` 约束，将多个逻辑 Tick 合为一个较大的 `DeltaTime` 步骤。批处理降低补赶成本，但会减少中间状态，要求系统正确使用 `SimulationStepBatchSize` 和网络时间，而不是假设一次更新永远只代表一个 Tick。

客户端 [`NetworkTimeSystem`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTimeSystem.cs) 根据收到的最新快照、RTT、Command Age 和配置估算服务器时间线与插值时间线，并用轻微加速或减速平滑误差。预测 Rate Manager 再根据新快照中实际应用到 Predicted Ghost 的 Tick，决定从哪个历史点回滚并重放到当前预测 Tick。

## 7. 连接实体与协议分流

[`NetworkStreamReceiveSystem`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkStreamReceiveSystem.cs) 是连接层中心。它创建和更新 `NetworkStreamDriver`，处理监听、连接、断线、迁移、握手、可选审批以及 Transport 事件。

每条连接在 ECS 中都是一个 Entity。其常见数据包括：

- `NetworkStreamConnection` 保存底层连接句柄
- `ConnectionState` 保存连接、握手、审批、已连接和断开状态
- `NetworkId` 是服务器分配的会话内连接 ID
- `NetworkSnapshotAck` 保存快照确认位图、RTT、Command Age、Prefab 加载数和包丢失信息
- `CommandTarget` 指向该连接默认控制的 Ghost
- Incoming/Outgoing Buffer 保存 Command、RPC 和 Snapshot 字节流
- `NetworkStreamInGame` 明确连接已进入游戏数据交换阶段

协议首字节由 [`NetworkStreamProtocol`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkStreamProtocol.cs) 标识。运行时只有三类业务数据：

```text
Command   客户端 -> 服务端   连续、按 Tick、允许丢包、携带历史冗余
Snapshot  服务端 -> 客户端   连续状态复制、允许丢包、基线差分
RPC       双向               离散消息、可靠有序
```

接收系统只负责识别并写入对应 Buffer。后续专用系统在正确的 System Group 中反序列化。Snapshot 入站 Buffer 只保留尚未处理的最新快照，因为更旧的状态到达后已没有继续应用的价值。

握手阶段会比较 NetCode 版本、Game Protocol Version、RPC Collection Hash 和 Ghost Component Collection Hash。任一不一致都可能主动断开，说明网络类型和序列化 Variant 的变化本质上也是协议变更。

## 8. Command 输入链路

Command 用于高频、连续、可预测的客户端输入。`ICommandData` 自带 `Tick`，通常作为目标 Ghost 上的 Dynamic Buffer 保存，内部最多维护 64 个 Tick 的环形历史。

标准链路如下：

```text
设备输入
  -> GhostInputSystemGroup
  -> ICommandData Buffer[InputTargetTick]
  -> 生成的 CommandSend System 序列化当前命令和历史冗余
  -> OutgoingCommandDataStreamBuffer
  -> CommandSendPacketSystem
  -> Transport unreliable pipeline
  -> IncomingCommandDataStreamBuffer
  -> 生成的 CommandReceive System 写入服务端目标 Ghost Buffer
  -> PredictedSimulationSystemGroup 按 ServerTick 读取并模拟
```

一个 Command 包除输入外，还携带客户端的 Snapshot Ack、RTT、已加载 Ghost Prefab 数和插值延迟。服务端因此能在处理输入的同时更新对该客户端复制状态的认识。

不可靠传输并不等于只发送当前一条命令。系统会发送当前命令和若干历史命令，并对连续值做差分压缩。`TargetCommandSlack` 与 `NumAdditionalCommandsToSend` 控制冗余窗口。

`CommandTarget` 适合一个连接控制一个主要 Ghost。`AutoCommandTarget` 可让多个属于同一连接的 Ghost 自动发送各自命令。边沿输入应使用计数器式 `InputEvent` 或等价设计，避免一帧按钮脉冲在帧率、TickRate 和重传窗口不同步时丢失。

## 9. RPC 链路

RPC 用于低频、离散、需要可靠顺序的消息，例如进入游戏、房间操作或一次性通知。业务代码创建同时带有 RPC 数据和 `SendRpcCommandRequest` 的临时 Entity；生成系统将其序列化到 `OutgoingRpcDataStreamBuffer`，[`RpcSystem`](../../../Packages/com.unity.netcode/Runtime/Rpc/RpcSystem.cs) 在帧末通过可靠管线发送。

接收端根据 RPC Collection 中注册的稳定类型 Hash 找到 Burst 函数指针，执行生成的反序列化入口，并创建带 RPC 数据和 `ReceiveRpcCommandRequest` 的临时 Entity。业务系统消费后应销毁该 Entity。

RPC 的可靠顺序只对其他 RPC 成立，不保证与 Snapshot 或 Command 的跨通道顺序。如果某条 RPC 依赖某个 Ghost 已经生成，应在业务层检查 Ghost 是否可用、携带 Tick 或建立显式等待状态，不能假设“先发送 RPC，客户端就一定先看到 RPC”。

## 10. Ghost 烘焙与类型注册

### 10.1 Authoring 到 Prefab 元数据

[`GhostAuthoringComponent`](../../../Packages/com.unity.netcode/Runtime/Authoring/Hybrid/GhostAuthoringComponent.cs) 描述 Ghost 的默认模式、支持模式、重要性、优化策略、预序列化和预测回滚选项。

[`GhostAuthoringComponentBaker`](../../../Packages/com.unity.netcode/Runtime/Authoring/Hybrid/GhostAuthoringComponentBaker.cs) 首先写入临时 Baking 数据。随后的 `GhostAuthoringBakingSystem` 遍历根 Entity 与 `LinkedEntityGroup`，为每个组件选择 Serializer Variant、Prefab 存在范围和发送掩码，然后：

1. 按 Server、Interpolated Client、Predicted Client 变体决定组件保留或剥离
2. 为根与子 Entity 添加 Ghost 运行时组件
3. 计算包含 Ghost 类型、模式、组件 StableTypeHash 和 PrefabType 的内容 Hash
4. 生成 `GhostPrefabMetaData` Blob
5. 对 ClientAndServer Prefab 添加运行时剥离标记

组件是否在某个 World 存在，与组件是否参与网络序列化是两个维度。`GhostPrefabType` 决定 Prefab 变体保留范围，`GhostSendType` 决定发送给预测客户端、插值客户端还是全部客户端。

### 10.2 Ghost Collection

[`GhostCollectionSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostCollectionSystem.cs) 把已加载 Ghost Prefab 和生成器注册的 Serializer 汇总为运行时集合。关键 Buffer 包括：

- `GhostCollectionPrefab` 保存 Ghost 类型与 Prefab Entity
- `GhostCollectionPrefabSerializer` 保存每种 Ghost 的快照尺寸、ChangeMask、Enableable bit、组件范围和预测选项
- `GhostCollectionComponentIndex` 把 Ghost 内组件位置映射到具体 Serializer
- `GhostComponentSerializer.State` 保存生成器注册的序列化函数指针和组件信息

服务端会在 Snapshot 中持续发送尚未被客户端确认的 Prefab 类型 GUID 与 Hash。客户端只有在本地 Prefab 集合匹配并加载后，才可安全解码对应 Ghost。

## 11. Snapshot 发送链路

[`GhostSendSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostSendSystem.cs) 在 Server World 的 `SimulationSystemGroup` 最后运行。它先为新 Ghost 分配运行时 Ghost ID 和 SpawnTick，再按连接构建快照。

发送过程的核心步骤是：

1. 更新每条连接的 Snapshot Ack 和历史状态
2. 计算该连接可见的 Ghost 和待发送 Despawn
3. 按 Chunk 计算基础重要性、距上次发送的 Age、距离缩放和自定义缩放
4. 在包预算内选择优先级最高的 Chunk
5. 从该连接已确认的历史中选择最多三个基线
6. 写入 ChangeMask、Enableable bit、组件字段、Dynamic Buffer 和 Child Entity 数据
7. 写入新 Prefab、相关 Ghost 数和 Despawn 消息
8. 通过不可靠 Snapshot Pipeline 发送，并保存本轮历史供后续差分

复制优先级按 Chunk 计算，而不是按单个 Entity。把重要性差异很大的 Ghost 放入同一 Archetype Chunk，会让它们共享调度粒度。`MaxSendChunks`、`MaxIterateChunks`、Snapshot 包大小和发送频率共同限制服务器每连接成本。

系统默认目标是一条连接每个网络更新发送一个 Snapshot 包。配置允许多包或使用 Fragmentation Pipeline，但更大的包会增加分片、丢包和突发带宽风险。

## 12. Snapshot 接收、生成与销毁

[`GhostReceiveSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostReceiveSystem.cs) 在客户端读取最新 Snapshot，处理 Prefab 声明、相关 Ghost 数、Despawn 和 Ghost 更新。

已有 Ghost 的状态被写入 `SnapshotDataBuffer` 历史。新 Ghost 不会在反序列化时立即完成实体生成，而是先写入 `GhostSpawnBuffer`。随后：

1. `GhostUpdateSystem` 更新已有 Ghost，并确定预测回滚起点
2. `GhostSpawnClassificationSystemGroup` 决定新 Ghost 使用 Predicted 还是 Interpolated 模式
3. `GhostSpawnSystemGroup` 在下一轮 Simulation 开始前完成实例化
4. `SpawnedGhostEntityMap` 建立 `(GhostId, SpawnTick) -> Entity` 映射

Predicted Ghost 到达 ServerTick 后可立即生成。Interpolated Ghost 必须等 `InterpolationTick` 到达其 SpawnTick，期间用 `PendingSpawnPlaceholder` 继续接收更新。Despawn 也分两条时间线：预测 Ghost 等 ServerTick，插值 Ghost 等 InterpolationTick，避免远端对象在客户端视觉时间线上提前消失。

预生成 Ghost 由 [`PreSpawnedGhostsBakingSystem`](../../../Packages/com.unity.netcode/Runtime/Authoring/Hybrid/PreSpawnedGhostsBakingSystem.cs) 在 SubScene 烘焙时处理。系统用 GhostType、Authoring Transform 和 Scene GUID 生成确定性 Hash，排序后分配 SubScene 内索引，并记录 SubScene Hash。服务器与客户端加载相同 SubScene 后即可对齐实体，不必把每个场景常驻对象都当普通运行时 Spawn 发送。

## 13. 插值、预测与回滚

### 13.1 快照历史

每个 Ghost 的 `SnapshotDataBuffer` 保存固定长度历史，当前实现使用 32 个快照槽。`GetDataAtTick` 会寻找目标 Tick 前后的样本，用于插值；目标晚于最新样本时可在配置范围内外推。

Interpolated Ghost 使用 `InterpolationTick` 和 Fraction。它们通常显示在服务器当前时间之前，以历史缓冲换取平滑和抗抖动。

Predicted Ghost 使用 `ServerTick`。本地先按输入模拟，收到服务器权威快照后再检查需要从哪里重算。

### 13.2 GhostUpdateSystem

[`GhostUpdateSystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostUpdateSystem.cs) 是客户端快照应用和预测起点计算的中心：

- 对插值 Ghost，选择目标 Tick 前后快照并调用生成 Serializer 的插值逻辑
- 对预测 Ghost，比较最新 Snapshot Tick 与 `PredictedGhost.AppliedTick`
- 有新权威数据时，从对应 Snapshot Tick 或可用备份 Tick 设置 `PredictionStartTick`
- 没有新数据时，从最近完整预测备份继续，而不是无条件回到旧快照
- 把本帧实际需要重放的 Tick 记录到 `AppliedPredictedTicks`

只收到部分 Ghost 的新数据时，不必让所有 Predicted Ghost 都从同一旧 Tick 回滚。`Simulate` 是 Enableable Component，预测循环开始时 `GhostPredictionDisableSimulateSystem` 只启用本 Tick 需要模拟的 Ghost，最终预测 Tick 再统一恢复启用状态。

### 13.3 预测历史备份

[`GhostPredictionHistorySystem`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostPredictionHistorySystem.cs) 在每帧最后一个完整预测 Tick 后运行。它按 ArchetypeChunk 分配连续内存，备份预测 Ghost 的可复制组件、Buffer 和 Enableable 状态，并记录 Entity 在备份 Chunk 中的位置。

恢复时只把网络序列化字段复制回组件，不覆盖非 `[GhostField]` 的本地状态。备份同时服务于：

- 没有新 Snapshot 时从上一完整 Tick 继续预测
- 新 Snapshot 到达时恢复权威基线并重放
- 预测误差检测
- Prediction Smoothing

结构变化是最危险的边界。Entity 换 Chunk、删除后重新添加复制组件，或改变 Child Entity 结构，都可能让 Chunk 备份无法直接匹配。系统维护 Entity 到旧 Chunk/Index 的额外映射，并由 Ghost Authoring 的 `RollbackPredictionOnStructuralChanges` 决定找不到直接备份时是否退回更早权威 Snapshot。项目预测系统应尽量避免在预测循环中频繁做结构变化。

## 14. 相关性、优先级与带宽

NetCode 把“客户端是否应看到 Ghost”和“本轮是否优先发送 Ghost”分开处理：

- Relevancy 决定 Ghost 对指定连接是否存在
- Importance 决定在 Snapshot 预算不足时谁先更新

相关性变化可能触发客户端 Spawn 或 Despawn，因此不适合用作每帧距离裁剪开关。稳定可见但更新频率较低的对象更适合保留相关性，只降低 Importance。

重要性默认包含 Prefab 基础值和 Age。还可通过 `GhostConnectionPosition`、`GhostDistanceImportance` 或自定义 `GhostImportance` 缩放。由于调度以 Chunk 为单位，项目设计 Archetype 和 Shared Component 时需要考虑网络发送分组，而不只是 ECS 查询性能。

## 15. 可选子系统

### 15.1 预测物理与延迟补偿

`PredictedFixedStepSimulationSystemGroup` 是预测循环内部的固定步长组。它可按 `PredictedFixedStepSimulationTickRatio` 以高于普通预测的频率运行，并避免 Fractional Tick，适合 Character Controller 和 Unity Physics。

[`PhysicsWorldHistory`](../../../Packages/com.unity.netcode/Runtime/Physics/PhysicsWorldHistory.cs) 在 Server World 按 Tick 克隆 `CollisionWorld` 到环形历史。服务端处理客户端射击时，可用客户端插值延迟取得过去 Tick 的碰撞世界进行 Lag Compensation。Collider Blob 是否深拷贝由 `LagCompensationConfig` 控制；深拷贝范围越大，历史内存和复制成本越高。

### 15.2 主机迁移与通用状态保存

[`ServerHostMigrationSystem`](../../../Packages/com.unity.netcode/Runtime/HostMigration/HostMigrationSystem.cs) 在 `GhostSendSystem` 后采集服务器配置、连接、SubScene、Prefab、Ghost ID 分配器和 Ghost 组件数据，序列化为可交给外部 Lobby 或服务保存的 Blob。

新主机加载所需 SubScene 和 Ghost Prefab 后，系统重新生成 Ghost，恢复服务器专用组件和连接状态，并用 `ConnectionUniqueId` 把重连客户端映射回原 NetworkId。该能力需要显式添加 `EnableHostMigration`，并不默认启用。

底层 [`WorldStateSave`](../../../Packages/com.unity.netcode/Runtime/StateSave/StateSave.cs) 是一个按 Chunk 保存指定 Required/Optional 组件的通用 Unsafe 状态容器。它支持普通组件、Dynamic Buffer、Enableable bit 和自定义索引策略。Host Migration 用按 Ghost ID 建索引的策略消费它。

## 16. 调试、编辑器与测试设计

Editor 层主要提供四类工具：

- `GhostAuthoringComponentEditor` 和组件预览用于检查 Prefab 变体与序列化选择
- Multiplayer PlayMode Window 用于选择 Client、Server 和 Thin Client 组合及模拟延迟
- Importance Drawer、Bounding Box Drawer 和 Packet Dump 用于观察复制范围和字节流
- NetCode Profiler 展示 Client/Server Snapshot、预测、插值和带宽数据

测试不是简单单元测试集合。`NetCodeTestWorld` 可在 EditMode 中创建成对 Client/Server World、注册驱动、Tick 多个 World 并注入延迟。测试覆盖协议版本、连接审批、多 Driver、Command 冗余、RPC、Ghost 序列化、Buffer、Enableable bit、预测切换、部分 Snapshot、Relevancy、Prespawn、SubScene、Host Migration 和 Lag Compensation。

阅读某个高风险内部行为时，最佳证据通常是“实现文件 + 对应测试”。例如预测问题优先看 `PredictionTests`、`PredictionSwitchTests` 和 `PartialSendTests`，连接问题看 `ConnectionTests` 与 `ConnectionApprovalTests`，快照字段问题看 `GhostSerializationTests` 和 Enableable/Buffer 专项测试。

## 17. 对 AnimarsCatcher 的映射

项目现有接入方式与包架构基本一致：

- [`CustomBootstrap`](../../../Assets/Scripts/Netcode/Bootstrap/CustomBootstrap.cs) 只决定进程创建哪些 World，并关闭包内自动连接端口
- `ClientStartConnectionSystem` 和 `ServerStartListenSystem` 通过 `NetworkStreamRequestConnect`、`NetworkStreamRequestListen` 接入公开连接请求层
- [`InputCommand`](../../../Assets/Scripts/Player/Input/Common/InputCommand.cs) 使用 `ICommandData` 承载逐 Tick 玩家输入
- 客户端输入构建系统位于 `GhostInputSystemGroup`，预测移动位于 `PredictedFixedStepSimulationSystemGroup`
- Gameplay Contracts 中的 RPC 和 Ghost Component 由各自 asmdef 内的 NetCode Source Generator 生成 Serializer
- Gameplay 执行权威伤害、资源、生成和胜负，Presentation 只消费同步状态或提交请求

当前需要特别留意以下边界：

1. 大量服务器业务系统仍直接放在普通 `SimulationSystemGroup`，其先后顺序若影响权威结果，应通过项目 System Group 或显式 `UpdateBefore/After` 固化
2. RPC 不能替代逐 Tick 输入，也不能假设与 Ghost Snapshot 同步到达
3. 客户端命中 RPC 只能作为候选请求，服务器仍需校验所有权、攻击状态、距离和目标合法性
4. `PredictedSimulationSystemGroup` 内系统会在客户端一帧执行多次，不能写入不可回滚的 Mono 状态、播放一次性表现或产生无去重副作用
5. Ghost 组件或 RPC 的字段、命名空间、程序集和 Variant 改动都可能改变协议 Hash，需要客户端与服务端同步发布
6. 项目不应引用 `GhostPredictionHistoryState`、ConnectionState 内部存储等 `internal` 实现；升级包时这些部分可能无兼容承诺

## 18. 适合项目使用的扩展点

稳定且符合包设计的扩展位置包括：

- 自定义 `ClientServerBootstrap` 决定 World 组合和启动参数
- `INetworkStreamDriverConstructor` 定制 Transport Driver、Pipeline 和网络参数
- `GhostInputSystemGroup` 采集输入，`PredictedSimulationSystemGroup` 执行确定性预测
- 自定义 `GhostSpawnClassificationSystemGroup` 系统决定特定 Ghost 的预测或插值模式
- `GhostRelevancy`、`GhostImportance` 和距离重要性配置复制预算
- Ghost Variant 控制第三方组件的序列化字段和 Prefab 存在范围
- `RpcCommandRequestSystemGroup` 前后的业务系统消费离散网络请求
- `PhysicsWorldHistorySingleton` 执行服务器 Lag Compensation 查询

不建议的接入方式包括修改包内 System 顺序、持有包内 Native 容器指针、依赖生成类全名、直接写 Incoming/Outgoing 原始 Buffer，或在业务代码中复制 Ghost Serializer 协议实现。

## 19. 关键不变量与风险

维护 NetCode 代码时应始终保留以下不变量：

- 服务端是 Ghost 状态和玩法结果的最终权威
- 预测系统对同一 Tick 重放必须得到相同结果
- Tick 比较使用 `NetworkTick` API，不能直接比较序列化整数
- 输入按 Tick 存储，边沿事件不能只依赖渲染帧布尔值
- RPC、Command、Snapshot 是独立通道，没有跨通道总顺序
- Ghost Prefab、Serializer Collection 和客户端本地 Prefab 必须保持协议一致
- Predicted Ghost 的结构变化必须考虑历史备份失配
- Snapshot 预算、相关性和重要性按连接计算，服务端成本随连接数增长
- 表现副作用只在最终预测 Tick 或确认后的表现层触发
- 包内 `internal` 类型只能用于阅读实现，不能成为项目长期依赖

## 20. 推荐源码阅读顺序

需要继续深入时，建议按下面顺序阅读，避免一开始陷入 `GhostSendSystem` 的底层位流细节：

1. [`ClientServerBootstrap.cs`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerBootstrap.cs) 建立 World 概念
2. [`GhostSimulationSystemGroup.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/GhostSimulationSystemGroup.cs) 与 [`GhostPredictionSystemGroup.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/GhostPredictionSystemGroup.cs) 建立更新顺序
3. [`NetworkTime.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTime.cs) 与 [`NetworkTimeSystem.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTimeSystem.cs) 理解时间轴
4. [`NetworkStreamReceiveSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Connection/NetworkStreamReceiveSystem.cs) 理解连接实体和协议分流
5. [`CommandSendSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Command/CommandSendSystem.cs) 与 [`RpcSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Rpc/RpcSystem.cs) 理解两类消息
6. [`GhostAuthoringComponentBaker.cs`](../../../Packages/com.unity.netcode/Runtime/Authoring/Hybrid/GhostAuthoringComponentBaker.cs) 与 [`GhostCollectionSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostCollectionSystem.cs) 理解元数据来源
7. [`GhostSendSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostSendSystem.cs) 和 `GhostChunkSerializer.cs` 理解服务器快照
8. [`GhostReceiveSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostReceiveSystem.cs)、[`GhostUpdateSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostUpdateSystem.cs) 和 [`GhostPredictionHistorySystem.cs`](../../../Packages/com.unity.netcode/Runtime/Snapshot/GhostPredictionHistorySystem.cs) 理解客户端应用与回滚
9. 最后按需求阅读 Prespawn、Physics、HostMigration、Profiler 和对应 Tests

## 21. 维护方式

本文件记录 `1.9.0` 的实现事实。升级 NetCode 后至少重新核对以下文件和行为：

- `package.json`、asmdef 和依赖版本
- Bootstrap 创建的 World Flags 与 Rate Manager
- System Group 的 `UpdateInGroup/Before/After`
- Protocol Version Hash 的组成
- Command 历史长度和发送冗余
- Snapshot 历史槽数、基线数量、包预算和 Despawn 策略
- Prediction Backup 与结构变化处理
- Source Generator 的候选接口、模板和生成类结构

新增专项分析时放在本目录，文件名按 `02_主题.md` 继续编号。专题文档应引用具体源码和测试，不复制大段第三方代码。
