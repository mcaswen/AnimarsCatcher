---
uid: changelog
---

## [1.9.0] - 2025-09-15

### 新增

* 为 `ToFixedString` 方法补充 `GenerateTestsForBurstCompatibility` 测试覆盖及 `ToString` 重载
* **行为破坏性变更：** 新增 `GhostSendSystemData.PercentReservedForDespawnMessages`，表示快照容量中为 Ghost Despawn 消息预留的百分比，默认值为 33%，即三分之一；它替代内部固定的 100 个 Ghost 上限
* 新增可单击链接，引导用户关闭 Batched Tick 警告
* PlayMode Tools 窗口新增 `+` 和 `-` 按钮，用于自动创建或移除瘦客户端
* 为 `NetworkDriverStore` 和 `NetworkDriverStore.Concurrent` 的 Driver 访问器增加边界检查

### 变更

* 平均略微降低快照中 Ghost `GID` 和 `SpawnTick` 的带宽消耗
* **API 破坏性变更：** 完全废弃 `PrefabDebugName.Name`，从而缩小 Archetype Chunk，提高每个 Chunk 可容纳的 Ghost 实例数
* 更新 `GhostOwnerIsLocal` 最佳实践：仅服务器 World 中的行为现为未定义，未来可能更改；它只应在客户端逻辑中使用。预测逻辑需要查找拥有的 Ghost 时，应剥离输入组件，使其只存在于预测 Ghost
* 更新 `NetcodeServerRateManage.WillUpdate` 最佳实践：服务器侧现应使用 `NetworkTime.NumPredictedTicksExpected`
* 为即将推出的 Single World Host 功能进行大量内部重构
* 重要度可视化器设置名称由 “Per Entity Spatial Chunk Structure” 改为 “Per Chunk”；行为不变，只让名称更准确反映可视化的底层数据
* 尽可能把 `NetworkDriverStore` 方法改为 `readonly`

### 修复

* 修复在忽略组件启用状态时使用 `Simulate` 的分析器警告，使 `SystemAPI.Query().WithAll<Simulate>()` 等调用能被正确检测
* 修复收集 Host Migration 数据时 Ghost 数据写入可能失败的问题，缓冲区现在始终能正确扩容
* 修复 `ImportanceDrawerSystem.cs` 导致的 `Allocator.Persistent` 内存泄漏
* `ClampPartialTicksThreshold` 现在能在 `NetCodeConfig` 中正确显示
* **行为破坏性变更：** Ghost Despawn 消息现按轮询优先级加入快照，同一 Ghost 最多同时有 2 条 Despawn 消息处于传输中。旧行为是每份快照最多发送 100 个 Ghost ID，每条 Despawn 连续发送最多 5 次后才加入下一批 100 条。Delta 压缩也得到显著改善，新方案大幅提高 Despawn 吞吐量并降低带宽消耗
* **行为破坏性变更：** `DefaultSnapshotPacketSize` 最小值由 1 字节提高到 100 字节
* 修复 Ghost Despawn 消息处理错误，该错误会导致遗漏 Despawn 和少见的快照错误
* 强化快照接收逻辑，要求 `dataStream.GetBitsRead()` 精确一致；同时修复 Chunk 尝试向快照写入首个 Ghost，但因超过 Stream 容量而失败时的一处无害错误
* 修复重要度可视化可能产生的依赖错误
* 修复 PlayMode Tool 文档页中的损坏表格



## [1.8.0] - 2025-08-17

### 新增

* PlayMode Tool 新增 Importance Visualizer Drawer，用于观察重要度缩放结果
* 新增 `GhostDistancePartitioningSystem.AutomaticallyAddGhostDistancePartitionSharedComponent`，允许退出“为所有有效 Ghost 实例添加 `GhostDistancePartitionShared`”的默认行为，从而通过该共享组件是否存在来过滤重要度缩放，而无需替换整套实现
* 通过 NetcodeSamples 测试改善 `GhostImportance.BatchScaleImportanceFunction` 和 `GhostImportance.ScaleImportanceFunction` 的覆盖，尤其覆盖只为部分 Ghost 添加 `GhostDistancePartitionShared` 或用户等效组件并将其用作过滤器的情况
* 通过 `ClientTickRate.ForcedInputLatencyTicks` 支持强制输入延迟；新增 `NetworkTime.InputTargetTick`、`NetworkTime.EffectiveInputLatencyTicks` 和输入系统 `ApplyCurrentInputBufferElementToInputDataForGatherSystem<TInputComponentData, TInputHelper>`，以正确处理 `IInputComponentData` 增量值
* 新增 `NetworkTime.NumPredictedTicksExpected`，表示客户端本次预测循环更新预计运行的未批处理预测 Tick 数
* [实验性] 为 Unity Profiler 窗口新增服务器和客户端 Profiler Module，作为 Web Profiler 的替代方案；新增统计项并支持组件级统计，需要 Unity 6 或更高版本，并定义 `NETCODE_PROFILER_ENABLED` 启用

### 变更

* **行为破坏性变更：** `GhostDistanceImportance` 缩放函数不再把 `baseImportance` 乘以 1000，该操作现由 `GhostSendSystem` 自动执行，参阅新常量 `GhostSystemConstants.ImportanceScalingMultiplier`；这消除了使用与不使用重要度缩放的 Ghost Chunk 之间最后一处 1000 倍差异
* 调用 `AddCommandData` 的输入系统现应写入 `InputTargetTick`，不再写 `NetworkTime.ServerTick`。只有遇到输入延迟时两者才不同，参阅 `ForcedInputLatencyTicks` 和 `MaxPredictAheadTimeMS`
* 扩展 `NetworkTime.ToFixedString` 输出，包含新的强制输入延迟数据
* `com.unity.transport` 依赖由 2.4.0 更新到 2.5.3

### 修复

* 当客户端 RTT 高于 `MaxPredictAheadTimeMS` 时，现在会正确为客户端增加强制输入延迟，不再持续发生错误预测
* 修复 Buffer 与预测切换剥离可能导致的崩溃：Prefab 包含只用于预测或插值且大小非零的 `IBufferElementData` 时，恢复 Buffer 的 Prefab 值使用了错误长度，可能导致 `memcpy` 覆盖内存



## [1.7.0] - 2025-07-29

### 新增

* 预测循环中的查询如果使用 `EntityQueryOptions.IgnoreComponentEnabledState` 忽略启用状态，同时涉及 `Simulate`，现在会显示警告

### 变更

* 移除用于隐藏 Host Migration 功能的 `ENABLE_HOST_MIGRATION` 宏，功能现默认启用；不依赖 Host Migration 的 `NetworkStreamIsReconnected` 组件也默认启用
* 重构 Host Migration API
  * 移除 `MigrateDataToNewServerWorld` 和 `ConfigureClientAndConnect` 辅助函数，改为在文档和示例中提供实现
  * 将 `HostMigrationUtility` 重命名为 `HostMigrationData`；由于该类只包含数据方法，将 `GetHostMigrationData` 和 `SetHostMigrationData` 简化为 `Get` 和 `Set`，参数会体现数据 Buffer 与 World 的方向；移除 `TryGetHostMigrationData`，Native List 版本改用 `Get`
  * `DataStorageMethod` 只剩一个枚举值，因此移除
* Host Migration 的 Ghost 组件序列化方法改为性能显著更好的实现

### 修复

* 修复 `GhostPlayableBehaviour` 未调用 `PreparePredictedData`，导致 `GhostAnimationController` 无法工作的问题
* 修复 `NetCodeConfig.EnableClientServerBootstrap` 在 `NetCodeConfig` 中不可见的问题
* 修复 WebGL Player 无法连接非 WebGL 平台 Player，或接收其连接的问题

## [1.6.2] - 2025-07-07

### 新增

* Netcode 数据包 `timestamp` 日志现以 `[Fr{0}]` 格式附加 `UnityEngine.Time.frameCount`

### 变更

* 客户端现通过 Command Data 发送额外的 Command Tick 信息，明确当前 Command 属于完整还是部分更新或 Tick，从而改善时间同步并减少错误预测

### 修复

* 在 Prefab 编辑模式中打开 Prefab，而不只是选中时，现在也能正确添加 `GhostAuthoringComponent`
* 修复静态优化、非预生成 Ghost 首次相对 `default(T)` Baseline 序列化为“零变更”时无法在客户端生成的问题；此前只有对象发生变化后才会首次发送
* **项目破坏性变更：** 重新生成 `Packages/com.unity.netcode/Tests/Editor/Physics/Unity.NetCode.Physics.Editor.Tests.asmdef` 的 GUID，避免与 `Packages/com.havok.physics/Plugins/Android/Havok.Physics.Plugin.Android.asmdef` 冲突。按 GUID `d8342c4acf8f78e439367cff1a5e802f` 引用 **Unity.NetCode.Physics.Editor.Tests** 的程序集必须改为 `bec3f262d6e6466eb2c61661da550f47`
* 修复客户端与服务器时间同步不正确的问题，在 IPC 下尤其明显，会产生多个副作用
  * 客户端通常只为部分 Tick 向服务器发送 Command，不为完整 Tick 发送，导致错误预测
  * 客户端略落后于服务器，会提前收到新快照，并跳过一帧或多帧 `PredictedSimulationSystemGroup`，造成明显抖动
* **潜在行为破坏性变更：** 客户端和服务器的预生成实例现在都会为 `GhostInstance.GhostType` 设置相同的有效值；此前服务器侧始终保留初始值 -1，从未初始化。两端行为现更加一致

## [1.6.1] - 2025-05-28

### 新增

* 新增 `BeginPredictedSimulationCommandBufferSystem` 和 `EndPredictedSimulationCommandBufferSystem`，分别在 `PredictedSimulationSystemGroup` 开始和结束时运行
* 新增内部 `PredictedSpawningSystemGroup`，在 `EndPredictedSimulationCommandBufferSystem` 后运行，确保收到服务器新快照时，所有新 Ghost 都已生成并准备接收数据
* 新增 `NetworkDriverStore` 架构、配置及其与 Unity Relay 配合使用的文档
* 新增实验性 Host Migration 功能，通过 `ENABLE_HOST_MIGRATION` 启用，否则隐藏
* 定义 `ENABLE_HOST_MIGRATION` 后，客户端断开再重连服务器时，两端连接实体都会获得 `NetworkStreamIsReconnected`；连接还会获得内部唯一 ID 来追踪该行为
* 可以通过 `NETCODE_SNAPSHOT_HISTORY_SIZE_6` 或 `NETCODE_SNAPSHOT_HISTORY_SIZE_16` 定义更小的 `GhostSystemConstants.SnapshotHistorySize`，适合服务器内存受限、单个 Ghost 快照发送频率较低的大规模场景
* 通过新字段 `PrioChunks.isRelevant` 支持组合 Ghost Relevancy 与 Ghost Importance Scaling，并[启用相关性计算快速路径](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/manual/optimizations.html#relevancy-fast-path-via-importance-scaling)
* 为 Netcode 工具增加分析数据，以了解其使用情况

### 变更

* **行为破坏性变更：** 部分 Tick 上的预测生成 Ghost，如果最近备份 Tick 与生成 Tick 相同，说明数据未变化，现会跳过从备份恢复状态，直接从生成状态继续预测
* **行为破坏性变更：** 降低 `GhostCount.GhostCountOnServer` 内部计算复杂度与性能开销；该值始终只是近似值
* 将 `IsReconnected` 拆分为连接重连所用的 `NetworkStreamIsReconnected`，以及 Host Migration 重新生成 Ghost 所用的 `IsMigrated`
* 将 Host Migration 类型移入 `Unity.Netcode.HostMigration` 命名空间，并将 `HostMigration` 类重命名为 `HostMigrationUtility`
* Host Migration 之间现会保留预生成 Ghost ID
* Host Migration 之间现会保留客户端连接 `NetworkID`

### 修复

* **行为破坏性变更：** 修复客户端在预测循环中生成 Ghost 时，`SnapshotDataBuffer` 保存错误状态的问题。`PredictedGhostSpawnSystem` 现通过 `PredictedSpawningSystemGroup` 同时作为预测循环的一部分更新，确保客户端预测生成 Ghost 在实际生成 Tick 使用完整 Tick 状态正确初始化，而不是使用部分 Tick 状态
* 修复配置为回滚到 `spawnTick` 且在预测循环内生成的预测 Ghost，从错误 Tick 重新模拟的问题；现使用正确的完整 Tick 状态恢复，而不是错误的部分 Tick 状态
* WebGL 现可创建并初始化服务器 Driver，以支持通过 Relay 自托管；此前许多方法受条件编译限制，在 WebGL 构建中被移除且只能在 Editor 使用
* `FixedStepSimulationSystemGroup` 中对 `PhysicsSystemGroup` 存在直接或间接更新依赖的所有非托管系统，现会正确移入 `PredictedFixedStepSimulationSystemGroup`。这是相对旧版的**行为变更**；此前无论更新顺序依赖如何，非托管系统都留在固定更新组
* 修复 Multiplayer PlayMode Tool 窗口在 Domain Reload 后停靠时抛出异常的问题，原因是恢复窗口状态时访问了 `EditorPrefs` 和 `Application.productName`
* 修复 Host Migration 期间 Ghost 可能以 0 ID 和类型迁移，导致在新 Host 实例化时出错的问题
* 修复 Host Migration 后服务器部署迁移数据时可能崩溃的问题
* 修复 Host Migration 后预生成 Prefab 实体列表初始化次序偏移一位的问题，内部 `PrespawnSceneList` 实体 Prefab 会导致类似 *invalid ghost type X (expected X+1)* 的错误
* 唯一预测 Ghost 从预测切换为插值、之后再切回预测时，预测循环不再执行过多回滚
* 更新多个包时，要求为其他包创建带新标题的独立章节
* 修复客户端 Packet Dump 未写入 `EnablePacketLogging.NetDebugPacketCache`，导致用户和 `NetworkStreamReceiveSystem` 等调用点无法使用的问题
* 修复 `SnapshotHistorySize` 小于 32 时，写入 Ghost Chunk 会覆盖仍在传输中的快照历史条目，导致永久无法获取有效 Baseline 的问题；传输队列已满时会暂停发送该 Ghost Chunk，直到认为在途快照已到达或丢失
* 修复没有 Ghost Field 时因指针步进错误导致的 Buffer 序列化问题，涉及 `GhostComponentSerializer.State.HasGhostFields`
* 修复 `SendToOwnerType` 或 `GhostSendType` 有条件阻止发送 Buffer 时，未使 Change Mask 失效导致的 Buffer 序列化错误
* 把误删的 `UsePreSerialization` 重新加入 `GhostAuthoringComponent` Inspector

### 废弃

* 推荐使用 `BatchScaleImportanceDelegate` 代替 `ScaleImportanceFunction`，前者能显著减少函数指针总调用次数



## [1.5.0] - 2025-04-22

### 新增

* 新增 `AutomaticThinClientWorldsUtility`，便于在运行时创建和管理瘦客户端，可由用户代码及 `PlayType.Server` 使用
* 新增 `ClientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks`，用于延长客户端预测生成 Ghost 的分类等待期，缓解“正确预测的对象在匹配服务器对应对象前就被 Netcode Despawn”的问题。默认情况下，到 `NetworkTime.InterpolationTick` 仍未分类的对象会被 Despawn，该值可额外延长若干 Tick。如果 Ghost 经常无法在 `InterpolationTick` 前复制，应先考虑增大 `ClientTickRate.InterpolationTimeMS` 或 `InterpolationTimeNetTicks`，详情参阅[插值文档](Documentation~/interpolation.md)
* 将默认分类系统的 `k_TickPeriod` 范围常量公开为 `ClientTickRate.DefaultClassificationAllowableTickPeriod`，默认 `±5 ticks`；它可缓解服务器频繁批处理 Tick 时的大 Tick 差导致的少见分类错误，但仍推荐实现能利用项目专属 `GhostField` 数据的自定义分类系统
* 新增 `ClientServerBootstrap.AllNetCodeWorldsEnumerator` 和 `ClientServerBootstrap.AllClientWorldsEnumerator`
* 新增自定义模板序列化与字节对齐注意事项文档
* 为极少见的 `MaxBaselineAge` 边界情况增加测试、Packet Dump 条目和警告日志
* RPC、Command 及组件（`IComponentData`、`IBufferElementData`、`IInputComponentData`）支持复制 FixedList，限制参阅文档
* RPC、Command 及上述组件支持复制 Unsafe Fixed Buffer，限制参阅文档
* 新增 `GhostAuthoringComponent.UseSingleBaseline`，强制该 Prefab 类型在序列化时使用 Single Baseline Delta 压缩；对于组件多或很少变化的 `GhostField` 多的 Ghost，可用少量额外带宽换取显著 CPU 节省
* 新增 Byte 与 Short 模板，在未压缩发送时分别使用 8 bit 和 16 bit，不再一律使用 32 bit
* Static 优化 Ghost 现会把 `CanUseStaticOptimization`（CUSO）状态及首个检测到版本变化的 `ComponentType` 写入 Packet Dump，便于调试
* 广泛改善 Packet Dump 数据，但启用后服务器性能开销增加
* 补充哪些字段类型支持预测误差报告的文档
* 补充 `GhostGroup` 文档与测试覆盖
* 补充 `[GhostField]` C# Union（通过 `[StructLayout(LayoutKind.Explicit)]`）的测试和文档，功能受多项限制
* 增加 `NetworkStreamSnapshotTargetSize`、UTP `MaxMessageSize` 与 `GhostSystemConstants.MaxSnapshotSendAttempts` 测试覆盖

### 变更

* **行为破坏性变更：** 客户端完成 `Handshake` 前忽略自身 `HandshakeApprovalTimeoutMS`，因为应采用服务器值；客户端 World 不会从 `NetCodeConfig.Global` 获取 `ClientServerTickRate`，只接受握手期间服务器发送的值
* **行为破坏性变更：** `AddCommandData` 现会拒绝 Tick 为 `Invalid` 的输入，避免少见的运行时异常
* **行为破坏性变更：** `RequestedPlayType == Server` 时，`DefaultDriverConstructor` 不再移除 IPC Driver，因为 DGS 构建现可实例化瘦客户端，前提是用户代码支持
* **数值破坏性变更：** 延迟补偿 Physics `CollisionWorld` 历史容量由 16 Tick 提高到 32 Tick；`LagCompensationConfig.ServerHistorySize: 0` 的默认值仍为 16。提高容量会分配更多 Collision World，增加 `ServerWorld` 内存，尤其对大型物理 Scene；只有需要支持高 Ping 玩家，例如频繁看到 `GetCollisionWorldFromTick` 把历史限制到最旧值时才应提高。公开常量 `PhysicsWorldHistory.RawHistoryBufferMaxCapacity` 也由 16 改为 32
* 用统一的 `com.unity.services.multiplayer` 1.1.0 替换已废弃的 `com.unity.services.relay`
* 逐步改进 Network Debugger 浏览器工具
* 修复在 Play Mode 外使用 `GhostPresentationGameObjectSystem`，且不通过 `Object.DisposeImmediate` 释放 GameObject 时的问题
* 为 Netcode 包全部 Dynamic Buffer 添加 `[InternalBufferCapacity(0)]`，避免 Buffer 保存在 Chunk 内部
* 改善 Ghost 实体调试：SubScene 关闭后 Ghost Prefab 仍保留名称，由它生成的实体也始终保留名称；预生成实体暂不保留
* `CommandSendSystem` 现发送当前 Tick 而非上一 Tick 的 Command，使输入提前一个 Tick 发送和到达服务器；所需 Slack 更少，客户端也少运行一次预测循环
* 包模板不再嵌入 Generator DLL，而是全部作为 Generator Additional File 提供
* 移除 `Editor/Template`，模板移到 `Runtime/SourceGenerator/Templates`
* 修改 Netcode 包模板时不再需要重新编译 Source Generator DLL
* **行为破坏性变更：** 单个 Ghost 大到无法放入快照包时，发送重试次数现限制为 `GhostSystemConstants.MaxSnapshotSendAttempts`，即 8 次；这相当于把包扩到 128 倍，通常会严重分片。`NetDebug.LogLevel` 为 `Debug` 时，每次失败重试都会产生性能警告；不再重复 `GhostSendSystem.SerializeJob.GatherGhostChunksBatch`，显著降低操作开销
* 为 Editor 工具增加 Analytics
* 最低支持 Editor 版本改为 2022.3.20f1

### 移除

* 移除内部 `GhostPredictionSwitchingSystemForThinClient` 以降低复杂度

### 修复

* 修复在生成 Prefab 期间断开连接时出现“Ghost Map 中存在没有关联 Entity 的 Ghost”错误
* 修复与断开连接同一帧广播 RPC 时过度严格的验证错误
* `AutomaticThinClientWorldsUtility` 现允许在 Bootstrap 阶段将 `BootstrapInitialization` 和 `RuntimeInitialization` 设为 `null`，禁用 Editor 中自动创建瘦客户端
* 移除 `Server` 模式下不能创建瘦客户端的限制，包括 DGS 构建；瘦客户端系统必须位于服务器会加载的程序集
* 修复 `RequestedNumThinClients` 导致用户创建的瘦客户端 World 被包自动清理的问题；现在只有通过 `AutomaticThinClientWorldsUtility` 创建或由用户手动加入其追踪列表的 World 才会自动释放
* 修复 `RollbackPredictionOnStructuralChanges` 文档不一致和若干拼写错误
* 修复客户端断开再重连服务器后，预生成 Ghost 不再更新的问题
* 修复只为部分 Ghost 添加 `GhostDistancePartitionShared` 或用户等效 `GhostImportancePerChunkDataType` 时，Ghost Importance Scaling 抛出异常的问题
* 当 `GhostDistancePartitionShared` 能通过 Change Filtering 排除未变 `LocalTransform` Chunk 时，`GhostDistancePartitioningSystem` 性能显著提高
* 修复 MultiPhysics 示例中粒子发射器不在仅客户端物理 World 生成粒子的问题
* 修复销毁实体时 `GhostPresentationGameObjectSystem` 抛出 `ObjectDisposedException` 的问题
* 修复结构变化后 `GhostPresentationGameObjectSystem` 访问 `ComponentLookup` 抛出异常的问题
* **行为破坏性变更：** 修复 Static 优化插值 Ghost 未正确禁用 `GhostField` 外推的问题；此类 Ghost 不支持外推，即使标记为 `SmoothingAction.InterpolateAndExtrapolate`
* 修复 Editor 报告编译错误时，Source Generated 文件无法在 IDE 正确打开的问题
* 修复 Debugger 未加载符号，导致无法调试代码生成 Ghost Serializer 的问题
* 修复把某个生成 Serializer，例如 RPC 或 Component，加入项目时因符号重复导致编译错误的问题
* 修复 `BeginFixedStepCommandBufferSystem` 和 `EndFixedStepCommandBufferSystem` 在 `PredictedSimulationSystemGroup` 内与 Physics Group 更新次数不同的问题；它们现每个物理步骤更新一次。该正确修复会改变行为：每个预测 Tick 使用多个物理步骤且依赖旧行为的项目，排队的 Command Buffer 变更现可能在每个物理步骤开始或结束时全部执行
* 修复序列化 `NetworkEndpoint` 的废弃警告，并为 RPC、Command 和 GhostField 添加 `NetworkEndpoint` 序列化
* **行为破坏性变更：** 所有少见的可恢复与不可恢复客户端快照反序列化错误现都会记录警告及对应 Packet Dump；修复 Ghost Chunk Ack 未在所有情况下清除，导致客户端无法从所谓可恢复错误中真正恢复的问题。遇到可恢复错误时，Static 优化 Ghost 现会失去 `CanUseStaticOptimization` Ack 优化，连接需要像重新连接一样重发全部 Ghost Chunk。新增统计项 `SnapshotPacketLoss.NumClientAckErrorsEncountered`
* Static Ghost 现可在 `SnapshotAckMaskCapacity` Tick 窗口之外记住客户端 Ack，前提是之前 `GhostSendSystem` 遍历 Ghost Chunk 时 Ack Mask 已确认该快照，且客户端没有因快照反序列化错误清空整个 Ack Mask Buffer
* Static 优化 Ghost 以前会先发送一份“零变更”快照，再决定后续可跳过该 Chunk，导致大规模或首次连接同步时发送过多快照；现在会从包中正确剔除零变更数据，显著节省带宽与 CPU
* 显著提高 Editor 和 Development Build 中 `SpawnGhostJob` 的性能，尤其是单 Tick 生成数千个 Ghost 时
* 修复自定义 Chunk Serializer 配合预序列化 Ghost 时，预序列化数据未复制到 Chunk 内部快照 Buffer，可能在存在 Buffer 时崩溃并序列化未初始化值的问题
* 修复 `GhostStatsSystem` 访问 Prediction Error Buffer 时可能越界的问题；预测误差多到需要限制预测字段名数量时，Buffer 未正确扩容
* 修复为处理不同平滑选项的类型使用自定义模板时，错误验证失败并导致编译错误的问题
* 修复 `GhostUpdateSystem.RestorePredictionBackup` 导致未变化子组件版本变化的问题；当 `HasGhostFields == 0 && SerializesEnabledBit != 0` 时，`PredictionBackupJob` 未更新 `childChangeVersions` 指针
* 修复快照大小限制未阻止全部错误，以及 Ghost Group 为空且 Stream 空间不足以编码 Group 长度 0 所需 2 bit 时，Group Root 序列化不正确的问题
* 修复 Group 序列化失败时未重置实体发送状态，导致已序列化子实体被错误报告为已发送，服务器可能用错误 Baseline 做 Delta 压缩的问题
* 修复禁用后台运行时的错误警告文本
* 修复因错误假设 `stackalloc` 会默认初始化元素而产生的少见 `IBufferElementData` 序列化问题

## [1.4.0] - 2024-11-14

### 新增

* 新增可切换的服务器 Tick 批处理警告
* 为 `NetcodePhysicsConfigAuthoring` 新增 `PhysicGroupRunMode`，用于配置预测物理循环何时运行
* 为 `ClientTickRate` 新增 `PredictionLoopUpdateMode`，用于配置 `PredictionSimulationSystemGroup` 何时更新；现在可让预测循环始终运行，不依赖是否存在预测 Ghost
* 新增 `GhostSendSystemData.MaxIterateChunks`，表示单个 `NetworkTickRate` 快照发送间隔内，`GhostSendSystem` 对指定连接在单 Tick 最多遍历多少 Chunk。它适用于包含数千个 Static Ghost、因而需要遍历数百个 Static Chunk 查找变化的场景；值过低可能产生空快照。该值可与 `MaxSendChunks` 配合，默认为 0，即关闭，以避免行为变化
* `NetCodeConfig` 新增多项 Unity Transport `NetworkConfigParameters`。使用自定义 Driver 时默认忽略，除非 Driver 调用新静态方法 `DefaultDriverBuilder.AddNetcodePackageNetworkConfigParameters`
* 新增 `ClientServerTickRate.SnapshotAckMaskCapacity`，以 Server Tick 为单位配置 Ack Mask 历史长度。快照系统只在尝试重发 Chunk 时查询它，用于判断 Ghost 是否拥有已确认 Baseline 快照。默认值从 256 提高到 4096；在 60 Hz `SimulationTickRate` 下，支持时间由约 4.26 秒提高到约 1.1 分钟。向单条连接发送数万个 Ghost 时，进一步增大可防止快照 Ack 错误
* 新增 `GhostAuthoringComponent.MaxSendRate`，表示该 Ghost Prefab 类型 Chunk 允许的最高发送频率，单位 Hz。最终频率仍受 `NetworkTickRate`、Ghost 数量、Static Optimization 或 Dynamic、`Importance`、Importance Scaling 与 `DefaultSnapshotPacketSize` 等影响；可用它强制降低高影响 Ghost 类型的带宽消耗
* `GhostCount` 新增 `GhostCountInstantiatedOnClient` 和 `GhostCountReceivedOnClient`，区分仅收到数据的 Ghost 与已经完整实例化且存在实体的 Ghost，参阅废弃项和 `PendingSpawnPlaceholder`
* 新增 `AutomaticThinClientWorldsUtility`，便于运行时创建和管理瘦客户端，可供用户代码和 `PlayType.Server` 使用

### 变更

* `NetworkProtocolVersion` 不匹配错误现会更准确说明失败原因及解决步骤
* 逐步改善 `MultiplayerPlayModeWindow` 的 Netcode World UI：服务器显示 Ghost 数量及 Tooltip 详情；客户端 Ping Tooltip 悬停可查看 `GhostCount` 单例；`DriverStore` Driver 统一显示
* 重新启用因 `CommandSendSystemGroup` 问题而随机失败、此前被禁用的 `LoadScenes_AllScenesShouldConnect` 和 `LoadScenes_NoScenesShouldLog` 测试
* **行为破坏性变更：** 除非 `GhostSendSystemData.MaxIterateChunks` 为 0，`GhostSendSystemData.MaxSendChunks` 不再限制最多遍历或查询的 Chunk 数，因为取消的 Chunk 快照写入不再计入总量。应改用 `MaxIterateChunks` 表示遍历上限；与大量 Static 或无关 Ghost 配合时，可减少空包
* **API 与行为破坏性变更：** Netcode `DefaultDriverConstructor` 现在默认采用 Transport 的 `NetworkParameterConstants.SendQueueCapacity` 和 `ReceiveQueueCapacity`，两者均为 512，不再使用包内 `max(playerCount * 4, 64)`。可选 `playerCount` 参数已从 `CreateServerNetworkDriver` 和 `GetNetworkServerSettings` 移除，可通过新增 `NetCodeConfig` 设置覆盖。该变更可防止高玩家数测试时常见的致命错误，并减少实现项目专属 `INetworkStreamDriverConstructor` 的需求，但内置 Driver Constructor 在客户端和服务器各增加约 1.8 MB 内存；旧值 64 未造成问题的项目建议改回 64
* “Delta time was negative...” 详细日志移到 `NetDebug.DebugLog` 后，并调整措辞
* 合并内部批处理与非批处理 `GatherGhostChunks` 方法，两者性能特征应基本一致
* 占位 Ghost 现命名为 `GHOST-PLACEHOLDER-{ghostType}`，便于调试
* 编辑并改善“配置客户端与服务器 World”文档章节
* **行为破坏性变更：** 客户端完成 `Handshake` 前忽略 `HandshakeApprovalTimeoutMS`，采用服务器值；客户端 World 不从 `NetCodeConfig.Global` 获取 `ClientServerTickRate`，只接受握手期间服务器发送的值
* **行为破坏性变更：** `AddCommandData` 现拒绝 Tick 为 `Invalid` 的输入，避免少见运行时异常
* **行为破坏性变更：** `RequestedPlayType == Server` 时，`DefaultDriverConstructor` 不再移除 IPC Driver，因为 DGS 构建现可实例化瘦客户端

### 废弃

* 废弃 `NetworkDriverInstance.simulatorEnabled` Setter，因为写入它不能真正启用或禁用模拟器
* **行为破坏性变更：** `GhostSendSystemData.MaxSendEntities` 不再生效；其含义容易误导，也不如 `MaxSendChunks` 和 `MaxIterateChunks` 精确
* `GetNetworkSettings` 重命名为 `GetNetworkClientSettings`
* 废弃含义模糊的 `GhostCount.GhostCountOnClient`；其值等同新字段 `GhostCountReceivedOnClient`，但旧 Tooltip 错误暗示它表示 `GhostCountInstantiatedOnClient`

### 修复

* 修复 `MultiplayerPlayModeWindow` 中服务器 World 按钮宽度错误产生水平滚动条的问题，并减少过度重绘
* 修复窗口脱离停靠后无法调整大小的限制
* 修复当前服务器 Tick 无效时 `CommandSendSystemGroup` 仍运行系统，导致 `CommandSendPacketSystem` 等系统抛出异常的问题
* 修复使用物理插值时，物理系统在部分 Tick 运行导致复制 Ghost 画面抖动的问题
* 现在可组合 `PredictionLoopUpdateMode` 与 `PhysicGroupRunMode`，即使没有预测 Ghost 也让物理系统在预测循环中运行
* 修复 Netcode Source Generated 文件触发多次 `Burst.CompileAsync`，长时间阻塞 Editor 和 Player 或导致崩溃的问题
* 修复关键的 `GhostSendSystem` 和 `GhostChunkSerializer` 问题：Ghost Chunk 下一次重发超过 256 Tick 时，Ghost 无法确认自身之前的快照；复制数千 Ghost 到单条连接时容易出现。无法 Ack 时必须重发更大 Delta，且不能使用 Static Optimization 提前退出，浪费带宽与 CPU。初步修复是显著增大 Ack 窗口，参阅 `SnapshotAckMaskCapacity`
* Auto Refresh 模式下，用户编辑 Ghost Prefab 属性时，`GhostAuthoringInspectionComponent` 不再错误触发重新烘焙
* `MinSendImportance` 不再人为延迟低重要度 Ghost 的首次发送，尽管以前可用 `FirstSendImportanceMultiplier` 缓解
* 修复服务器帧 `deltaTime` 超过 `MaxSimulationStepsPerFrame * MaxSimulationStepBatchSize` 时，Netcode `ElapsedTime` 落后于 `InitializationSystemGroup` 的问题，并改变服务器追赶行为；此前批处理仍不足时会跳过 Tick，现在后续帧有时间时会尽量追赶遗漏 Tick
* 修复服务器 World 中 Netcode `ElapsedTime` 可能领先 `InitializationSystemGroup` 的问题；现在应始终相等，或在累计时间不足以执行 Tick 时略微落后
* 修复生成 Prefab 期间断开连接时出现“Ghost Map 中存在没有关联 Entity 的 Ghost”错误
* 修复与断开连接同一帧广播 RPC 时过度严格的验证错误
* `AutomaticThinClientWorldsUtility` 现允许在 Bootstrap 阶段把 `BootstrapInitialization` 和 `RuntimeInitialization` 设为 `null`，禁用 Editor 自动创建瘦客户端
* 移除 `Server` 模式及 DGS 构建不能创建瘦客户端的限制；瘦客户端系统必须位于服务器会加载的程序集
* 修复 `RequestedNumThinClients` 导致用户创建的瘦客户端 World 被自动清理的问题；现在只有通过 `AutomaticThinClientWorldsUtility` 创建或手动加入追踪列表的 World 才会自动释放



## [1.3.6] - 2024-10-16

### 变更

* 改善 `NetworkStreamDriver.ConnectionEventsForTick` 的 XML 文档
* 更新 Entities 包依赖

### 修复

* 修复 Netcode Source Generated 文件触发多次 `Burst.CompileAsync`，长时间阻塞 Editor 和 Player 或导致崩溃的问题
* 修复禁用 Fast Enter Play Mode Options，也就是进入 Play Mode 时触发 Domain Reload 的情况下，Editor 忽略 Scene 中 `OverrideAutomaticNetcodeBootstrap` 实例的问题
* 修复 Netcode for Entities API 文档中长期存在的多项错误


## [1.3.2] - 2024-09-06

### 变更

* 更新 Entities 包依赖

### 新增

* 显著降低 Command 或输入包的带宽消耗：每个包的首条 Command Payload 现相对零值做 Delta 压缩；每包发送 Command 数与 `TargetCommandSlack` 关联；`NetworkTick` 相对通常成立的前一 Tick 假设做 Delta 压缩；前后 Command 完全相同时只使用一个 `changeBit`
* 新增 `ClientTickRate.NumAdditionalCommandsToSend`，配置每个 Command 或输入包额外向服务器发送多少条 Command
* 支持把输入 Command 写入 `NetDebugPacket` Dump，帮助观察和诊断带宽；输入组件可实现可选且兼容 Burst 的 `ToFixedString`，在 Dump 中显示字段数据
* 新增 `NetworkSnapshotAck.CommandArrivalStatistics`，在服务器上按客户端记录收到多少 Command、多少到达过晚；可用于调整 `TargetCommandSlack` 和 `NumAdditionalCommandsToSend`
* 显著扩展延迟补偿自动测试，现可检测客户端与服务器延迟补偿结果之间相差一个 Tick 的错误
* 新增配置 `LagCompensationConfig.DeepCopyDynamicColliders`，默认 `true`，以及 `DeepCopyStaticColliders`，默认 `false`；用于控制克隆 World 时是否深拷贝 Collider，避免查询延迟补偿历史 World 时出现运行时异常；另请参阅 `PhysicsWorldHistorySingleton.DeepCopyRigidBodyCollidersWhitelist`

### 变更

* `PhysicsWorldHistory` 现在会在指定 `ServerTick` 的 `BuildPhysicsWorld` 步骤之后克隆 Collision World，修复服务器 `GetCollisionWorldFromTick` 返回的 `CollisionWorld` 偏差一个 Tick 的问题。`ServerTick` T 保存的数据现真正对应 Tick T 的构建操作，而不是旧行为中的 T-1。该变更可能造成回归，属于轻微破坏性变更，强烈建议对延迟补偿精度配置自动测试
* `PhysicsWorldHistory` 默认深拷贝动态 Collider，性能影响应可忽略

### 修复

* 修正包 XML 文档中 `seealso` 的使用
* 改善并澄清 Command Stream、Ghost Snapshot、Ghost 生成、日志、网络连接、联网立方体、预测与 RPC 文档
* 修复通过 `GetCollisionWorldFromTick` 查询延迟补偿历史 `CollisionWorld` 命中实体，但该实体随后已删除时的问题。动态 Ghost Collider 现默认深拷贝，避免 Blob Asset 断言。也可通过 `LagCompensationConfig` 或 `NetCodePhysicsConfig` Authoring 选择复制静态 Collider，但推荐执行两次查询：先用最新 Collision World 只查静态几何体，再以静态命中位置限制针对延迟补偿动态实体的查询
* 修复 History Buffer 大小不是 2 的幂时，`ServerTick` 回绕会返回错误条目的问题
* 修复使用 Burst 1.8 与 Unity 6.0+ 时，iOS 和 WebGL AOT Player 初始化 Netcode 生成的 Ghost Serializer 函数指针会抛出异常的问题
* 修复 Ghost Group 序列化错误访问 `GhostCollectionPrefab` 数组中其他 Ghost Prefab 类型的问题
* 修复 Ghost Group 的 Buffer 序列化因快照存储 Buffer 所需尺寸计算错误而覆盖内存的问题，Editor 会抛出异常；根因是访问 `GhostCollectionPrefab` 时使用了错误索引


## [1.3.0-pre.4] - 2024-07-17

### 新增

* 为 `GhostCreation.Config` 新增可选 `UUID5GhostType`，运行时创建 Ghost Prefab 时可提供自己的唯一 UUID5，不再依赖根据 Ghost 名称 SHA1 自动生成的值
* 新增 `NetworkStreamDriver.ResetDriverStore`，用于正确重置 `NetworkDriverStore`

### 变更

* 全部 `Simulate` 组件启用状态现通过 Job 重置，不再在主线程同步执行，以避免预测循环结束时的大幅停顿；只有 Job 化工作负载较大时收益明显
* 修正多个历史版本中错误或缺失的 Changelog 条目
* Burst 依赖更新到 1.8.16
* 统一 Multiplayer Project Settings
* 将菜单项整合到公共位置以改善工作流：移除 Multiplayer 菜单，合并到 Window、Assets/Create 与右键菜单等位置
* Unity Transport 依赖更新到 2.2.1
* 重新公开 `TryFindAutoConnectEndPoint` 和 `HasDefaultAddressAndPortSet`，并小幅更新文档
* `ConcurrentDriverStore` 和 `NetworkDriverStore.Concurrent` 现为 `public`，可在 Job 中使用 `NetworkDriverStore.Concurrent` 收发数据


## [1.3.0-exp.1] - 2024-06-11

### 新增

* Multiplayer PlayMode Tools 窗口现调用同步 `Connect` 和 `Disconnect`，并显示 `Handshake` 连接阶段；服务器已经接受客户端 Transport 连接，但客户端仍在等待服务器分配 `NetworkId` 的 RPC 时处于 Handshake
* 进一步澄清并小幅改善 PlayMode Tools 窗口
* 新增 `DefaultRelevancyQuery`，用于定义通用相关性规则，无需逐个 Ghost 配置
* 为 `NetCodeConfig` 增加 Tooltip 与信息，支持 `ClientServerTickRate`、`ClientTickRate` 和 `GhostSendSystemData`
* 新增 `EnablePacketLogging.LogToPacket`，允许用户代码向按连接生成的 Netcode Packet Dump 添加自定义事件
* 新增可选连接审批流程，在分配 Network ID 前验证连接是否允许加入；客户端通过 `IApprovalRpcCommand` RPC 向服务器发送审批请求，服务器可验证任意 Payload；连接获批前不处理其他数据
* 补充预测、边界情况、插值、压缩、物理 Ghost 配置检查表与整体更新循环文档
* 加强 RPC 序列化验证并改善错误日志，现会确认反序列化后的大小是否等于预期字节数
* 增加 `ReliableSequencedPipelineStage` 的 `windowSize: 64` 测试覆盖
* 为 `GhostAuthoringComponent` 新增 `PredictedSpawnedGhostRollbackToSpawnTick`，允许客户端预测生成 Ghost 在收到服务器权威生成前，从自身生成 Tick 回滚并重新模拟；只有客户端收到至少包含一个预测 Ghost 的新快照时才回滚
* 从 `NetworkParameterConstants.MTU` 改为用户可配置的 `NetworkParameterConstants.MaxMessageSize`，使快照和 Command Buffer 使用正确值并据此扩容
* 公开 `NetworkStreamDriver.DriverStore` 和 `LastEndPoint`
* 新增 `NetworkStreamDriver` 实例的零拷贝访问器 `GetDriverInstanceRW`、`GetDriverInstanceRO`，以及底层 Driver 访问器 `GetDriverRW`、`GetDriverRO`；内部也改用这些方法，返回结构体副本的旧接口弱废弃
* 支持序列化非字节对齐 RPC；现在可通过 `IRpcCommandSerializer` 让 RPC 字段相对硬编码 Baseline 做 Delta 压缩
* 可通过 `NetcodeServerRateManager.WillUpdate` 检测服务器 World 是否会执行 Tick，在 BusyWait 模式的空闲帧执行昂贵操作，参阅优化文档

### 变更

* RPC 实体名称现会在 `NetcodeRPC_` 前缀后包含 RPC 组件完整类型名
* Netcode RPC Header 由每包 9 B 改为 5 B，另根据 `DynamicAssemblyList` 为每条 RPC 增加 10 B 或 4 B
* 序列化 RPC 最大大小改为 8192 字节，即 `ushort.MaxValue` bit；系统现会发送实际写入位数，以便 `RpcSystem` 验证读取位数完全一致
* 降低 `RpcSetNetworkId` RPC Payload 的带宽消耗
* `com.unity.transport` 依赖更新到 2.2.0
* 修复预测循环中再次生成的预测 Ghost 经 `PredictedSpawnGhostSystem` 延迟初始化后，没有回滚到备份或从生成 Tick 重新预测的问题；该错误会让第一个完整 Tick、后续部分 Tick 和备份都包含错误预测
* `RpcSetNetworkId` 重命名为 `ServerApprovedConnection`
* 服务器 `Handshake` 不再瞬间完成，连接通常需要约 7 Tick，原为约 4 Tick
* 服务器现会在协议版本握手期间触发 `NetCodeConnectionEvent`，状态为 `ConnectionState.State.Handshake`
* 降低 Netcode `NetworkProtocolVersion` RPC 的带宽消耗

### 移除

* 移除 `NoScale` 函数委托

### 修复

* 修复同时安装 `com.unity.netcode` 与 `com.unity.dedicated-server` 时的编译错误
* 修复直接调用 `Disconnect` 断开自身客户端时，无法回收 `NetworkId` 组件和释放实体的问题
* 正确报告并清理被用户代码置入无效状态的陈旧连接
* 修复 `CommandSendSystem` 尝试通过陈旧连接发送 RPC 的问题
* 调用 Driver `Connect` 后，`NetworkStreamConnection` 会立即保存准确连接状态，不再等待一帧
* 修复若干文档问题
* `EntityManager` 处于 Exclusive Transaction 时跳过收集对应 World 的 Analytics，避免 `InvalidOperationException`
* 应用移除 `NoScale` 函数的破坏性变更
* 修复 Play Mode 中开启并修改 `NetCodeDebugConfig` 后再关闭时，`LogLevel` 未恢复默认 `Notify` 的问题
* 修复若干文档错误并改善整体语法
* 修复首次启动时未在 Project Assets 窗口选中 `NetCodeConfig.Global` 就无法正确加载的问题；如果 PreloadedAssets 中设置了全局 `NetCodeConfig`，项目会自动升级并通过 `NetCodeClientAndServerSettings` 把配置移到 Project Settings
* 网络 Delta Time 为负时跳过系统组更新
* 修复 `NETCODE_NDEBUG` 宏编译错误并补充文档
* 修复不同命名空间中两个同名 `IInputComponentData` 生成代码冲突的问题，Source Generator 现考虑命名空间
* 澄清 Network Emulation Tooltip
* 修复与 `ProtocolVersion` RPC 同一 Tick 发送的 RPC 因偏差一个 Tick而损坏的问题
* 改善 `PredictedSimulationSystemGroup` 和 `ClientServerBootstrap` 文案
* 修复 Prefab 数量很大时 `GhostCollectionSystem` 的性能问题
* 修复 `PredictedGhostSpawnSystem` 错误设置 Snapshot Buffer 中序列化 Buffer Data Offset，导致与 `GhostUpdateSystem` 不兼容并可能把错误数据复制回实体 Buffer 的问题
* 修复预序列化 Ghost 用错误值覆盖组件数据的问题
* 修复 `GhostUpdateSystem` 错误处理 `GhostComponentAttribute.SendToOwner`，导致 Continuation 与部分 Tick 中预测 Ghost 的复制数据被错误覆盖的问题
* 修复结构变化后实体数据不再存在于 Prediction History Buffer，Ghost 无法从最近完整 Tick 继续预测，导致客户端执行大量预测步骤的问题
* 修复 RPC 出现在已删除连接实体上，用户代码偶尔无法从已断开连接的 `ReceiveRpcCommandRequest.SourceConnection` 找到 `NetworkId` 而抛出异常的问题
* 修复客户端只向服务器确认最近一份快照，而不是用于抵抗丢包的最近 32 份快照的问题；该错误影响可用 Baseline 的正确使用，并导致 Static 优化 Ghost 多次重发
* 修复 `GhostAuthoringInspectionComponent` UI 被右侧裁切的问题
* 防御性修复刷新和自动刷新按钮显示不正确的问题
* 修复 Ghost Chunk 被复用时提前释放分配的检查，现会正确释放 Chunk，降低服务器内存开销
* 修复预测模式切换插值时的旋转异常
* 修复首次连接时 RTT 计算过高的问题，高丢包下尤其明显
* 修复禁用 Domain Reload 时运行 Netcode 测试会使后续 Play Mode 的 `NetworkTimeSystem.TimestampMS` 无效，导致 Ping 显示 `0±0` 等问题
* 修复处理 `PredictedSpawnGhostRequest` 并初始化实体时，预测生成 Ghost 的 Enableable Component 状态未正确保存到 Snapshot Buffer 的问题
* 修复预生成 Ghost 的 Enableable Component 状态未正确保存到预测生成 Baseline Buffer 的问题
* 修复销毁实体时 `GhostPresentationGameObjectSystem` 访问追踪 `GameObject` 列表的无效索引，尤其删除最后一个元素时抛出异常的问题
* 修复 `SetupDataAndAvailableBaselines` 中极少见的无限循环崩溃
* 修复 `PredictedGhostHistorySystem` 使用错误 Ghost 类型和 Serializer 保存新生成 Ghost 备份的问题；当预测生成 Ghost 可从生成 Tick 重新模拟时，会在 `GhostUpdateSystem` 中导致崩溃、大量内存分配或用无效数据覆盖其他组件
* 修复预测循环系统在预期 Tick 立即生成对象且没有 Command Buffer 时，预测生成 Ghost 未正确从备份恢复的问题；下一帧初始化的生成状态是部分 Tick 而非完整 Tick，会增加错误预测，此时应优先采用与最近完整 Tick 对齐的备份
* 修复 MultiPhysics 示例因缺少 WorldIndex Authoring，纯视觉粒子在客户端与玩家角色发生碰撞的问题
* `IRpcCommandSerializer<T>` 现可用于实现 `IRpcCommand` 和 `IApprovalRpcCommand` 的结构体，不再只限 `IComponentData`；代码生成器会跳过生成 RPC 系统与 Serializer
* 服务器现会正确等待收到客户端有效协议版本，再把客户端从 `Handshake` 移到 `Connected`，因此服务器能正确触发 Handshake 事件
* 服务器未收到客户端 `RequestProtocolVersionHandshake` RPC 时，协议版本握手现可按 `HandshakeApprovalTimeoutMS` 正确超时；启用审批流程时，两个状态共用该超时计数器
* 移除硬编码 Protocol Version RPC 逻辑，简化 RPC 收发；Netcode 握手 RPC 现使用既有 `IApprovalRpcCommand` 流程


## [1.2.4] - 2024-08-14

### 变更
* 更新 Entities 包依赖


## [1.2.3] - 2024-05-30

### 变更
* 更新 Entities 包依赖


## [1.2.1] - 2024-04-26

### 变更

* 将 Burst 依赖更新至 1.8.13
* 更新 Entities 包依赖


## [1.2.0] - 2024-03-22

### 变更
* 发布准备


## [1.2.0-pre.12] - 2024-02-13

### 新增

* 通过批处理函数指针调用和改用更合适的哈希表，优化 Ghost Chunk 收集流程
* 新增批量执行重要度缩放的 `BatchScaleImportanceDelegate`；无需同时设置 `ScaleImportance` 和 `BatchScaleImportance` 函数指针，设置后优先使用后者
* 为 `GhostSendSystemData` 新增 `TempStreamInitialSize`，用于调整服务器序列化 Ghost 时临时缓冲区的初始大小，现默认设为 8 KB
* 新增 `AlwaysRelevantQuery`，无需逐个 Ghost 配置即可定义通用相关性规则
* 支持 `NetCodeConnectionEvents`，可通过 `NetworkStreamDriver` 单例的 `ConnectionEventsForFrame` 属性访问，为追踪客户端连接和断开事件提供 `ConnectionState` 之外的选择
* 在 Unity Editor 中单步执行时，Multiplayer PlayMode Tools 窗口现会显示 `NetCodeConnectionEvent`

### 变更

* `StreamCompressionDataModel` 改为通过 `in` 参数传递，避免每次调用 `WriteXXX` 或 `ReadXXX` 时发生多次复制
* 将 Burst 依赖更新至 1.8.12
* Netcode 销毁已断开 `NetworkConnection` 实体时使用的 `EntityCommandBuffer` 从 `BeginSimulationEntityCommandBufferSystem` 改为 `NetworkGroupCommandBufferSystem`；常见情况下，若在后者执行前调用 `Disconnect`，连接可在同一帧释放而不再延迟一帧；依赖这一个帧延迟的用户代码将发生运行时异常，因此这是小型破坏性变更

### 修复

* 修复升级时保留已废弃 EditorPref 值会导致用户无法在 UI 中启用 Network Emulator 的问题
* 修复预序列化 Ghost 在特定条件下损坏内存、崩溃或把错误数据复制到 Snapshot Buffer 的问题
* 直接使用非托管函数指针，避免每次调用 `FunctionPointer.Invoke` 时产生 GC 分配和昂贵的 `Marshal.GetDelegateFromFunctionPointer` 调用，同时兼容随时启用或禁用 Burst
* 减少循环不变量引起的大量内存复制，并减少部分 Safety Check 和高开销操作
* 临时缓冲区无法容纳全部数据时，不再对整个 Chunk 执行昂贵的重复序列化；该操作是序列化循环的主要开销之一，默认缓冲区增至 8 KB 后几乎不会发生
* 服务器模拟中的 `InterpolationTick` 现始终等于 `ServerTick`，与参数摘要描述一致，并修正摘要中的拼写错误
* 修复在复制内部预生成 Ghost 前更新相关性列表会导致预生成初始化失败的问题

## [1.2.0-pre.6] - 2023-12-13

### 变更

* 正式发布准备


## [1.2.0-pre.4] - 2023-11-28

### 新增

* 现在可通过两种方式禁用 Entities `ICustomBootstrap` 的自动引导流程，即对 NetCode 自身 `ClientServerBootstrap` 的调用：在 Project Settings 中禁用默认开启的选项，或在第一个构建场景，即 Active Scene，添加新的 `OverrideAutomaticNetcodeBootstrap` MonoBehaviour；因此不再需要仅为区分 Frontend 和 Gameplay 场景编写自定义 Bootstrap
* 新增包含大多数 NetCode 配置变量的 `NetCodeConfig` ScriptableObject，无需修改代码即可自定义，且大多数变量可在运行时调整
* 新增用于准确测量丢包的 Snapshot Sequence Id（SSId）；每份快照增加 1 字节 Header，但可测量 Netcode 自身造成的丢包，例如丢弃乱序快照或同一帧到达多份快照时丢弃其中一份；统计数据可通过客户端 `NetworkSnapshotAck` 上的新结构访问
* 新增 `RpcCollection.GetRpcHeaderLength` 和 `NetworkStreamDriver.GetMaximumHeaderSize`，用于确定安全 Payload 最大值

### 修复

* 修复仅服务器模式下 `MultiplayerPlaymodeWindow` 中的罕见异常
* 插值 Ghost 现支持 `IInputComponentData` 和 `AutoCommandTarget`
* 改进 `UpdateGhostOwnerIsLocal`，使其响应 `GhostOwner` 变化，不再需要轮询
* 修复预测 Ghost 包含被复制的 Enableable 标记组件时出现的 NetDbg `ArgumentException`
* 修复仅影响显示的问题：由 Baking 创建的附加实体曾按根实体计算 Variant，但它们实际是子实体，因此自动选择的 Variant 应采用子实体默认值
* 允许用户在选中 GameObject 时关闭 `GhostAuthoringInspectionComponent` 自动 Baking，减少在 Hierarchy 或 Project 窗口中切换选择时的卡顿
* 修复 `GhostAuthoringInspectionComponent` 在 Editor 内允许编辑的区域中有时仍无法修改的问题
* 修复嵌套 Prefab 中根 Prefab 不是 Ghost 时禁止使用 `GhostAuthoringComponent` 的问题
* 修正 `DefaultDriverConstructor` 创建 Driver 时像是在要求用户采取操作的日志措辞
* 修复调用 `NetworkDriverStore.Disconnect` 时内部 Driver 被覆盖，并在罕见情况下引发异常的问题

## [1.2.0-exp.3] - 2023-11-09

### 新增

* `GhostInputSystemGroup` 和 `GhostSimulationSystemGroup` 现包含在 LocalSimulation World 中，因此输入轮询系统会自动加入 LocalWorld，便于单机测试流程；为空的 `SystemGroup` 执行 Tick 开销可忽略，因此不影响 LocalWorld 性能
* 调试包围盒现支持 GameObject 渲染；此前已支持 Entities Graphics，详情参阅 PlayMode Tools 文档
* `ConvertToGhostPrefab` 现会在 `EntityName` 为空时将其设为配置的 GhostName，适合动态创建的 Entity Prefab
* 现可正确检测实现了继承自 `IComponentData` 或 `IBufferElementData` 的泛型接口的组件、Buffer 和 Command，并生成序列化代码

### 变更

* 为便于维护，组件和 Buffer Serializer 的代码生成改用包内 Helper 方法，无用户可见变化
* 将 Transport 依赖更新至 2.1.0
* Editor 最低支持版本改为 2022.3.11f1
* private 或 internal 的组件、Command、Buffer 和 RPC 现也可复制

### 移除

* 移除对 `com.unity.logging` 的强制依赖；此前使用 Netcode for Entities 必须安装 Logging 包，现在该包为可选依赖

### 修复

* `ClientServerBootStrap` 中原本抛出 `NotImplementedException` 的三处位置，即 `CreateServerWorld`、`CreateClientWorld` 和 `CreateThinClientWorld`，现会正确抛出 `PlatformNotSupportedException`
* 服务器变更 Owner Predicted Ghost 的 Owner 时，Ghost 现会根据 Owner 自动在插值与预测模式之间切换
* 修复部分组件发送优化导致数据反序列化错误的问题，该优化用于仅存在于插值或预测 Ghost 上，或根据 Owner 决定是否存在的组件
* 修复 Enable Bit 序列化忽略 `GhostComponentAttribute.SendToOwner`，无论设置如何都用最新服务器数据覆盖状态的问题
* 修复组合使用 `GhostComponent.PrefabType` 标志时的代码生成问题
* 修复使用 PlayMode Tools 的 Timeout 功能时出现 `Error: Invalid context argument index` 的问题
* 更新覆盖 Variant 规则时的日志消息
* 修复存在 Ghost Hash 不匹配时 `GhostCollectionSystem` 中的 `IndexOutOfRangeException`，该问题在开发期间较常见
* 修复多个 Ghost 变更 Owner 并需要切换预测模式时，访问 `m_PredictionSwitchingSmoothingLookup` Buffer 的问题
* 修复 `GhostPrefabCreation.ConvertToGhostPrefab` 错误复制根实体 Variant 并分配给子实体组件的问题
* 支持注册自定义 Chunk 序列化函数指针来优化 Ghost 序列化和预序列化，使用户可按 Archetype 编写针对用例优化的序列化代码，而无需虚方法的函数指针间接调用
* 修复普通 Ghost 序列化的慢路径：Chunk 数据无法放入临时 Stream Buffer 时会多次重复序列化同一 Chunk；当实体因组件数量或组件大小而具有较大序列化体积时，该情况此前非常常见


## [1.1.0-pre.3] - 2023-10-17

### 变更

* `DefaultTranslationSmoothingAction.DefaultStaticUserParams` 现为 public，用户代码可修改默认值或在自定义平滑方法中使用

### 修复

* 修复使用预测误差平滑时从备份 Buffer 取得错误组件数据，导致平滑函数无法按预期工作的问题
* 修复存在部分 Tick 且 `PredictedFixedStepSimulationTickRatio` 大于 1 时，执行 `PredictedFixedStepSystemGroup` 所报告的已用时间不正确，进而影响物理和角色控制器插值的问题
* 修复序列化或反序列化包含超过 32 个字段的组件时，Change Mask 读取错误的问题
* 修复 `GhostComponentSerializerCollectionSystemGroup` 中的 `InvalidOperationException: Comparison function is incorrect`；原因是 `ComponentTypeSerializationStrategy.DefaultType` 为 `byte` 标志枚举，溢出后会错误地把 `128 - 0` 与 `0 - 128` 视为相同结果


## [1.1.0-exp.1] - 2023-09-18

### 新增

* Source Generator 现可配置启用或禁用日志、报告耗时，并可设置最低日志级别，现默认值为 Error
* 新增公开的模板规范和生成器文档
* 为 `ClientServerBootstrap` 新增获取 Client World、Server World 或 Thin Client 列表的便捷方法
* 新增 Analytics 事件，包括 Multiplayer Tools 字段、预测模式切换计数器和 Tick Rate 配置
* 为 `PredictedFixedStepSimulationSystemGroup` 新增方法，可把执行频率初始化为基础 Tick Rate 的倍数
* Network Simulator 现可配置 `Packet Fuzz %`；这是安全测试工具，正常测试期间不应启用，用于模拟恶意中间人通过触发数据包反序列化异常使服务器宕机的攻击；因此所有反序列化代码都应包含保护和容错，使逻辑能够平稳失败
* 新增 `CopyInputToCommandBufferSystemGroup`，包含把 `IInputCommandData` 复制到下层 `ICommand` Buffer 的所有系统；以该组为排序目标时，可保证它执行后不会再复制输入
* 新增 `CopyCommandBufferToInputSystemGroup`，包含把 `ICommandData` 复制到对应 `IInputCommandData` 表示的所有系统；它在预测循环最前执行，便于将逻辑安排在输入更新前后
* 新增 `GhostSpawnClassificationSystemGroup`，用于集中容纳所有生成分类系统
* 为部分遗漏 `NetworkDriver.BeginSend` 或 `EndSend` 的位置增加错误消息
* 定义 `ENABLE_UNITY_RPC_REGISTRATION_LOGGING` 后，Netcode 启动期间会记录已注册 RPC 的信息
* 现在会自动检测多人游戏期间 `Application.runInBackground` 被设为 false 的常见错误，并通过可抑制的错误日志说明应启用该选项的原因
* 新增 `InputBufferData<T>` Buffer，作为所有 `IInputComponentData` 的底层容器
* 为 `DefaultDriverBuilder` 的部分公开接口增加条件编译，使 WebGL 构建不包含无法监听的 `RegisterServer` 方法；仍可手动完成相关操作，但不再提供这些 Helper 方法
* 新增使用 `WebSocketNetworkInterface` 创建 `NetworkDriver` 的方法
* 为 `NetworkStreamDisconnectReason` 枚举新增 `AuthenticationFailure` 和 `ProtocolError`；前者表示 Transport 配置为 DTLS 或 TLS 后无法建立安全会话，后者表示底层发生意外 Transport 错误，例如 TCP Stream 中包含格式错误的数据包

### 变更

* 放宽 Variant 的 public 字段限制；声明 Ghost 组件 Variant 时，Variant 字段不再必须为 public，使该类型基本只用于声明类型序列化
* `MultiplayerPlayModePreferences.MaxNumThinClients` 的 Thin Client 上限从 32 提升到 1000，便于在 Editor 内进行一定规模的高玩家数测试
* `NetcodeTestWorld` 现按包本身的顺序更新 World：先更新服务器，再更新所有客户端 World
* 安装 Dedicated Server 包后，PlayMode Type 值会被当前 Multiplayer Role 覆盖

### 废弃

* 废弃公开的 `PredictedFixedStepSimulationGroup.TimeStep`；应始终使用 `PredictedFixedStepSimulationGroup.ConfigureTimeStep` 配置 `PredictedFixedStepSimulationSystemGroup` 的执行频率
* 废弃供代码生成内部使用但此前为 public 的 `IInputBufferData` 接口，并将在 1.2 版本移除

### 修复

* 修复在部分情况下 `GhostFieldAttribute.Composite` 设为 true 时，为组件和 Buffer 生成的序列化代码及计算的 Change Mask 位不正确的问题
* 修复 `GhostComponentVariation` 中错误的类型名检查
* 修复未量化 float 模板缺少 Region，导致用于插值字段时报错的问题
* 修复保存 `ClientServerSetting` Asset 时检查不正确，导致 Worker Process 无法看到设置变更的问题
* 修复 Bootstrap 未配置执行频率时，Server World 没有为该组设置正确频率的问题
* 修复 NetDbg 工具连接 Editor 或 Player 时抛出异常的问题
* 将 Multiplayer PlayMode Tools 窗口重命名并小幅改进为 PlayMode Tools，以免和引擎功能 `[MPPM] Multiplayer Play Mode` 混淆
* 修复通过 Assembly Definition Reference 等方式访问 Netcode for Entities 内部成员时，因 AOT 与 Unity.Entities 中的 `MonoPInvokeCallbackAttribute` 名称歧义而导致编译错误的问题
* 修复同时使用相关性、Despawn 和 Packet Dump 时 Packet Dump 日志抛出异常的问题，并消除错误记录堆栈引起的性能开销
* 修复 Release 构建中的 Variant Hash 计算问题，该问题会使 Ghost Prefab Hash 在 Development 或 Editor 与 Release 构建之间不一致
* `GhostUpdateSystem.RestoreFromBackup` 现仅在 Chunk 自上次恢复后发生变化时，才使组件 Chunk Version 失效或递增
* 修复 `TryGetHashElseZero` 使用 `ComponentType.GetDebugName` 计算 Variant Hash，导致 Release Player 构建结果错误的问题
* 修复 `NetworkDriver.BeginSend` 错误导致 `RpcSystem` 无限循环的问题
* 修复 Unity 2023.2 或更高版本使用已废弃 Analytics API 的问题
* 修复 Unity 2023.2 中因 Editor 和 Entities.Editor 程序集中重复定义符号而导致的编译问题
* 下列不应出现的 Netcode 系统不再加入 DefaultWorld：`PhysicsWorldHistory`、`SwitchPredictionSmoothingPhysicsOrderingSystem`、`SwitchPredictionSmoothingSystem`、`GhostPresentationGameObjectTransformSystem`、`GhostPresentationGameObjectSystem` 和 `SetLocalPlayerGraphicsColorsSystem`
* 过去很难取得给定 `IInputComponentData` 对应的生成 Buffer，现在可直接使用 `InputBufferData<MyInputComponent>`
* 修复 WebGL 构建编译错误
* 修复 `SnapshotDataLookupCache` 创建顺序错误，导致使用 `SnapshotBufferHelper` 的自定义分类系统因 Cache 未初始化而抛出异常的问题
* 修复把复制的 `[GhostEnabledBit]` 标记组件添加到预生成 Ghost 时，`ArchetypeChunk.GetDynamicComponentDataArrayReinterpret` 抛出 `ArgumentException` 的问题


## [1.0.17] - 2023-09-11

### 新增

* 定义 `ENABLE_UNITY_RPC_REGISTRATION_LOGGING` 后，Netcode 启动期间会记录已注册 RPC 的信息

### 变更

* NetcodePacket 调试日志文件名现包含日期、时间和版本信息

### 修复

* 修复 RPC 已排入队列后连接被断开时服务器可能抛出异常的问题
* 修复 Unity 2023.2 或更高版本中，`NetCodeClientSettings`、`NetCodeClientServerSettings` 和 `NetCodeServerSettings` 的 `OnDisable` 方法抛出 `AssetDatabase.RegisterCustomDependency are restricted during importing` 异常的问题


## [1.0.15] - 2023-07-27

### 变更

* 将 `com.unity.entities` 依赖更新至 1.0.14
* 不再进行不必要的 TempJob 分配，改用 `Allocator.Temp`

### 修复

* 修复运行时持续分配但不释放 Query 所造成的 `EntityQuery` 泄漏，并降低运行时内存压力
* 避免在热点路径中持续分配 Query，降低 Editor 测试的内存占用


## [1.0.12] - 2023-06-19

### 变更
* 将 `com.unity.entities` 依赖更新至 1.0.11


## [1.0.11] - 2023-06-02

### 修复

* 更新 Logging 依赖


## [1.0.10] - 2023-05-23

### 新增

* 在文档中新增“新增内容”和“升级指南”章节
* 新增 `NetworkRequestListenResult` Cleanup Component，可用于追踪 Listen Request 的结果

### 变更

* 更新文档索引页中的信息和链接
* 移除强制本地客户端和服务器始终通过 Loopback Address 连接的行为
* IPC 连接现也可监听 `NetworkEndPoint.Any`
* `NetworkDriver` 配置为使用 Unity Relay 时，`NetworkStreamDriver.GetRemoteAddress` 现始终返回一致的连接地址；此前建立连接后会错误返回无效地址
* 将 `NetworkTimeSystem` 的全部内部状态公开为公共 API

### 修复

* 修复处理 `NetworkRequestListen` 和 `NetworkRequestConnect` 时的异常，并正确处理同时存在多个错误 Request 的情况
* 修复服务器或客户端发生较长时间卡顿时，例如加载之后，`InterpolatedTick` 回退后无法正确恢复的问题
* `MultiplayerPlayModeWindow > Dump Packet Logs` 现更可靠，支持 NUnit 测试，且 Dump 文件名包含更多上下文
* 修复启用 Packet Dump 时 `GhostSendSystem` 不复制 Ghost 的问题，并新增 `GhostValuesAreSerialized_WithPacketDumpsEnabled` 测试


## [1.0.8] - 2023-04-17

### 变更

* 根据运行平台所需的最大 Worker Thread 数量分配内存，不再默认按理论上限 128 个 Worker Thread 分配，从而降低内存占用
* 移除每个预测 Ghost Prefab 为支持预测生成而创建的附加实体；当所有 Ghost Prefab 都支持全部模式时，还可将所需 Archetype 数量减少近一半


### 修复

* 修复客户端与服务器计算 SubScene Hash 的方式不同，导致预生成 Ghost 无法正常工作的问题
* 修复为 Live Conversion 和 Baking 打开 SubScene 时，生成的 Ghost 包含无效 Blob Asset 引用，例如 Collider，可能引发崩溃、碰撞缺失和错误预测的问题
* 修复为客户端独立构建 Baking SubScene 时，未使用正确 `NetCodeClientTarget`，即 Client 或 Client/Server 的问题
* 修复 Project Settings 窗口未关闭或切换到其他设置页时，Entities/Build 设置 UI 不会更新要使用的 ClientTarget 的问题
* 修复无需服务器且未创建服务器时，`HasServerWorld` 仍报告存在 Server World 的问题
* 修复获取 `Unity.NetCode.LowLevel.SnapshotDataLookupCache` 时偶发的 `InvalidOperationException: GetSingleton<Unity.NetCode.LowLevel.SnapshotDataLookupCache>()`
* 修复 Ghost Prefab 验证失败后尝试访问失效 `DynamicBuffer`，导致 `GhostCollectionSystem` 抛出 `InvalidOperationException` 的问题
* 修复 `GhostChunkSerializer` 用部分 Enable Bit Mask 覆盖 Snapshot Data 的问题
* 修复 `GhostUpdateSystem` 读取并应用错误 Enable Bit 的问题
* 修复从预测 Ghost History Buffer 恢复 Enable Bit 状态的问题
* 修复 System 创建顺序问题，该问题会使带 `[GhostField]` 字段或 `[GhostEnableBit]` 特性的组件静默采用 `DontSerializeVariant`，尤其发生在通过 `GhostPrefabCreation.ConvertToGhostPrefab` 于运行时创建 Ghost Prefab 时
  * Ghost Registration 和 Default Variant Registration 系统现使用 `[CreateBefore(typeof(DefaultVariantSystemGroup))]`，因此用户代码访问 `GhostComponentSerializerCollectionData` 时可添加 `[CreateAfter(typeof(DefaultVariantSystemGroup))]`
  * 现在也会保护所有这些调用，误用时给出明确的致命错误
* 修复用户项目启用 `GhostDistanceImportance` 后，`GhostDistancePartitioningSystem` 每帧都为每个包含 `LocalTransform` 的 Ghost 添加一条 Shared Component ECB 记录的问题


### 废弃

* `GhostAuthoringInspectionComponent` 现已显示所有复制组件，因此无需再主动启用 Prefab Override，故废弃 `SupportsPrefabOverrides` 特性


## [1.0.0-pre.66] - 2023-03-21

### 新增

* 使用 `IPCNetworkInterface` 时验证并清理 Connect 和 Listen Address，避免 Transport 发生难以定位原因的严重崩溃

### 变更

* 下列组件已重命名：
NetworkSnapshotAckComponent: NetworkSnapshotAck,
IncomingSnapshotDataStreamBufferComponent: IncomingSnapshotDataStreamBuffer,
IncomingRpcDataStreamBufferComponent: IncomingRpcDataStreamBuffer,
OutgoingRpcDataStreamBufferComponent: OutgoingRpcDataStreamBuffer,
IncomingCommandDataStreamBufferComponent: IncomingCommandDataStreamBuffer,
OutgoingCommandDataStreamBufferComponent: OutgoingCommandDataStreamBuffer,
NetworkIdComponent: NetworkId,
CommandTargetComponent: CommandTarget,
GhostComponent: GhostInstance,
GhostChildEntityComponent: GhostChildEntity,
GhostOwnerComponent: GhostOwner,
PredictedGhostComponent: PredictedGhost,
GhostTypeComponent: GhostType,
SharedGhostTypeComponent: GhostTypePartition,
GhostCleanupComponent: GhostCleanup,
GhostPrefabMetaDataComponent: GhostPrefabMetaData,
PredictedGhostSpawnRequestComponent: PredictedGhostSpawnRequest,
PendingSpawnPlaceholderComponent: PendingSpawnPlaceholder,
ReceiveRpcCommandRequestComponent: ReceiveRpcCommandRequest,
SendRpcCommandRequestComponent: SendRpcCommandRequest,
MetricsMonitorComponent: MetricsMonitor,

### 移除

* 移除内部 `ListenAsync` 和 `ConnectAsync` 方法，用户可见 API 无变化

### 修复

* 修复 Ghost 包含没有任何 Prediction Error 名称的复制组件，例如 Entity 引用时，极少发生的异常
* 修复记录缺少程序集依赖时 Source Generator 崩溃的问题
* 移除 Source Generator 生成序列化代码时对 Unity.Transport 包依赖的要求
* 修复 Snapshot History Buffer 恢复错误，导致 Entity 组件被随机数据覆盖的问题
* 修复 `ClientServerBootstrap.AutoConnectPort` 为 0，表示禁用自动连接并由用户通过 Driver Connect API 手动连接时，PlayMode Tools 的 IP 和 Port 字段仍会触发连接，进而创建两个连接并报错的问题；现也会阻止连接已经建立时再次发起连接
* 修复 Source Generator 错误验证使用 Override 的自定义模板的问题
* 移除转换包含预生成 Ghost 的 SubScene 时关于旧临时分配的警告
* 强制所有 `ICommandData` 的 `InternalBufferCapacity` 为零；Netcode 输入 Buffer 的 Dynamic Buffer 所需长度硬编码为 64，确定无法放入内部容量，此前每个实体会持续浪费数百字节
* 修复 Send Queue 满时 Player 可能崩溃的问题
* 修复尝试使用无效 Interpolation Tick 时的异常，该情况可能发生在 Snapshot 更新期间，或连接断开后的预测生成系统中


## [1.0.0-pre.44] - 2023-02-13

### 新增

* 为 `GhostDistanceData.TileSize` 增加验证，防止分配无效 Tile 或抛出 `DivideByZeroException`
* 为 `DisableAutomaticPrespawnSectionReportingAuthoring`、`GhostAuthoringComponent`、`GhostAuthoringInspectionComponent`、`DefaultSmoothingActionUserParamsAuthoring`、`GhostPresentationGameObjectAuthoring`、`NetCodeDebugConfigAuthoring`、`GhostAnimationController`、`GhostPresentationGameObjectEntityOwner` 和 `NetCodePhysicsConfig` 增加指向文档的 HelpURL
* 为 `NetworkStreamDriver` 新增 `GetLocalEndPoint` API

### 变更

* 将 `EnablePacketLogging` 组件设为 public，以支持按连接记录调试信息
* 将 `com.unity.transport` 依赖更新至 2.0.0-pre.6

### 废弃
* `ProjectSettings / NetCodeClientTarget` 此前实际保存到 `EditorPrefs` 而非 Project Settings，破坏了跨机器构建的确定性；修复后原 EditorPref 会被覆盖，并废弃 `ClientSettings.NetCodeClientTarget`，请改用 `NetCodeClientSettings.instance.ClientTarget`

### 修复

* 修复 Editor 启用 Domain Reload 后切换 Play Mode 时，`NetworkEmulator` 导致游戏强制立即退出 Play 状态的问题
* 修复被 Baking 的实体没有 `LocalTransform`，即 Transform V1 的 Position 或 Rotation 组件时，预生成 Ghost Baking 的问题
* 修复 Ghost Distance Importance Scaling 失效的问题，请阅读更新后的文档
* 补充 `NetworkStreamListenSystem.OnCreate` 中遗漏的字段写入，修复 Relay Server
* 代码生成且经 Burst 编译的 Serializer 方法现仅在具有 `WorldFlag.GameClient` 或 `WorldFlag.GameServer` 标志的 World 内编译，从而提升启用 Domain Reload 时退出 Play Mode、所有情况下的 Baking 以及重新编译速度
* 修复具有相同 Archetype 但数据不同的多个 Ghost 类型偶尔触发 Ghost 改变类型错误的问题
* 修复 Relay 示例错误创建 Client Driver 而非 Server Driver 的问题
* 修复客户端 Relay 设置逻辑；调用带 Relay 设置的 `DefaultDriverConstructor.RegisterClientDriver` 时，仅在请求的 PlayType 为 Client、未找到服务器时为 ClientAndServer、启用 Simulator，或处于仅客户端构建时才执行
* 修复 `GhostPredictionHistorySystem.PredictionBackupJob` 中的 `ArgumentException: ArchetypeChunk.GetDynamicComponentDataArrayReinterpret<System.Byte> cannot be called on zero-sized IComponentData`，并通过为 `GhostSerializationTestsForEnableableBits` 增加预测 Ghost 版本，为 `GhostPredictionHistorySystem` 补充完整测试覆盖
* `GhostUpdateSystem` 现支持 Change Filtering，因此客户端组件只会在实际变化时标记为已变化；强烈建议客户端读取包含 `[GhostField]` 和 `[GhostEnabledBit]` 的组件时实现 Change Filtering
* 修复输入组件类型嵌套在父类中时的代码生成问题


## [1.0.0-pre.15] - 2022-11-16

### 新增

* 新增 Client & Server Bounding Boxes 调试绘制器，位于 `Packages\com.unity.netcode\Editor\Drawers\BoundingBoxDebugGhostDrawerSystem.cs`，可对比客户端认为的 Ghost 绝对位置和服务器上的真实位置；也可用于可视化相关性逻辑，因为能看到对当前客户端不相关的服务器 Ghost 控件；可通过 Multiplayer PlayMode Tools 窗口启用或禁用
* 在 `NetCodeClientSetting` Project Setting 中新增 `FRONTEND_PLAYER_BUILD` Scripting Define
* 新增 `GhostSpawnBufferInspectorHelper` 和 `GhostSpawnBufferComponentInspector`，允许从 Ghost Spawn Buffer 读取任意类型组件，可在生成分类系统中辅助解析预测生成 Request
* 支持把 `GhostTypeComponent` 显式转换为 `Hash128`
* 新增 `double` 类型序列化模板
* 新增 `TransformDefaultVariantSystem`；用户未提供默认值时，可选地为 `LocalTransform`，即 Transform V1 的 `Rotation` 或 `Position` 设置默认 Variant
* 新增 `PhysicsDefaultVariantSystem`；用户未提供默认值时，可选地为 `PhysicsVelocity` 设置默认 Variant
* 为 `NetworkStreamDriver` 新增 `GetLocalEndPoint` API
* `GhostAuthoringInspectionComponent` 现提供更多默认 Variant 选择信息

### 变更

* 将 `com.unity.transport` 依赖更新至 2.0.0-exp.4
* 客户端 Ghost Prefab 也会添加 `SharedGhostTypeComponent`，把 Ghost 拆分到不同 Chunk
* `GhostTypeComponent` 现等于并匹配 Prefab GUID
* 移除 `CodeGenTypeMetaData`，并调整 `VariantType` 结构的内部生成方式；同时将 `VariantType` 重命名为 `ComponentTypeSerializationStrategies`，更准确地表达用途并与 Variant 概念区分
* 通过可添加到组件结构体的新特性 `GhostEnabledBitAttribute`，即 `[GhostEnabledBit]`，支持复制 `IEnableableComponent` 的 Enable Bit；若未添加该特性，即便组件带 `[GhostField]`，Enable Bit 也不会复制；这是破坏性变更，所有包含 Ghost Field 的 Enableable Component 现在都必须在结构体声明上添加 `[GhostEnabledBit]`
* 所有 `DefaultVariantSystemBase` 现归入 `DefaultVariantSystemGroup`
* 项目添加 `LocalTransform`，即 Transform V1 的 `Rotation` 或 `Position`，或 `PhysicsVelocity` Variant 时，不再需要定义自定义 `DefaultGhostVariant` 系统，因为包已提供默认选择
* 将 `com.unity.transport` 依赖更新至 2.0.0-pre.2

### 移除

* 移除对 `com.unity.jobs` 包的依赖

### 修复

* 修复 Input Buffer 类型位于默认命名空间时 Source Generator 报错的问题
* 始终通过 `ref` 传递 `SystemState`，避免只在副本而非原值中重新分配 `UnsafeList`
* Analytics 中的预生成数量改用正确数据类型
* 使用 `EditorAnalytics` 检查 Analytics 是否启用
* 修复场景实体没有 SubScene 组件时 Hierarchy 窗口抛出异常的问题
* 修复 `GhostComponentSerializerRegistrationSystem` 和 Ghost Metadata 注册系统在 `GhostComponentSerializerCollectionData` 创建前尝试访问它的问题
* 修复不同 Ghost 类型或 Prefab 共用相同 Archetype 时，`GhostUpdateSystem`、`GhostPredictionHistorySystem` 等系统崩溃的问题
* 修复从 Frontend 场景启动 Demo 时 NetCodeSample 项目画面闪烁并重复渲染实体的问题
* 修复序列化实体期间抛出异常时，`GhostSendSystem` 无法中止 DataStream 的问题
* 修复把 Variant 恢复为默认值时 `GhostAuthoringInspectionComponent` 抛出 `InvalidOperationException` 的问题
* 修复 `GhostAuthoringInspectionComponent` 的 UI 布局问题
* 修复 Baking 期间 `GhostAuthoringInspectionComponent.ComponentOverrides` 的 Hash 问题，过期 Hash 此前仍会写入 `BlobAsset`；现在会报告错误并指向对应的过期 Ghost Prefab
* 修复 `quaternion` 无法作为 `ICommandData` 或 `IInputComponentData` 字段的问题，并为其他类似情况在代码生成模板中新增 Region
* 修复 `DefaultVariantSystemBase.Rule` 使用 `DontSerializeVariant` 或 `ClientOnlyVariant` 时的 Hash 生成，现改用常量 Hash `DontSerializeHash` 和 `ClientOnlyHash`
* `NetDbg` 现能在 Prediction Errors 部分正确显示较长命名空间，并改善可读性
* 移除包中的 CSS 警告
* 修复 Baking 附加 Ghost 实体时移除 `LocalTransform`、`WorldTransform` 和 `LocalToWorld` 矩阵的问题
* `ClientServerTickRate.SimulationTickRate` 与 `PredictedFixedStepSimulationSystemGroup.RateManager.Timestep` 不匹配时会抛出错误，并将二者设为一致值
* 改进 `GhostAuthoringInspectionComponent`：消除 Baker 创建大量附加实体时的卡死，改善 Input 显示，修复未保存 `EntityGuid` 导致无法修改附加 Entity 的问题；现也会检测但不销毁损坏的 `ComponentOverrides`，便于从 `TRANSFORMS_V1` 等配置迁移
* 修复 `SentForChildEntities = true` 时子实体组件的序列化；此修复可能轻微降低 Baking 和 Netcode World 初始化性能
* 在 Entity Inspector 中公开 `NetworkTick` 值
* 修复未复制 `ICommandData.Tick` 的代码生成错误
* 修复处理 Buffer、Command 和 Component 属性时，代码生成对 GhostField 错误的处理
* 修复特定条件下，例如未量化或位于 Command 中，`Entity`、`float`、`double`、`quaternion` 和 `ulong` 的代码生成异常；同时改善为 `ICommandData` 设置无效 `SmoothingAction` 时的异常报告
* 代码生成现支持最长 509 个字符的字段名，原为 61，字段过多时也不会抛出截断错误
* 为 `ICommandData` 增加错误日志：
  * 单个 `ICommandData` 尝试序列化超过 1024 字节时
  * `ICommandData` 批量发送发生写入失败时
* `ICommandData` Batch 现支持分片，因此写入多个 `ICommandData` 不再静默发送失败
* `ICommandData` 现正确支持 `float`、`double`、`ulong` 和 `Entity` 类型
* 修复多项 Variant 选择问题；尤其是 Default Serializer 的 `GhostComponentAttribute` 所定义的 `PrefabType` 规则，现会传播到其所有 `DontSerializeVariant`
* 优化字符串区域设置
* 修复禁止修改 Asset 时仍可修改并保存 Netcode Settings Asset 的问题


## [1.0.0-exp.8] - 2022-09-21

### 新增

* 新增统一的 `NetCodePhysicsConfig`，集中配置全部 Netcode Physics 设置；转换时会从这些设置生成 `LagCompensationConfig` 和 `PredictedPhysicsConfig`
* 预测 Ghost 物理现使用多个 Physics World：Predicted Physics World 模拟 Ghost 物理，另一个仅客户端 Physics World 可用于表现效果；详情参阅预测物理文档
* 连接时发生协议版本不匹配错误后，日志会输出协议使用的版本和 Hash，便于定位不匹配原因
* 增加合理性检查，防止更新无效 Ghost
* 新增 `GhostPrefabCreation.ConvertToGhostPrefab`，无需对应 Asset 即可通过代码创建 Ghost Prefab
* 支持创建多个 Network Driver；服务器现在可以通过不同 Network Interface 同时监听相同端口，例如同时使用 IPC、Socket 和 WebSocket
* 新增代码生成文档
* 新增 `RegisterPredictedPhysicsRuntimeSystemReadWrite` 和 `RegisterPredictedPhysicsRuntimeSystemReadOnly` 扩展方法，用于使用预测网络物理系统时追踪依赖
* 支持运行时修改 Thin Client 数量
* 新增 `NetworkTime` 组件，包含客户端和服务器模拟的全部时间与 Tick 信息；项目更新方法参阅升级指南
* 支持 Enable Bit
* 新增 `IInputData` 输入接口，可自动把输入数据作为网络 Command Data 处理；系统会根据当前 Tick 自动将输入复制到 Command Buffer，并在需要时取回；同时新增可在该输入组件中可靠同步单次事件的 `InputEvent` 类型
* 设置 `ClientTickRate.MaxPredictionStepBatchSizeRepeatedTick` 和 `ClientTickRate.MaxPredictionStepBatchSizeFirstTimeTick` 后，可批量运行预测循环；输入变化时会拆分 Batch，除非变化的输入数据标记了 `[BatchPredict]`
* 优化 InProc 客户端/服务器和 IPC 连接，减少 Prediction Tick 数量与插值帧数
* 新增可添加到连接的 `ConnectionState` System State Component，用于追踪状态变化、新连接和断开
* 新增 `NetworkStreamRequestConnect` 组件，可添加到新实体以创建连接，而无需调用 `Connect`
* 为 `World` 和 `WorldUnmanaged` 新增 `IsClient`、`IsServer` 和 `IsThinClient` Helper 方法
* 增加对 Unity.Logging 包的依赖
* DOTS Hierarchy 现会标记 Ghost：粉色代表已复制，蓝色代表 Prefab；内置 Unity Hierarchy 也有类似标记，但受 API 限制功能较少
* `GhostAuthoringComponent` 现使用 Ghost 图标
* 更新重要度缩放函数和类型的 API 文档
* 新增 Predicted Physics API 文档
* 为 `DefaultDriverBuilder` 新增创建和注册 IPC、Socket Driver 的 Helper 方法；服务器在 Editor 中同时使用二者，Player 构建仅使用 Socket；客户端与服务器位于同一进程时使用 IPC，否则使用 Socket
* 新增 Ghost Metrics 单例 API
* 为 `DefaultDriverBuilder` 新增 `RegisterClientDriver` 和 `RegisterServerDriver` Helper 方法，接收初始化安全连接所需的证书和密钥
* 改进 `GhostAuthoringComponent` 窗口，并将 `ComponentOverrides` 移到新的可选组件 `GhostAuthoringInspectionComponent`
* Source Generator 现使用 `CancellationToken`，收到取消请求时提前退出执行
* 新增 `NetworkStreamRequestListen`，用于开始监听新连接，而无需调用 `NetworkStreamDriver.Listen`
* 为 `DefaultDriverBuilder` 新增接收 Relay Server 数据的 `RegisterClientDriver` 和 `RegisterServerDriver` Helper 方法，以通过 Relay Server 连接
* 为 Ghost 配置和场景设置规模新增 Analytics Callback
* 新增默认生成分类系统；若用户系统未先处理客户端预测生成，该系统会匹配生成 Tick 前后 5 Tick 内同类型 Ghost 的生成
* 优化 `GhostCollectionSystem` 导入和处理 Ghost Prefab 的流程
* 新增示例，展示如何在预测循环中备份和回滚未复制组件
* 新增 `ChangeMaskArraySizeInBytes` 和 `SnapshotHeaderSizeInBytes` Utility 方法
* 为 Dynamic Buffer 新增内部扩展 `ElementAtRO`，用于取得 Buffer 元素的只读引用

### 变更

* Hybrid 模式改由 Player Loop 驱动 Client World 和 Server World 的 Tick，不再依赖 Default World 通过 Tick System 更新二者
* 预测 Ghost 物理现使用自定义系统更新 Physics Simulation，内置系统改为更新仅客户端模拟
* 可序列化组件的 128 个上限现按实际使用的组件计算，而非项目中存在的全部组件
* 所有错误现都会报告位置，可点击 Console 日志中的错误跳转到对应源码文件或类
* 从所有模板移除未使用的 `__GHOST_MASK_BATCH__` Region
* 启用预测物理后，`PhysicsWorldHistory` 会向预测运行时物理数据注册只读依赖
* 修复 Package Cache 文件夹包含临时或无效目录名时 Source Generator 崩溃的问题
* 重构 Source Generator，并支持 `.additionalfile`，要求 Unity 2021.2 或更高版本
* 将 `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier` 重命名为 `ClientServerTickRate.MaxSimulationStepBatchSize`
* 用 `NetDebug` 单例替代 `NetDebugSystem.NetDebug`
* 用 `SpawnedGhostEntityMap` 单例替代 `GhostSimulationSystemGroup.SpawnedGhostEntityMap`
* 插值延迟现根据客户端感知到的平均 Snapshot Ratio 计算，有助于补偿丢包和抖动
* 改用 `StreamCompressionModel`，不再使用已废弃的 `NetworkCompressionModel`
* 多项改进 Multiplayer PlayMode Tools 窗口：增加代表真实网络速度的 Simulator Profile，支持运行时创建和销毁 Thin Client、实时修改 Simulator 参数，以及通过快捷键模拟延迟尖峰
* Ghost Relevancy Map 和 Mode 从 `GhostSendSystem` 移到 `GhostRelevancy` 单例
* `Connect` 和 `Listen` 方法移到 `NetworkStreamDriver` 单例
* Utility 方法 `GhostPredictionSystemGroup.ShouldPredict` 移到 `PredictedGhostComponent`
* `GhostCountOnServer` 和 `GhostCountOnClient` 从 `GhostReceiveSystem` 移到 `GhostCount` 单例 API
* 注册预测平滑函数的 API 从 `GhostPredictionSmoothingSystem` 移到 `GhostPredictionSmoothing` 单例
* 注册 RPC 和获取 RPC Queue 的 API 从 `RpcSystem` 移到 `RpcCollection` 单例
* 移除已过时的 `AlwaysUpdateSystem` 特性，并在适当位置添加新的 `RequireMatchingQueriesForUpdate` 特性
* 将 `GhostDistancePartitioningSystem` 转换为 `ISystem`
* 将 `GhostReceiveSystem` 转换为 `ISystem`
* 将 `GhostSendSystem` 转换为 `ISystem`，公共 API 移到名为 `GhostSendSystemData` 的 Singleton Entity
* 将 `PredictedPhysicsWorldHelper` 类可见性改为 internal
* `CommandReceiveClearSystem` 和 `CommandSendPacketSystem` 改为非 internal
* 将 `StartStreamingSceneGhosts` 和 `StopStreamingSceneGhosts` 改为内部 RPC；需要自定义预生成场景流程的用户必须添加自己的 RPC
* `PrespawnsSceneInitialized`、`SubScenePrespawnBaselineResolved`、`PrespawnGhostBaseline`、`PrespawnSceneLoaded` 和 `PrespawnGhostIdRange` 改为 internal
* `PrespawnSubsceneElementExtensions` 改为 internal
* `LiveLinkPrespawnSectionReference` 改为 internal；它仅在 Editor 中用于规避 Entities Conversion 限制，不应作为可由用户添加的公共组件
* internal 的 Component、Buffer、Command 和 RPC 现也会生成序列化代码
* 废弃 `GhostCollectionSystem.CreatePredictedSpawnPrefab` API；客户端现会自动配置可预测生成的 Ghost Prefab，可按常规方式实例化而无需调用该 API
* Ghost 子实体现默认使用 `DontSerializeVariant`，因为序列化子 Ghost 的代价较高，原因包括其他 Chunk 中子实体的引用局部性较差，以及遍历子实体需要随机访问；因此 `GhostComponentAttribute.SendDataForChildEntity = false` 现为默认值，应对所有需要发送给子实体的类型显式设为 true；若要复制层级，强烈建议创建多个 Ghost Prefab，并用自定义的伪 Transform 父子逻辑保持层级扁平；只有一个层级的 Snapshot Update 必须同步时才应使用显式子层级
* `RegisterDefaultVariants` 的签名改为使用 `Rule`，要求用户明确指定自定义默认值是否也应用于子实体
* 现在必须通过以下方式之一为特定类型显式启用 Prefab Override 自定义：
  **a)** 为组件添加 `[SupportPrefabOverride]` 特性
  **b)** 通过 `[GhostComponentVariation]` 添加组件的自定义 Variant
  **c)** 通过 `DefaultVariantSystemBase.RegisterDefaultVariant` 添加默认 Variant
  **注意：**也可通过 `[DontSupportPrefabOverride]` 特性显式禁止所有 Override
* 将 `GhostComponentAttribute.OwnerPredictedSendType` 重命名为 `GhostComponentAttribute.SendTypeOptimization`
* 用当前 API 替换已过时的 `EntityQueryBuilder` API
* 将 `SnapshotSizeAligned` 和 `ChangeMaskArraySizeInUInts` 移到 `GhostComponentSerializer` 类
* 将 `DefaultUserParams` 重命名为 `DefaultSmoothingActionUserParams`
* 将 `DefaultTranslateSmoothingAction` 重命名为 `DefaultTranslationSmoothingAction`


### 移除

* 移除静态 bool `RpcSystem.DynamicAssemblyList`，由同名非静态属性替代，参阅下方升级指南
* Editor 专用属性 `ClientServerBootstrap.RequestedAutoConnect` 由 `ClientServerBootstrap.TryFindAutoConnectEndPoint` 替代
* 移除 `ClientSimulationSystemGroup` 等自定义客户端和服务器顶层组，改用 `[WorldSystemFilter]` 和内置顶层组
* 移除 `[UpdateInWorld]`，改用 `[WorldSystemFilter]`
* 移除 `ThinClientComponent`，改用 `World.IsThinClient()`
* 移除接收 `SystemBase` 的 `PopulateList` 重载，调用方应从 `ISystem` 传入 `ref SystemState`；`DynamicTypeList` 改为 internal，用户代码不应使用

### 修复

* 修复发生回滚且 Prediction Tick 环绕零时，预测系统在客户端计算错误预测 Tick 数量的问题；同时修复客户端退出游戏或断开服务器时 Delta Time 和 Elapsed Time 突增的问题
* 修复 Source Generator 错误不在 Editor 中显示的问题
* 修复特定情况下 Ghost Physics Proxy 的大角度旋转同步错误
* 修复客户端尚未到达正确 Prediction Tick 就可能生成预测 Ghost 实体的罕见问题
* 修复罕见的 Interpolation Tick 回滚
* 修复从备份恢复组件和 Buffer 时未检查 `SendToOwner` 设置的问题
* 修复 IL2CPP 下 Packet Logger 导致 Android 和 iOS 崩溃的问题
* `GhostSendSystem.OnUpdate` 现使用 Burst 编译
* 修补 Entity GUID 时确保 Serial Number 唯一
* Analytics 结果不再统计长度为零的更新，并修复进入和退出 Play Mode 时的断言错误
* 修复选择 DedicatedServer 平台时的编译错误；这不代表 NetCode 包或其依赖包支持 Dedicated Server 平台

### 升级指南

* 建议使用新的统一 `NetCodePhysicsConfig` Authoring Component 启用延迟补偿，不再使用 `LagCompensationConfig` Authoring Component
* 所有对静态 `RpcSystem.DynamicAssemblyList` 的调用应替换为同名实例属性，并确保在 World 创建期间、`RpcSystem.OnUpdate` 调用前完成；示例参阅 `SetRpcSystemDynamicAssemblyListSystem`
* `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier` 已重命名为 `ClientServerTickRate.MaxSimulationStepBatchSize`
* 所有 Editor 专用的 `ClientServerBootstrap.RequestedAutoConnect` 调用应替换为支持全部 `PlayTypes` 的 `ClientServerBootstrap.TryFindAutoConnectEndPoint`
* 已移除 `NetworkStreamDisconnected` 组件；请为需要检测断开的连接添加 `ConnectionState` 组件，并使用响应式系统
* 使用 Netcode 日志系统时，`GetExistingSystem<NetDebugSystem>().NetDebug` 应替换为 `GetSingleton<NetDebug>()`；修改日志级别时使用 `GetSingletonRW<NetDebug>`
* `GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap` 应替换为 `GetSingleton<SpawnedGhostEntityMap>().Value`；不再需要等待或设置 `LastGhostMapWriter`，应移除相关代码
* `GetExistingSystem<GhostSendSystem>().GhostRelevancySet` 和 `GetExistingSystem<GhostSendSystem>().GhostRelevancyMode` 应替换为 `GetSingletonRW<GhostRelevancy>.GhostRelevancySet` 和 `GetSingletonRW<GhostRelevancy>.GhostRelevancyMode`；不再需要等待或设置 `GhostRelevancySetWriteHandle`，应移除相关代码
* `GetExistingSystem<NetworkStreamReceiveSystem>().Connect` 和 `GetExistingSystem<NetworkStreamReceiveSystem>().Listen` 应替换为 `GetSingletonRW<NetworkStreamDriver>.Connect` 和 `GetSingletonRW<NetworkStreamDriver>.Listen`
* `ThinClientComponent` 应替换为 `World.IsThinClient()` 调用
* 已移除 Netcode 专用顶层 System Group 和 `[UpdateInWorld]`，请改用 `[WorldSystemFilter]`；映射关系为：   * `[UpdateInGroup(typeof(ClientInitializationSystemGroup))]` => `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInGroup(typeof(ClientSimulationSystemGroup))]` => `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInGroup(typeof(ClientPresentationSystemGroup))]` => `[UpdateInGroup(typeof(PresentationSystemGroup)]`   * `[UpdateInGroup(typeof(ServerInitializationSystemGroup))]` => `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`   * `[UpdateInGroup(typeof(ServerSimulationSystemGroup))]` => `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`   * `[UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup))]` => `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]` => `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInWorld(TargetWorld.Client)]` => `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInWorld(TargetWorld.Server)]` => `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]`   * `[UpdateInWorld(TargetWorld.ClientAndServer)]` => `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]`   * `[UpdateInWorld(TargetWorld.Default)]` => `[WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]`   * `if (World.GetExistingSystem<ServerSimulationSystemGroup>()!=null)` => `if (World.IsServer())`   * `if (World.GetExistingSystem<ClientSimulationSystemGroup>()!=null)` => `if (World.IsClient())`
* 已移除 `GhostCollectionSystem.CreatePredictedSpawnPrefab` API；客户端现会自动配置可预测生成的 Ghost Prefab，可按常规方式实例化而无需调用该 API
* `GhostPredictionSystemGroup` 已重命名为 `PredictedSimulationSystemGroup`
* 升级期间所有 `GhostAuthoringComponent.ComponentOverrides` 都会被覆盖；请通过新的可选 `GhostAuthoringInspectionComponent` 重新应用；应尽可能优先使用特性，这类手动 Override 只用于无法通过特性表达的一次性差异
* 在 `RegisterDefaultVariants` 方法内，将所有 `defaultVariants.Add(new ComponentType(typeof(SomeType)), typeof(SomeTypeDefaultVariant));` 替换为 `defaultVariants.Add(new ComponentType(typeof(SomeType)), Rule.OnlyParent(typeof(SomeTypeDefaultVariant)));`；若还希望 Variant 应用于子实体，则使用 `Rule.ParentAndChildren(typeof(SomeTypeDefaultVariant))`

### 使用新的 NetworkTime 组件

所有当前模拟 Tick 信息都必须从 `NetworkTime` 单例取得，具体包括：
* 已移除 `GhostPredictionSystemGroup.PredictingTick`，必须改用 `NetworkTime.ServerTick`；在预测循环内读取时，`ServerTick` 会正确反映当前 Prediction Tick
* 已移除 `GhostPredictionSystemGroup.IsFinalPredictionTick`，改用 `NetworkTime.IsFinalPredictionTick` 属性
* 已移除 `ClientSimulationSystemGroup` 的 `ServerTick`、`ServerTickFraction`、`InterpolationTick` 和 `InterpolationTickFraction`；可从 `NetworkTime` 单例取得相同属性，不同时间属性和标志行为的详情参阅 `NetworkTime` 组件文档


## [0.51.1] - 2022-06-27

### 变更

* 包依赖
    * 将 `com.unity.entities` 更新至 `0.51.1`

## [0.51.0] - 2022-05-04

### 变更

* 包依赖
    * 将 `com.unity.entities` 更新至 `0.51.0`
* 将 Transport 依赖更新至 1.0.0

### 新增

* 编译不引用 Netcode 包的程序集时，阻止 Netcode Generator 运行


## [0.50.1] - 2022-03-18

### 新增

* DOTS Runtime 构建不再包含 Hybrid 程序集

### 变更

* Hybrid 的默认客户端/服务器 Bootstrap 不再创建 Initialization、Simulation 和 Presentation Tick System，Client World 与 Server World 改由 PlayerLoop 更新

### 修复

* 修复启用延迟补偿时 `PhysicsWorldHistory` 中的异常
* 修复 Source Generator 在 Package Cache 中发现无效数据时的罕见编译错误
* 修复系统无法显示在 System Hierarchy 窗口中的问题
* 修复一次发送过多 RPC 时，RPC 在罕见情况下可能丢失的问题
* 修复预生成或预测生成 Ghost 仅序列化部分字段时错误抛出溢出异常的问题


## [0.50.0] - 2021-09-17

### 新增

* 新增 `GhostSpawnSystem.ConvertGhostToInterpolated` 和 `GhostSpawnSystem.ConvertGhostToPredicted`，用于切换 Ghost 的预测模式；方法包含可选过渡时间参数，非零时会让视觉 Transform 从旧状态平滑过渡到新状态
* 客户端加载 Ghost Prefab 期间可将 `GhostCollectionPrefab.Loading` 设为 `GhostCollectionPrefab.LoadingState.LoadingActive`，按需加载 Prefab
* 客户端和服务器均处于 In Game 状态时，支持在运行时动态加载包含预生成 Ghost 的新 SubScene
* 客户端现可只加载服务器场景的子集，并按需加载或卸载；创建带 `DisableAutomaticPrespawnSectionReporting` 组件的单例可禁用内置 SubScene 同步并实现自定义逻辑，适合更复杂的流式加载场景或特殊需求
* 支持 `FirstSendImportanceMultiplier`，可人为提高客户端新发现 Ghost 的重要度；即使 `MinSendImportance` 很高，也能快速向客户端发送全部 Ghost
* 新增 `DriverMigrationSystem`，允许迁移 `NetworkDriver` 和相关 Connection Entity；可运行示例参阅 `WorldMigrationTests`
* Netcode Bootstrap 现可处理 `ISystemBase` 系统
* 除非手动断开，`NetDbg` 会在获得焦点或首次打开时自动连接
* 若 `ICommandData` 位于预测 Ghost 上、该 Ghost 归当前连接所有，且 Authoring Component 启用了 `SupportAutoCommandTarget`，则无需设置 `CommandTargetComponent` 即可发送 Command；`SupportAutoCommandTarget` 会添加 `AutoCommandTarget` 组件，服务器可将其 `Enabled` 设为 false 来阻止发送；`AutoCommandTarget` 可为多个实体发送 Command，且 `AutoCommandTarget` 和 `CommandTargetComponent` 都支持同一实体上存在多个 `ICommandData`
* 新增 `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier`，可用更长 Delta Time 执行 Server Tick，以替代或配合同一帧执行更多 Tick
* 新增 `ClientServerTickRate.SendSnapshotsForCatchUpTicks`，决定服务器需要在一帧执行多个 Tick 时，是为全部 Tick 发送 Snapshot，还是只为最后一个 Tick 发送；默认只发送最后一个

### 变更

* 将 `GhostFieldAttribute.MaxSmoothingDistance` 从 `int` 改为 `float`
* 将 `ConnectionAcceptJob.debugPrefix` 从 `FixedString32` 改为 `FixedString128`，以容纳更长的 World 名称
* Despawn 流程现可处理大量 Ghost 同时 Despawn，并降低所需带宽；存在丢包时可能增加 Despawn 延迟
* `UpdateInWorld` 重命名为 `UpdateInWorldAttribute`
* `UpdateInWorld.TargetWorld` 枚举移到 `Unity.NetCode` 命名空间
* 客户端现可进入或退出 In Game 状态，无需断开服务器连接
* 服务器现可停止向所有客户端流式发送 Ghost，即退出游戏，加载新 Scene 或 SubScene 后再重新开始发送
* `GhostPredictionDebugSystem` 仅在 `NetDbg` 已连接时运行，并行处理更多错误以提高性能
* 为提升 DOTS Runtime 可移植性，改用 Stopwatch 而非 TimeSpan
* 改进应用 Ghost 状态时的 Tick 处理，避免因没有可回滚状态而报错
* 服务器现负责为所有预生成 Ghost 分配唯一 ID
* 生成的 Serializer 中所有类型现使用完全限定名
* 调试日志改用 `com.unity.logging` 实现
* 服务器端增加验证，确认已设置的 Command Target Entity 包含 `ICommandData` Buffer
* 修复设置了非空 Entity Target 但不存在 Command Data Buffer 时，服务器不更新 Command Age 的问题；该错误会让客户端不断增加预测循环次数并降低帧率
* 预生成 Ghost 实体在转换期间会被禁用，运行时完成 Baseline 初始化后再启用；这可阻止用户代码在实体就绪前修改组件，从而避免预生成 Ghost Hash 验证失败
* `ICommandData` 或 `IRpcCommand` 字段，以及 `IComponentData` 或 `IBufferElement` 的复制字段，以保留前缀 `__GHOST` 或 `__COMMAND` 开头时会报告错误
* 用主动启用的 `LagCompensationConfig` 替代主动禁用的 `DisableLagCompensation`
* 移除先前已废弃的 `GhostCollectionAuthoringComponent`
* 取消废弃 `ConvertToClientServerEntity`；旧 Source Generator 不支持 Ghost 运行时转换，因此曾将其废弃，新 Source Generator 已解决此问题；涉及 Ghost 的内容仍建议使用 SubScene
* `NetworkStreamCloseSystem` 移到 `NetworkReceiveSystemGroup`
* Network Connection Entity 现包含 `LinkedEntityGroup`，在简单情况下更容易于断开连接时删除 Ghost
* `GhostAuthoringComponent` 新增复选框，无需其他 Authoring Component 即可向 Ghost 添加 `GhostOwnerComponent`
* `SceneLoadingTests` 不再仅限 Editor
* 修复用于 IL2CPP 测试的 WebSocket `DebugWebSocket` 代码

### 修复

* 修复 `GhostAuthoringEditor` 未显示分配给组件的正确默认 Variant 的问题
* 修复未释放 `GhostVariantAssignmentCollection` Blob Data 所导致的内存泄漏
* 修复 Ghost Variant Cache 只收集 public 结构体上的 `GhostComponentVariation` 特性的问题
* 服务器不再把过期输入存入 Input Buffer，使当前输入状态与上一帧状态的比较更可靠
* 报告的处理时间大于计算出的 Delta Time 时，避免 RTT 计算溢出
* 修复子实体的 Hash 计算
* 将 `RpcCommandRequest.SendRpcData` 的可访问性从 protected 改为 public，修复为泛型 RPC 类型注册泛型 Job 时抛出的一致性错误
* 修复错误的统计数据包大小所导致的随机崩溃
* 修复仅客户端模式下 `GhostStatsSystem` 尝试访问不存在的 `NetworkAckComponent` 单例的问题
* 修复 `GhostSnapshotValueULong` 拼写错误导致包含 Unsigned Long 字段的 RPC 编译失败的问题
* 修复 `LogAssert.ignoreFailingMessages` 未重置为 true，导致部分失败测试未报告的问题
* 保护 `IrrelevantImportanceDownScale` 不低于 1
* `SnapshotDataBuffer` 和 `SnapshotDynamicDataBuffer` 现使用 `[InternalBufferCapacity(0)]`，减少 Chunk 内实体大小
* 修复生成的 Serializer 类转换类型时未加正确命名空间所导致的编译错误
* 修复命名空间、类型名或字段名以双下划线开头时，Ghost 生成以 `GhostCodeGen failed for fragment` 失败的问题；存在 `__GHOSTXXX__` 或 `__COMMANDXXX__` 关键字时现会报告明确错误
* 改善创建无效 Ghost Authoring 时的用户体验
* 修复组件同时实现多个接口时没有报告错误，并为错误接口生成代码的问题
* Standalone Player 中的 `PacketLogger` 输出文件现保存到 `Application.consoleLogPath`，不再保存到当前文件夹，避免部分平台和环境报错
* 若程序集不包含需要代码生成 Serializer 的类型，则缺少程序集引用时不再报告编译错误
* Override Prefab 中的嵌套组件时，现会分配正确 GameObject 引用

### 升级指南

* `TargetWorld` 枚举现属于 `Unity.NetCode` 命名空间；查找所有 `UpdateInWorld.TargetWorld` 并替换为 `TargetWorld`，枚举值保持不变
* `DisableLagCompensation` 已不存在；未使用延迟补偿时可直接移除，正在使用时必须添加 `LagCompensationConfig` 才能继续运行
* `GhostCollectionAuthoringComponent` 已移除，替代方案参阅上一个升级指南和入门文档



## [0.8.0] - 2021-03-23
### 新增
* 新增基于 Roslyn Source Generator 的代码生成系统
* 为 Ghost 增加预序列化支持，可降低每帧向多个连接发送复杂 Ghost 时的序列化 CPU 开销
* 除带宽外，新增按 CPU 时间控制服务器序列化数据量的参数：`MinSendImportance`、`MinDistanceScaledSendImportance`、`MaxSendChunks` 和 `MaxSendEntities`
* 为预生成 Ghost 新增默认 Baseline
* 新客户端连接服务器时，为预生成 Ghost 增加带宽和 CPU 优化；只发送相对默认 Baseline 已变化的预生成 Ghost
  启用 Static 优化后，预生成 Ghost 未变化时不发送任何数据
* 新增运行时客户端/服务器验证，确认预生成 Ghost Baseline 与 SubScene 在客户端和服务器上数据一致

### 变更
* NetCode 创建的 Entity 现具有合适名称
* 移除 `IGhostDefaultOverridesModifier`
  * 修改组件或 Buffer 序列化时必须改用 `GhostComponentVariation`
  * 添加自定义模板时应实现分部类 `UserDefinedTemplates`
* NetCode 生成的类不再出现在项目中
* 移除 NetCode 代码生成窗口
* `GhostSendSystem.KeepSnapshotHistoryOnStructuralChange` 设为 true，即默认值时，部分结构变化现可保留 Snapshot History
* 预生成 Ghost 与正常生成 Ghost 的 GhostId 现分属两个不相交集合；预生成 Ghost ID 的第 31 位为 1，因此其值为负整数

### 修复
* 修复 `ICommandData` 结构体中使用 Entity 时的错误代码生成
* 确保 `CreatePredictedSpawnPrefab` 不实例化子实体
* 修复关闭时未发送断开消息的问题
* 修复实体发生结构变化时可能使用无效 Baseline 的极罕见问题
* 若物理在 Ghost 预测循环中运行且 `PhysicsMassOverride.IsKinematic` 为 1，则不再修改预测 Ghost 的 Translation 和 Rotation
* 从未发送给客户端的实体变为不相关时，不再需要 Despawn 消息
* 修复 Dynamic Buffer Change Mask 未正确清理，导致 Buffer 始终报告为已变化的问题
* 修复客户端不处于 In Game 状态时未重置 `latestSnapshotEstimate` 的问题
* 修复启用 Static 优化的预测 Ghost 未更新 `PredictionHistoryBuffer` 的问题
* 修复最后一个客户端退出游戏时 `GhostSendSystem` 未正确清理的问题

### 升级指南
* 升级前后必须移除 `Assets/NetCodeGenerated` 文件夹；若在升级后才移除，期间可能出现编译错误

若项目曾通过 Modifier 或模板自定义代码生成，还需执行以下步骤

#### 项目使用自定义模板时
在项目中创建新文件夹，并添加指向 NetCode 的程序集引用，例如：
```text
+ CodeGenCustomization/
   + NetCodeRef/
       NetCode.asmref
   + Templates/
       Templates.asmdef (has NETCODE_CODEGEN_TEMPLATES define constraints)
```
将模板和 Subtype 定义放在这里，具体步骤如下，更多信息参阅更新后的文档

##### 重新实现模板注册
在包含 `netcode.asmref` 的文件夹中创建新文件，并添加 `UserDefinedTemplates` 分部类，示例中为 `NetCodeRef`
然后实现 `static partial void RegisterTemplates(...)` 方法，并在其中注册模板

```csharp
using System.Collections.Generic;
namespace Unity.NetCode.Generators
{
    public static partial class UserDefinedTemplates
    {
        static partial void RegisterTemplates(List<TypeRegistryEntry> templates, string defaultRootPath)
        {
            templates.AddRange(new[]{

                new TypeRegistryEntry
                {
                    Type = "Unity.Mathematics.float3",
                    SubType = Unity.NetCode.GhostFieldSubType.Translation2d,
                    Quantized = true,
                    Smoothing = SmoothingAction.InterpolateAndExtrapolate,
                    SupportCommand = false,
                    Composite = false,
                    Template = "Assets/Samples/NetCodeGen/Templates/Translation2d.cs",
                    TemplateOverride = "",
                },
            }
        }
    }
}
```
##### 定义新的 Subtype
若模板使用 Subtype，如上方示例所示，需要在 NetCode 程序集引用文件夹内为 __Unity.NetCode.GhostFieldSubType__ 类型添加分部类
例如：
```c#
namespace Unity.NetCode
{
    static public partial class GhostFieldSubType
    {
        public const int MySubType = 1;
    }
}
```
新 Subtype 随后会在项目中所有引用 `Unity.NetCode` 程序集的位置可用

#### 如何重新实现 GhostComponentModifier
`ComponentModifier` 已移除，应改用 __GhostComponentVariation__ 特性创建 Ghost 组件 Variant
<br>
1. 在能够访问目标类型的程序集中创建新文件来存放 Variant，然后为此前的每个 Modifier 创建对应 Variant 实现，如下例所示

```csharp
  // 旧 Modifier
  new GhostComponentModifier
  {
      typeFullName = "Unity.Transforms.Translation",
      attribute = new GhostComponentAttribute{PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.All, SendDataForChildEntity = false},
      fields = new[]
      {
          new GhostFieldModifier
          {
              name = "Value",
              attribute = new GhostFieldAttribute{Quantization = 100, Smoothing=SmoothingAction.InterpolateAndExtrapolate}
          }
      },
      entityIndex = 0
  };

// 新 Variant
[GhostComponentVariation(typeof(Translation))]
public struct MyTranslationVariant
{
  [GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)] public float3 Value;
}
```

2. 随后必须将这些 Variant 声明为__对应组件使用的默认值__
   需要继承 `DefaultVariantSystemBase` 创建具体系统，并实现 `RegisterDefaultVariants` 方法
```csharp
class MyDefaultVariantSystem : DefaultVariantSystemBase
{
    protected override void RegisterDefaultVariants(Dictionary<ComponentType, System.Type> defaultVariants)
    {
        defaultVariants.Add(new ComponentType(typeof(Translation)), typeof(MyTranslationVariant));
        ...
    }
}
```
该系统的存放位置没有特殊限制，更多信息参阅更新后的文档


## [0.7.0] - 2021-02-05
### 新增
* 新增网络日志功能，通常用于取得更详细的 Netcode Debug 信息，即 `Debug` 级别，或为每个连接启用 Ghost Snapshot 与 Packet 日志；PlayMode Tools 窗口和 `NetCodeDebugConfigAuthoring` 组件提供简单开关，也可通过 `NetCodeDebugSystem.LogLevel` 或为 Connection Entity 添加 `EnablePacketLogging` 组件在代码中修改
* 支持在 Ghost 预测循环中运行物理；创建带 `PredictedPhysicsConfig` 组件的单例即可启用，详情参阅手册的物理章节

### 变更
* Disconnect Reason 现会从 Transport 正确传递给 Netcode，且 `NetworkStreamDisconnectReason` 枚举与 `Unity.Networking.Transport.DisconnectReason` 一致

### 废弃
* 废弃 `GhostCollectionAuthoringComponent`；请创建引用待生成 Prefab 的组件，并确保该组件存在于场景实体上；无需再把预生成 Ghost 存入 Collection

### 修复
* 修复客户端 Interpolation Time 抖动且偶尔倒退的问题
* 修复 Network Condition Simulator 的丢包模拟工作不稳定的问题
* 修复 IL2CPP Strip 导致运行时错误的问题
* 修复 Release 构建中的分片 Snapshot 问题

### 升级指南
* 用户自定义 Bootstrap，即继承 `ClientServerBootstrap` 的类，必须添加 `[Preserve]` 特性

## [0.6.0] - 2020-11-26
### 新增
* Ghost 现支持 Dynamic Buffer 序列化；与 `IComponentData` 类似，可用 `GhostComponentAttribute` 标记 `IBufferElementData`，用 `GhostFieldAttribute` 标记成员，通过网络复制 Buffer
* `ICommandData` 现可序列化并发送给远端玩家
* 为 `GhostComponentAttribute` 新增 `SendToOwner` 属性，可配置组件发送给哪个玩家子集：仅 Owner、仅非 Owner，或全部玩家
* 新增 Ghost 组件序列化 Variant；`GhostComponentVariation` 特性可通过覆盖原类型定义中的 `[GhostField]` 和 `[GhostComponent]` 属性，为组件或 Buffer 指定不同序列化选项
* 可使用 `[DontSupportVariation]` 特性阻止组件支持 Variant；添加后若为该类型定义 `GhostComponentVariation`，会触发异常
* Ghost 组件特性和序列化 Variant 可按 Prefab 自定义；Ghost Prefab 中每个组件均可修改：
    * PrefabType
    * GhostSendType
    * 该组件存在 Variant 时使用哪个 Variant
* 可使用 `[DontSupportPrefabOverride]` 特性阻止组件支持按 Prefab Override；添加后无法再在 Inspector 中自定义该组件
* 现可通过调用 `GhostPredictionSmoothingSystem.RegisterSmoothingAction<ComponentType>(SmoothingActionDelegate)` 并提供 `ComponentType` 和 `GhostPredictionSmoothingSystem.SmoothingActionDelegate` 来注册预测平滑函数，示例参阅 `Runtime/Snapshot/DefaultUserParams.cs`
* 新增 `ClientTickRate` 组件；添加到 Singleton Entity 后可控制客户端时间计算所用的 Interpolation Time；默认值可通过静态 `NetworkTimeSystem.DefaultClientTickRate` 访问
* 当客户端要应用于插值 Ghost 的 Tick 尚未收到且已超出插值延迟时，支持进行外推；将 `GhostField` 特性的新 `Smoothing` 字段设为 `SmoothingAction.InterpolateAndExtrapolate` 即可启用
* 为 `[GhostField]` 特性新增 `MaxSmoothingDistance` 参数；指定后，若两份 Snapshot 间数值变化超过该上限，则禁用插值，适合处理传送等不应插值的变化

### 变更
* 不再要求创建 Ghost Collection；只要 Ghost 存在 Prefab 就会自动收集，可通过在 Spawner 组件中引用 Prefab，或放置预生成 Ghost 实例来创建 Prefab

### 修复
* 修复 Elapsed Time 未使用最大 Simulation Rate，导致固定时间步物理耗时不断增加的问题
* 修复性能过低时在 Editor 中同时运行客户端和服务器会发生时间回滚的问题

### 升级指南
`GhostField` 特性的 `Interpolate` bool 已由 `Smoothing` 替代；将 `Interpolate=true` 替换为 `Smoothing=SmoothingAction.Interpolate` 可保持原行为，设为 `SmoothingAction.InterpolateAndExtrapolate` 可启用外推

## [0.5.0] - 2020-10-01
### 新增
* 新增 `RpcSystem.DynamicAssemblyList`；客户端和服务器的程序集集合不同时，可延迟计算 RPC 和 Ghost 组件 Checksum
* RPC 和 Command 现支持客户端与服务器双向发送 Entity 引用

### 变更
* 调整系统顺序以兼容最新版 Physics；`NetworkTimeSystem` 移到 `ClientInitializationSystemGroup`；`SimulationSystemGroup` 会在运行物理的 `FixedStepSimulationSystemGroup` 前执行客户端 `GhostSpawnSystemGroup`、`GhostReceiveSystemGroup` 和 `GhostSimulationSystemGroup`；`RpcCommandRequestSystemGroup`、`RpcSystem` 和服务器 `GhostSendSystem` 在所有模拟代码之后的帧末执行；其他系统也已移入相应组
* 新增 `GhostInputSystemGroup`，向 Input Buffer 添加输入的系统应在其中运行

### 修复
### 升级指南
* 向 `ICommandData` Buffer 添加输入的系统需要移到 `GhostInputSystemGroup`

## [0.4.0] - 2020-09-10
### 新增
* 代码生成现支持 `ICommandData`，Command Data 序列化可由生成器生成而无需手写；添加 `[NetCodeDisableCommandCodeGen]` 可退出代码生成
* `NetCodeConversionSettings` 新增 Client And Server 模式，可构建同时支持客户端和服务器的单个 Standalone Build
* 新增生成 Prefab 预测生成版本的静态方法 `GhostCollectionSystem.CreatePredictedSpawnPrefab`

### 变更
* RPC 或 Command 不使用代码生成时，用于注册它们的系统，即继承 `RpcCommandRequestSystem<TActionSerializer, TActionRequest>`、`CommandSendSystem<TCommandDataSerializer, TCommandData>` 和 `CommandReceiveSystem<TCommandDataSerializer, TCommandData>` 的系统，需要增加用于设置 Job 的代码
* `ICommandData` 接口不再接收额外泛型类型
* 新增 `CommandSendSystemGroup` 和 `CommandReceiveSystemGroup`，生成 `ICommandData` 代码时可用于依赖排序
* Authoring 所用 GameObject 移到独立程序集
* 不再支持客户端固定 Tick Rate，同时移除渲染插值
* Editor 中不再支持多个渲染客户端，但仍支持 Thin Client
* `GhostPrefabCollectionComponent` 现只包含一个 Prefab List，其 `GhostPrefabBuffer` 挂在同一实体上

### 废弃
* 废弃 `ConvertToClientServerEntity`，请改用 SubScene Conversion 工作流

### 修复
* 修复包含多个被 Ghost 化 Entity 引用的组件所生成代码中的编译错误
* 修复预测生成错误时未销毁预测生成 Ghost 的问题
* 修复预测 Ghost 的子实体数据可能损坏的问题

### 升级指南
* 客户端现只有一个 Prefab，使用前需要进行修补，因此预测生成代码必须改用新的 `GhostCollectionSystem.CreatePredictedSpawnPrefab` Utility 方法
* 在服务器上通过 `GhostPrefabCollectionComponent` 查找 Ghost Prefab 时，必须改为从同一实体读取 `GhostPrefabBuffer`
* 客户端使用固定 Tick Rate 模式时，需要移除 `FixedClientTickRate` 单例的创建；若使用了 `CurrentSimulatedPosition` 和 `CurrentSimulatedRotation`，也应移除
* PlayMode Tools 中使用 Num Clients 时，需要改用 Num Thin Clients
* 不使用代码生成的 RPC 需要向 `RpcCommandRequestSystem` 添加更多代码，新实现应如下所示：
```c#
class MyRequestRpcCommandRequestSystem : RpcCommandRequestSystem<MyRequestSerializer, MyRequest>
{
    [BurstCompile]
    protected struct SendRpc : IJobEntityBatch
    {
        public SendRpcData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var sendJob = new SendRpc{data = InitJobData()};
        ScheduleJobData(sendJob);
    }
}
```
* `ICommandData` 的 `Tick` 属性现在同时需要 Getter 和 Setter
* 使用代码生成时，`ICommandData` 结构体不再需要序列化代码，也无需实现 `CommandSendSystem` 和 `CommandReceiveSystem`；接口已从 `ICommandData<T>` 改为 `ICommandData`
* 手写 `ICommandData` 序列化代码时，需要将其移到实现 `ICommandDataSerialize<T>` 的结构体中，并在 `CommandSendSystem` 和 `CommandReceiveSystem` 实现中添加如下 Job 调度代码：
```c#
public class MyCommandSendCommandSystem : CommandSendSystem<MyCommandSerializer, MyCommand>
{
    [BurstCompile]
    struct SendJob : IJobEntityBatch
    {
        public SendJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var sendJob = new SendJob{data = InitJobData()};
        ScheduleJobData(sendJob);
    }
}
public class MyCommandReceiveCommandSystem : CommandReceiveSystem<MyCommandSerializer, MyCommand>
{
    [BurstCompile]
    struct ReceiveJob : IJobEntityBatch
    {
        public ReceiveJobData data;
        public void Execute(ArchetypeChunk chunk, int orderIndex)
        {
            data.Execute(chunk, orderIndex);
        }
    }
    protected override void OnUpdate()
    {
        var recvJob = new ReceiveJob{data = InitJobData()};
        ScheduleJobData(recvJob);
    }
}
```

## [0.3.0-preview.3] - 2020-08-21
### 新增
* 新增 Ghost 序列化代码生成工作流；新流程按组件生成代码，不再按 Ghost 类型 Prefab 生成
  * Ghost Field 和 Ghost Component 现通过代码中的 `GhostField` 与 `GhostComponent` 特性配置，可设置预测、插值、量化等参数
  * Ghost Component 和 Collection Inspector 现在只显示 Ghost 在代码中的配置结果
  * Ghost 现可按需生成，无需再显式点击按钮
  * 新增 Ghost Compiler 窗口，可调整生成方式，例如从按需生成改为手动生成，并显示是否有 Ghost 不同步
* 代码生成现支持 RPC；实现 `IRpcCommand` 接口并像普通 `IComponentData` 一样编写 RPC，即可自动生成序列化代码，无需手写
* 支持 Ghost Group；可在 Authoring 时向 Ghost Prefab 添加 `GhostGroup` Buffer，其中列出的全部 Ghost 保证与主实体一同发送；子 Ghost 必须添加 `GhostChildEntityComponent` 才能加入组，也可在运行时把子实体移入组时添加该组件
* 新增相关性支持；服务器将 `GhostSendSystem.GhostRelevancyMode` 改为 `GhostRelevancyMode.SetIsRelevant` 或 `GhostRelevancyMode.SetIsIrrelevant`，并向 `GhostSendSystem.GhostRelevancySet` 添加 Ghost，即可限制发送给特定客户端的 Ghost 集合
* 为 Ghost 新增优化模式；新的 Static 优化采用侵入性更低的 Delta Compression，使 Chunk 中没有实体变化时可完全停止发送数据
* `NetDbg` 新增 Prediction Error 可视化
* 服务器 Connection Entity 可添加 `NetworkStreamSnapshotTargetSize`，用于控制 Snapshot 目标大小
* 新增 `GhostReceiveSystem.GhostCountOnServer` 和 `GhostReceiveSystem.GhostCountOnClient`，用于检查客户端应有多少 Ghost 以及实际有多少

### 变更
* 用 `FixedString64` 支持替代 `NativeString64`，并新增对 `FixedString32`、`FixedString128`、`FixedString512` 和 `FixedString4096` 的支持
* Dynamic Timestep 模式下没有收到新数据时，现可从上一个完整 Prediction Tick 恢复预测，而无需回滚到最近收到的 Snapshot
* 新增 `DisableLagCompensationComponent`，作为单例添加后可阻止延迟补偿系统运行

### 修复
* Quaternion 反量化后会重新归一化，确保仍为有效旋转
* Float 量化后会舍入到最近整数，提高精度
* 现可在每帧发送多个包含 RPC Command 的数据包；此前这样做会静默丢弃 Command

### 升级指南
* 不再支持 `NativeString64`，请改用 `FixedString64`
* `GhostUpdateSystemGroup` 已不存在，用于更新顺序的引用应替换为 `GhostUpdateSystem`
* NetCode 现在要求 Unity 2020.1.2

#### 新 Ghost 工作流
* 将组件中的所有 `[GhostDefaultField]` 改为 `[GhostField]`，所有 `[GhostDefaultComponent]` 改为 `[GhostComponent]`；构造函数参数也已变化，应使用 `[GhostField(Quantization=100, Interpolate=true)]` 而非 `[GhostDefaultField(100, true)]`
* 手动添加字段的所有 Ghost 都必须为组件添加 `GhostField` 特性，因为不再支持手动 Override
* 从 Server、Interpolated Client 或 Predicted Client 移除组件的所有 Ghost，都必须为组件添加 `[GhostComponent(PrefabType=<type>)]`，其中 `<type>` 与此前设置一致
* 不希望位于 Ghost 子实体上时同步的所有组件，都需要添加 `[GhostComponent(SendDataForChildEntity = false)]`
* 打开所有 Prefab，确认 `Name`、`Importance` 和 `Default ghost mode` 仍正确；`Supported Ghost Mode` 与 `Optimization Mode` 是新字段，其默认值与旧工作流行为一致
* 所有使用 Owner Predicted 模式的 Ghost 都必须添加 `GhostOwnerComponent`，并确保代码正确设置该组件的 `NetworkId`；此前可将 Network ID 存在任意组件中，再由 `GhostAuthoringComponent` 指向它
* Owner Predicted Ghost 上仅发送给插值或预测 Ghost 的所有组件，都需要添加 `[GhostComponent(OwnerPredictedSendType = <type>)]`，其中 `<type>` 为 `GhostSendType.Interpolated` 或 `GhostSendType.Predicted`
* 删除旧 NetCode 版本生成的代码
* 使用预测生成时，新 Request 方式是实例化 Ghost Prefab 的 Predicted Client 版本，并向实体添加 `PredictedGhostSpawnRequestComponent`
* 此前在 `MarkPredictedGhosts` 中实现的所有自定义生成行为，包括匹配预生成 Ghost 实体，都必须移到生成分类系统
* 此前在 `UpdateNewPredictedEntities` 或 `UpdateNewInterpolatedEntities` 中实现的所有修改已生成 Ghost 的自定义代码，都必须移到 `GhostSpawnSystemGroup` 中且在 `GhostSpawnSystem` 后运行的系统；使用 Tag Component 检测新 Ghost

#### RPC
* 若 `IRpcCommand` 组件的 Execute 方法只使用 `RpcExecutor.ExecuteCreateRequestComponent`，可移除 `Serialize`、`Deserialize`、`CompileExecute`、Execute 方法及其 Burst 函数指针实现，还需移除该组件的 `CommandRequestSystem` 实现；这些内容都会由代码生成
* 仍需手动序列化或执行的所有 RPC 实现，应从 `public struct MyRequest : IRpcCommand` 改为实现 `public struct MyRequest : IComponentData, IRpcCommandSerializer<MyRequest>`
* RPC 序列化签名改为 `void Serialize(ref DataStreamWriter writer, in MyRequest data)`，反序列化签名改为 `void Deserialize(ref DataStreamReader reader, ref MyRequest data)`
* 手动序列化或执行 RPC 的 `CommandRequestSystem` 应从 `class MyRequestCommandRequestSystem : RpcCommandRequestSystem<MyRequest>` 改为 `class MyRequestCommandRequestSystem : RpcCommandRequestSystem<MyRequest, MyRequest>`

## [0.2.0-preview.5] - 2020-06-05
### 新增
* 支持预生成 Ghost；把 Ghost Prefab 实例放入 SubScene 后，服务器和客户端加载场景时都会创建它们，随后自动互相关联，并像正常生成的 Ghost 一样工作

### 变更
* 修改 Snapshot 大小限制方式，使其更稳健并提供更清晰的错误
* 为 `GhostAuthoringComponent` 新增 `Name` 字段，代码生成期间用于识别 Ghost Prefab；默认使用 Prefab 名称，也可修改
* `ClientServerBootstrap` 现正确使用两阶段初始化来初始化全部系统
* `PhysicsWorldHistory.CollisionHistoryBuffer` 改为返回 `CollisionHistoryBuffer` 的安全内存引用，不再在栈上复制大量数据
* 升级至 Entities 0.11

### 修复
* 修复 Ghost Prefab 为 Variant Prefab 或 Model Prefab 时的问题
* 修复检测到 Snapshot 不同步时 DataStream 随之失步的问题
* 修复尝试通过无效指针注册格式错误 RPC 时 `RegisterRPC` 的问题
* 修复高延迟下 `ServerTick` 不单调递增的问题
* 修复客户端连接并断开服务器时重复创建 `ClientServerTickRate` 的问题
* 修复 World 中已存在 `ClientServerTickRate` 时客户端不复用它的问题
* 修复生成 Client World 和 Server World 的系统列表时 `TypeManager` 尚未初始化所导致的 `ClientServerBootstrap` 问题

### 升级指南

* `GhostAuthoringComponent` 新增 `Name` 字段，因此所有包含该组件的 Prefab 都需要打开再关闭以序列化该字段；代码生成时会将其作为名称前缀，因此可能还需要再次点击 _Generate Code_ 按钮

## [0.1.0-preview.6] - 2020-02-24
### 新增
* 新增 UnityPhysics 集成，包括来自 DotsSample 的延迟补偿；使用前必须在项目中添加 UnityPhysics

### 变更
* Unity Transport 升级至 0.3.0，并需要部分 API 调整，参阅升级指南
* 所有 `FunctionPointer` 实例都缓存在静态字段中，减少编译调用次数
* Helper 方法 `RpcExecutor.ExecuteCreateRequestComponent` 现返回其创建的实体
* 为 `NetworkStreamReceiveSystem` 新增创建 Driver 时使用的接口；可在 Bootstrap 期间将 `NetworkStreamReceiveSystem.s_DriverConstructor` 设为自定义实例，以自定义方式创建 Driver
* 移除已废弃一段时间且会导致运行时 Conversion 问题的 `World.Active` 变通方案
* 确保所有可使用 Burst 编译的 Job 都启用 Burst，从而小幅提升性能
* Ghost 类型现根据 Ghost Prefab Asset 的 GUID 而非 Archetype 选择，因此多个不同 Ghost 可以拥有相同 Archetype；Ghost 不是有效 Prefab 时会在 Conversion 期间报错

### 修复
* 修复由 GameObject 实例创建的 Ghost Prefab 被所有系统处理的问题
* 代码生成现仅在文件内容变化时写入文件
* 释放 Client World 或 Server World 时会从 Tick System 取消注册，避免错误
* 计算 Time Scale 时计入 Command Age 更新延迟，使高延迟下输入更稳定

### 升级指南
Unity Transport 已升级至 0.3.0，`DataStreamReader` 和 `DataStreamWriter` API 随之变化

`IRpcCommand` 和 `ICommandData` 已改为不接收 `DataStreamReader.Context`

`ISnapshotData` 和 GhostCollection 接口已改为不接收 `DataStreamReader.Context`，必须重新生成全部 Ghost 和 Collection

`GhostDistanceImportance.NoScale` 和 `GhostDistanceImportance.DefaultScale` 已由 `GhostDistanceImportance.NoScaleFunctionPointer` 与 `GhostDistanceImportance.DefaultScaleFunctionPointer` 替代，后两者为已编译函数指针而非方法

## [0.0.4-preview.0] - 2019-12-12
### 新增
### 变更
* 修改 `NativeString64` 的代码生成，改用 DataStream 中的序列化

### 修复
### 升级指南

## [0.0.3-preview.2] - 2019-12-05
### 新增
### 变更
* 更新文档并新增预测章节
* 将 Entities 升级至 0.3.0

### 修复
* 修复多个客户端在同一帧断开时的崩溃
* 修复 `AfterSimulationInterpolationSystem` 中的读写访问说明符
* 修复非 Development Standalone Build 的构建错误

### 升级指南

## [0.0.2-preview.1] - 2019-11-28
### 新增
### 变更
### 修复
* 修复 String 生成序列化代码中的编译错误
* 修复禁用 Netcode 时进入 Play Mode 出现的警告

### 升级指南

## [0.0.1-preview.6] - 2019-11-26
### 新增
* 支持按距离缩放重要度，从而容纳更多 Ghost
* 支持包含复制数据的嵌套实体
* Entity 引用现可作为 Ghost Field；这些引用为弱引用，无法保证目标存在时会解析为 `Entity.Null`
* `NativeString64`、枚举和 bool 可作为 Ghost Field
* 新增 `ClientServerTickRate`，可配置与时间步进相关的行为；Headless Server 可在达到目标帧率后休眠以节省 CPU
* 可根据实体处于预测还是插值模式发送不同数据，在预测模式下可节省部分带宽
* 新增协议版本，只有版本匹配时连接才能成功
* Network Debugger 新增时间图表和服务器视图
* Network Simulator 现支持抖动

### 变更
* 改进 Authoring 流程
  * Conversion 执行后，`GhostAuthoringComponent` 现会自动检测实体包含的组件；点击 Update component list 按钮时自动填充，不再需要手动输入每个组件名
  * 可为特定组件类型定义默认值，例如 Translation 组件通常需要同步 Value 字段；定义默认处理后，Ghost Authoring Component 解析 Entity Component List 时会使用该配置
  * 新增 `[GhostDefaultField]` 特性，可添加到需要同步的 Ghost 变量上，`GhostAuthoringComponent` 会检测这些字段
  * 新增 `[GhostDefaultComponent]` 特性，可定义组件在 InterpolatedClient、PredictedClient 和 Server 上的默认同步行为
  * 新增 `GhostCollectionAuthoringComponent`，用于注册所有生成 Prefab
  * 简化路径配置，可设置生成文件的根目录，并在代码中指定默认值
  * Inspector 中会用粗体标记会复制变量数据的组件，更容易判断每个 Ghost 的发送数据量
* 改进 Snapshot 预测处理
  * 现使用服务器 Delta Time 而非客户端 Delta Time
  * 支持 Dynamic Timestep 和 Fractional Tick Prediction
  * 可处理卡顿，回滚重放不会追溯过远，最多 64 帧
  * 配置预测实体所需样板代码更少，更多默认处理移入代码生成
  * 新增 `GhostPredictionSystemGroup`，更准确地计算客户端当前 Prediction Tick
  * Interpolation Time 作为 Prediction Time 的偏移，确保二者不会漂移
* 一次发送多个输入，降低输入丢失对错误预测的影响
* 新增 Thin Client；相比完整客户端模拟占用更少资源，更容易进行多客户端测试
* 新增 RPC Heartbeat System；仅在客户端没有向服务器发送任何内容时运行，防止断开超时；输入开始发送且 Snapshot 开始同步后即停止运行
* 减少 RPC 样板代码；将继承 `IRpcCommandRequestComponentData` 的组件与 `SendRpcCommandRequestComponent` 一同添加到实体后会自动发送
* 简化 Client World 和 Server World 的 Bootstrap；现可更方便地使用自定义 Bootstrap，并在运行时按需创建客户端或服务器 World；Editor 中默认由 PlayMode Tools 控制 World 创建
* 新增 `NetCodeConversionSettings`，可在 SubScene Build Settings 工作流中指定构建类型，即 Client 或 Server
* 检测 Ack Mask 不同步
* 改进 Ghost 代码生成，使存在编译错误时仍可重新生成代码
* 没有 `CommandSendSystem` 时现在也会确认 Snapshot
* 改用 Entities `TimeData` 结构，不再从 `ClientSimulationSystemGroup` 或 `ServerSimulationSystemGroup` 获取时间

### 修复
* Ghost Authoring Component 的代码生成现会为用户命名空间生成 Import
* 代码生成会触发 Asset Database 刷新，使修改后的文件得到编译
* Command Input 现会在开始传输前正确检查是否存在 `NetworkStreamInGame`
* Ack 的到达间隔现可超过 64 Tick
* 生成代码不再要求项目启用 Unsafe Code

### 升级指南
* 现在要求 Unity 2019.3，最低 beta 11，以及 Entities 0.2.0-preview
* `NetCode` 文件夹已移入正式包，现应使用 `com.unity.netcode`
* 所有 Netcode 代码移入 `Unity.NetCode` 命名空间
* 移除 `[NotClientServerSystem]` 特性，改用具有相同行为的 `[UpdateInWorld(UpdateInWorld.TargetWorld.Default)]`
* 移除 `GhostPrefabAuthoringComponent`，改用新的 `GhostCollectionAuthoringComponent` 配置 Ghost 数据
* 移除不再需要的 `ClientServerSubScene`
* 移除 `NetworkTimeSystem.predictTargetTick`，改用 `GhostPredictionSystemGroup.PredictingTick`
* RPC 接口已变化，不再需要生成 Collection

## [0.0.1-preview.2] - 2019-07-17
### 新增
* 新增基于 Prefab 定义 Ghost 的工作流；Prefab 可包含用于生成 Ghost 代码的 `GhostAuthoringComponent`，客户端生成 Ghost 时可使用 `GhostPrefabAuthoringComponent` 实例化 Prefab；该流程替代 `.ghost` 文件，所有项目都需要更新到新的 Ghost 定义
* 新增 `ConvertToClientServerEntity`，可在 Conversion 工作流中替代 `ConvertToEntity`，指定目标为客户端或服务器 World
* 新增 `ClientServerSubScene` 组件，可与 `SubScene` 一起触发客户端和服务器 World 的 SubScene 流式加载

### 变更
* 默认组中的系统默认加入客户端和服务器 World，除非标记 `[NotClientServerSystem]`，使内置系统可用于多人项目
* Development Player 运行时，Standalone Player 现使用与 Editor 相同的 Network Simulator 设置
* Server Build 选项，即 `UNITY_SERVER` Define，现能为 Dedicated Server 正确配置 World；在 Player Settings Define 中设置 `UNITY_CLIENT` 会生成仅客户端构建
* Debugger 现显示所有运行中的服务器和客户端

### 修复
* 更新系统时，将 `World.Active` 改为当前执行的 World
* 改进客户端和服务器之间的时间计算

### 升级指南
`.ghost` 文件中指定的所有 Ghost 定义都需要转换为 Prefab；创建包含 `GhostAuthoringComponent` 以及所有所需组件 Authoring Component 的 Prefab，使用 `GhostAuthoringComponent` 更新组件列表并生成代码

## [0.0.1-preview.1] - 2019-06-05
### 新增
* 为 NetCode 新增预测和生成预测支持系统，可用于实现网络对象的客户端预测
* 初步支持为 NetCode 中的复制对象生成所需代码
* 通用化 NetCode 输入处理
* 为多人 World 新增自定义固定时间步代码

### 变更
* 将 NetCode 拆分为独立程序集并改进文件夹结构，便于在其他项目使用
* 将 Asteroids 示例拆为 Client、Server 和 Mixed 独立程序集，便于构建不包含客户端代码的 Dedicated Server
* 将 Entities 升级至 Preview 33

### 修复
### 升级指南

## [0.0.1-preview.0] - 2019-04-16
### 新增
* 新增 Asteroids 游戏示例，用于开发新的 NetCode

### 变更
* 更新至 Unity.Entities Preview 26

### 修复
### 升级指南
现在要求 Unity 2019.1
