# Netcode for Entities 1.0 新增功能

本文汇总 Netcode for Entities 1.0 版本中的变更

完整变更列表请参阅[更新日志](xref:changelog)。有关如何升级到 1.0 版本的信息，请参阅[升级指南](upgrade-guide.md)

## 新增

* 新增统一的 `NetCodePhysicsConfig`，可在一处配置全部 Netcode 物理设置。转换时会根据这些设置生成 LagCompensationConfig 和 PredictedPhysicsConfig
* 新增 `GhostPrefabCreation.ConvertToGhostPrefab` API，可直接通过代码创建 Ghost 预制体，无需为其准备资源
* 支持创建多个网络驱动。服务器现在可以通过不同的网络接口监听同一端口，例如同时使用 IPC、Socket 和 WebSocket
* 新增 NetworkTime 组件，其中包含客户端/服务器模拟的全部时间与 Tick 信息。有关如何更新项目的详细信息，请参阅升级指南
* 支持序列化 IEnableableComponent。服务器可以选择复制组件的启用状态
* 新增输入接口 IInputCommandData，可将输入数据自动作为网络命令数据处理。系统会根据当前 Tick 自动将输入复制到命令缓冲区，或从中取回输入
* 新增 InputEvent 类型，可在此类输入组件中可靠同步单次事件
* 支持通过设置 `ClientTickRate.MaxPredictionStepBatchSizeRepeatedTick` 和 `ClientTickRate.MaxPredictionStepBatchSizeFirstTimeTick` 批量运行预测循环。输入发生变化时会拆分批次，除非变化的输入数据标记了 `[BatchPredict]`
* 优化同一进程内通过 IPC 连接运行客户端和服务器时的预测 Tick 数量与插值帧数量
* 新增 `ConnectionState` 系统状态组件，可添加到连接上以跟踪状态变化、新连接和断开连接
* 新增 `NetworkStreamRequestConnect` 组件，可将其添加到新实体上创建连接，无需直接调用 `Connect`
* 新增 `NetworkStreamRequestListen` 组件，可将其添加到新实体上使服务器开始监听，无需直接调用 `Listen`
* 为 `World` 和 `WorldUnmanaged` 新增 `IsClient`、`IsServer` 和 `IsThinClient` 辅助方法
* 新增 `Ghost Metrics` API，用于在运行时获取 Ghost 相关统计信息
* 为 DefaultDriverBuilder 新增辅助方法，用于创建并注册 IPC 和 Socket 驱动。服务器在编辑器中同时使用两者，在 Player 构建中仅使用 Socket；客户端与服务器位于同一进程时使用 IPC，否则使用 Socket
* RegisterClientDriver 和 RegisterServerDriver 新增安全连接（DTLS）支持，并可接收证书
* RegisterClientDriver 和 RegisterServerDriver 新增 Relay 服务器数据支持
* 新增默认生成分类系统。当用户系统没有优先处理客户端预测生成时，该系统会负责处理，并匹配生成 Tick 前后 5 个 Tick 内相同 Ghost 类型的生成结果
* 优化 GhostCollectionSystem 导入和处理 Ghost 预制体的过程

## 更新

* 现在也会为具有 internal 可见性的 Component、Buffer、Command 和 RPC 生成序列化代码
* DOTS Hierarchy 现在会将 Ghost 标记出来，粉色表示已复制，蓝色表示预制体。Unity 内置 Hierarchy 也使用类似标记，但受 API 限制，功能较少
* 支持在运行时编辑 ThinClient 数量
* 新增 Unity.Logging 包依赖。Unity.Logging 现在是本包默认使用的日志方案
* 客户端和服务器 World 现在由 Player Loop 更新，不再依赖默认 World 直接更新它们
* 预测 Ghost 物理现在使用多个 Physics World：预测 Physics World 用于模拟 Ghost 物理，客户端专用 Physics World 可用于表现效果。详细信息请参阅预测物理文档
* 预测 Ghost 物理现在使用自定义系统更新物理模拟，内置系统则用于更新仅客户端模拟
* 可序列化组件数量上限 128 现在针对实际使用中的组件，而不是项目内的全部组件
* Netcode 源码生成器模板现在应使用 NetCodeSourceGenerator.additionalfile，并通过唯一 ID 标识。详细信息请参阅[模板](ghost-types-templates.md)文档
* 对 `PlayMode Tools Window` 进行了多项改进，包括代表真实网络速度的模拟器“配置档”、运行时创建和销毁瘦客户端、实时修改模拟器参数，以及通过快捷键模拟延迟突增的工具

## 更多信息

* [升级指南](upgrade-guide.md)
* [更新日志](xref:changelog)
