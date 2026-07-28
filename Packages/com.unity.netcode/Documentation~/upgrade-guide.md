# 从 Entities 0.51 升级到 1.0

Netcode for Entities 引入了大量变更，因此从 0.51 升级到 1.0 的过程可能较为繁琐

<a id="classes-renamed-and-moved-in-other-assemblies"></a>
## 类型重命名及跨程序集迁移

* 以下组件已经重命名，并会自动更新：

| 原名称 | 新名称 |
|--------|--------|
| NetworkSnapshotAckComponent | NetworkSnapshotAck |
| IncomingSnapshotDataStreamBufferComponent | IncomingSnapshotDataStreamBuffer |
| IncomingRpcDataStreamBufferComponent | IncomingRpcDataStreamBuffer |
| OutgoingRpcDataStreamBufferComponent | OutgoingRpcDataStreamBuffer |
| IncomingCommandDataStreamBufferComponent | IncomingCommandDataStreamBuffer |
| OutgoingCommandDataStreamBufferComponent | OutgoingCommandDataStreamBuffer |
| NetworkIdComponent | NetworkId |
| CommandTargetComponent | CommandTarget |
| GhostComponent | GhostInstance |
| GhostChildEntityComponent | GhostChildEntity |
| GhostOwnerComponent | GhostOwner |
| PredictedGhostComponent | PredictedGhost |
| GhostTypeComponent | GhostType |
| SharedGhostTypeComponent | GhostTypePartition |
| GhostCleanupComponent | GhostCleanup |
| GhostPrefabMetaDataComponent | GhostPrefabMetaData |
| PredictedGhostSpawnRequestComponent | PredictedGhostSpawnRequest |
| PendingSpawnPlaceholderComponent | PendingSpawnPlaceholder |
| ReceiveRpcCommandRequestComponent | ReceiveRpcCommandRequest |
| SendRpcCommandRequestComponent | SendRpcCommandRequest |

* `DefaultUserParams` 已重命名为 `DefaultSmoothingActionUserParams`
* `DefaultTranslateSmoothingAction` 已重命名为 `DefaultTranslationSmoothingAction`
* `ClientServerTickRate.MaxSimulationLongStepTimeMultiplier` 已重命名为 `ClientServerTickRate.MaxSimulationStepBatchSize`
* `NetworkCompressionModel` 已迁移到 Unity.Collections，并重命名为 `StreamCompressionModel`
* 辅助方法 `GhostPredictionSystemGroup.ShouldPredict` 已迁移到 `PredictedGhost`
* `GhostComponentAttribute.OwnerPredictedSendType` 已重命名为 `GhostComponentAttribute.SendTypeOptimization`
* `GhostPredictionSystemGroup` 已重命名为 `PredictedSimulationSystemGroup`

<a id="predictedtick-servertick-and-time-information"></a>
## PredictedTick、ServerTick 与时间信息

与当前模拟 Tick 有关的全部信息都必须从 `NetworkTime` 单例获取，具体包括：

* `GhostPredictionSystemGroup.PredictedTick` 已移除。应始终改用 `NetworkTime.ServerTick`；在预测循环内部读取时，它会正确反映当前预测 Tick
* `GhostPredictionSystemGroup.IsFinalPredictionTick` 已移除，请改用 `NetworkTime.IsFinalPredictionTick`
* `ClientSimulationSystemGroup` 上的 `ServerTick`、`ServerTickFraction`、`InterpolationTick` 和 `InterpolationTickFraction` 已移除，可以从 `NetworkTime` 单例获取相同属性

有关不同时间属性和标志行为的详细信息，请参阅 `NetworkTime` 组件文档

<a id="use-the-new-singletons-to-access-apis-and-shared-data"></a>
## 使用新单例访问 API 与共享数据

除少数例外外，所有 Netcode 系统都应视为无状态。全部公开且可访问的数据都存储在实体单例中。许多 API 已从系统移除，并迁移到以下单例组件：

* 使用 Netcode 日志系统时，将 `GetExistingSystem<NetDebugSystem>().NetDebug` 替换为 `GetSingleton<NetDebug>()`；修改日志级别时使用 `GetSingletonRW<NetDebug>()`
* `Connect` 和 `Listen` 方法已迁移到 `NetworkStreamDriver` 单例
* `GhostSimulationSystemGroup.SpawnedGhostEntityMap` 已替换为 `SpawnedGhostEntityMap` 单例
* Ghost 相关性映射和模式已从 `GhostSendSystem` 迁移到 `GhostRelevancy` 单例
* `GhostCountOnServer` 和 `GhostCountOnClient` 已从 `GhostReceiveSystem` 迁移到 `GhostCount` 单例 API
* 注册预测平滑函数的 API 已从 `GhostPredictionSmoothingSystem` 迁移到 `GhostPredictionSmoothing` 单例
* 注册 RPC 和获取 RPC 队列的 API 已从 `RpcSystem` 迁移到 `RpcCollection` 单例
* 将 `GetExistingSystem<GhostSimulationSystemGroup>().SpawnedGhostEntityMap` 替换为 `GetSingleton<SpawnedGhostEntityMap>().Value`。不再需要等待或设置 `LastGhostMapWriter`，应删除相关代码
* 将 `GetExistingSystem<GhostSendSystem>().GhostRelevancySet` 和 `GhostRelevancyMode` 替换为 `GetSingletonRW<GhostRelevancy>().GhostRelevancySet` 和 `GhostRelevancyMode`。不再需要等待或设置 `GhostRelevancySetWriteHandle`，应删除相关代码
* 将 `GetExistingSystem<NetworkStreamReceiveSystem>().Connect` 和 `Listen` 替换为 `GetSingletonRW<NetworkStreamDriver>().Connect` 和 `Listen`

<a id="changes-in-visibility-and-deprecated-apis"></a>
## 可见性与弃用 API 变更

* `LagCompensationConfig` 已移除。请使用统一的 `NetCodePhysicsConfig` Authoring 组件启用延迟补偿，不再使用 `LagCompensationConfig` Authoring 组件
* 对静态 `RpcSystem.DynamicAssemblyList` 的调用应替换为对同名实例属性的调用。请确保在创建 World 期间、`RpcSystem.OnUpdate` 调用前完成设置。NetcodeSamples 中提供了示例
* 仅编辑器使用的 `ClientServerBootstrap.RequestedAutoConnect` 应替换为 `ClientServerBootstrap.TryFindAutoConnectEndPoint`，后者能够处理全部 `PlayType`
* `GhostCollectionSystem.CreatePredictedSpawnPrefab` API 已移除。客户端现在会自动设置预测生成 Ghost 预制体，可以按常规方式实例化，无需调用该 API
* `PrespawnsSceneInitialized`、`SubScenePrespawnBaselineResolved`、`PrespawnGhostBaseline`、`PrespawnSceneLoaded` 和 `PrespawnGhostIdRange` 现为 internal
* `PrespawnSubsceneElementExtensions` 现为 internal
* `LiveLinkPrespawnSectionReference` 现为 internal。它只在编辑器中作为 Entities 转换限制的临时解决方案使用，不应是用户可以添加的公开组件
* 静态 bool `RpcSystem.DynamicAssemblyList` 已移除，并替换为同名非静态属性
* `ClientServerBootstrap.RequestedAutoConnect` 仅编辑器属性已替换为 `ClientServerBootstrap.TryFindAutoConnectEndPoint`
* `ThinClientComponent` 已移除，请改用 `World.IsThinClient()`
* `NetworkStreamDisconnected` 组件已移除。请为需要检测断开连接的连接添加 `ConnectionState` 组件，并使用响应式系统处理
* `CommandReceiveClearSystem` 和 `CommandSendPacketSystem` 现为 internal
* `StartStreamingSceneGhosts` 和 `StopStreamingSceneGhosts` 现为 internal RPC。如果需要自定义预生成场景流程，用户必须添加自己的 RPC

<a id="new-way-to-pass-templates-to-source-generator"></a>
## 向源码生成器传递模板的新方式

* Netcode 源码生成器模板现在必须通过 `additional files` 传给生成器。模板扩展名必须是 `NetCodeSourceGenerator.additionalfile`，并使用唯一 ID 标识；该 ID 必须出现在模板第一行<br/>
  详细信息请参阅[编写模板](ghost-types-templates.md#writing-the-template)和[向 Netcode 注册新模板](ghost-types-templates.md#registering-your-new-template-with-netcode)文档

<a id="netcode-groups-world-filtering-and-detect-world-types"></a>
## Netcode 系统组、World 过滤与 World 类型检测

* 使用 `World` 和 `WorldUnmanaged` 上的 `IsClient`、`IsServer` 和 `IsThinClient` 辅助方法，判断 World 是否分别为客户端、服务器或瘦客户端
* Netcode 专用顶层系统组和 `[UpdateInWorld]` 已移除，并由 `[WorldSystemFilter]` 替代。映射如下：

| 旧写法 | 新写法 |
|--------|--------|
| `[UpdateInGroup(typeof(ClientInitializationSystemGroup))]` | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInGroup(typeof(ClientSimulationSystemGroup))]` | `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInGroup(typeof(ClientPresentationSystemGroup))]` | `[UpdateInGroup(typeof(PresentationSystemGroup))]` |
| `[UpdateInGroup(typeof(ServerInitializationSystemGroup))]` | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]` |
| `[UpdateInGroup(typeof(ServerSimulationSystemGroup))]` | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]` |
| `[UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup))]` | `[UpdateInGroup(typeof(InitializationSystemGroup))][WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation&#124;WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]` | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation&#124;WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInWorld(TargetWorld.Client)]` | `[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInWorld(TargetWorld.Server)]` | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]` |
| `[UpdateInWorld(TargetWorld.ClientAndServer)]` | `[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation&#124;WorldSystemFilterFlags.ClientSimulation)]` |
| `[UpdateInWorld(TargetWorld.Default)]` | `[WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]` |
| `if (World.GetExistingSystem<ServerSimulationSystemGroup>()!=null)` | `if (World.IsServer())` |
| `if (World.GetExistingSystem<ClientSimulationSystemGroup>()!=null)` | `if (World.IsClient())` |

<a id="major-changes-for-ghost-field-serialization"></a>
## Ghost 字段序列化的重大变更

* Ghost 中的全部子实体现在默认使用 `DontSerializeVariant`，因为序列化子 Ghost 的成本相对较高。其原因包括子实体位于其他 Chunk，引用局部性较差，以及遍历子实体需要随机访问。因此，`GhostComponentAttribute.SendDataForChildEntity = false` 现在是默认值；对于需要在子实体上发送的全部类型，必须显式将该标志设为 true。如果需要复制层级结构，强烈建议创建多个 Ghost 预制体，并使用自定义的模拟 Transform 父子逻辑保持扁平层级。只有同一层级的快照更新必须同步时，才应使用显式子层级
* `RegisterDefaultVariants` 的签名已改为使用 `Rule`。用户现在必须明确指定自定义默认变体是否也应用于子实体
* 升级过程会清除全部 `GhostAuthoringComponent` `ComponentOverrides`。请通过新的可选 `GhostAuthoringInspectionComponent` 重新应用全部 `ComponentOverrides`
* 在 `RegisterDefaultVariants` 方法内，将所有 `defaultVariants.Add(new ComponentType(typeof(SomeType)), typeof(SomeTypeDefaultVariant));` 替换为 `defaultVariants.Add(new ComponentType(typeof(SomeType)), Rule.OnlyParent(typeof(SomeTypeDefaultVariant)));`。如果也希望将变体应用于子实体，则使用 `Rule.ParentAndChildren(typeof(SomeTypeDefaultVariant))`

注意：应尽可能优先使用特性。只有无法通过特性表达的一次性差异，才应使用这种手动覆盖方式
