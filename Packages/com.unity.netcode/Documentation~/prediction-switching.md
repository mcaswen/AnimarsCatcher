# 预测模式切换

在典型多人游戏中，通常只希望预测客户端直接交互的 Ghost，也就是使用 `GhostMode.Predicted`，因为预测会消耗大量 CPU。示例包括：

- 玩家自己的角色控制器，通常使用 `GhostMode.OwnerPredicted`
- 角色控制器正在碰撞的动态对象，例如箱子、球、平台和载具
- 客户端触发的交互物品，例如武器，以及投射物等相关实体

对于 World 中的大多数 Ghost，应使用 `GhostMode.Interpolated` 进行插值。Netcode 支持根据某些条件，以每个客户端、每个 Ghost 为单位选择启用预测，例如预测客户端角色控制器一定半径内的所有 Ghost
此功能称为预测模式切换

<a id="the-client-singleton"></a>
## 客户端单例

客户端单例 `GhostPredictionSwitchingQueues` 提供两个队列，可将 Ghost 加入其中：

- `ConvertToPredictedQueue`：将插值 Ghost 转换为预测 Ghost，通过 `GhostPredictionSwitchingSystem.ConvertGhostToPredicted` 实现
- `ConvertToInterpolatedQueue`：将预测 Ghost 转换为插值 Ghost，通过 `GhostPredictionSwitchingSystem.ConvertGhostToInterpolated` 实现

`GhostPredictionSwitchingSystem` 会自动转换这些 Ghost，也就是在运行时修改 Ghost 的 `GhostMode`
实际表现为添加或移除 `PredictedGhost`

<a id="prediction-switching-queue-rules"></a>
## 预测模式切换队列规则

- 实体必须是 Ghost
- Ghost 类型，也就是预制体，必须通过 [`GhostAuthoringComponent`](ghost-snapshots.md#authoring-ghosts) 将 `Supported Ghost Modes` 设为 `All`
- 其 `CurrentGhostMode` 不能设为 `OwnerPredicted`，因为 `OwnerPredicted` Ghost 已经会根据所有权切换预测模式
- 切换为 `Predicted` 时，Ghost 当前必须处于 `Interpolated` 模式，反之亦然
- Ghost 当前不能正在切换预测模式，参阅下方转换章节和 `SwitchPredictionSmoothing` 组件

> [!NOTE]
> 切换系统会检查这些规则，因此无效的队列条目会被安全忽略，同时记录错误或警告日志

<a id="timeline-issues-with-prediction-switching"></a>
## 预测模式切换的时间线问题

预测模式切换会将 Ghost 从一条相对[时间线](interpolation.md#timelines)移动到另一条时间线，可能在转换期间引发视觉问题，并导致 Ghost 向前或向后瞬移超过 `2 x Ping` 毫秒的距离

- 预测 Ghost 与客户端运行在同一条时间线上，大约领先服务器一个 Ping
- 插值 Ghost 运行在服务器之后的时间线上，大约落后服务器一个 Ping

<a id="the-switchpredictionsmoothing-component-and-prediction-switching-transitions"></a>
### `SwitchPredictionSmoothing` 组件与预测模式切换转换

可以使用临时组件 `SwitchPredictionSmoothing` 及处理该组件的 `SwitchPredictionSmoothingSystem`，通过预测模式切换平滑来缓解时间线跳变。该平滑过程使用线性插值，在用户将实体加入队列时通过 `ConvertPredictionEntry.TransitionDurationSeconds` 指定的时间内，自动在实体 `Transform` 的 `Position` 和 `Rotation` 值之间转换

平滑过程并不完美，频繁改变方向的高速对象仍可能出现视觉瑕疵。最佳实践是将 `TransitionDurationSeconds` 设得足够高以避免瞬移，同时又足够低以减少突然改变方向的频率

<a id="component-modification-with-prediction-switching"></a>
## 预测模式切换时的组件修改

预测模式切换还存在一个额外问题：可能已经通过 `GhostAuthoringInspectionComponent` 和/或变体，从 Ghost 的预测版本或插值版本中移除了特定组件。因此，每当 Ghost 在运行时切换预测模式时，都需要使用 `AddRemoveComponents` 方法添加或移除这些组件，以保持与规则一致

> [!NOTE]
> 此过程会自动执行，但需要注意，重新添加组件时，组件值会重置为创作阶段烘焙的值

<a id="example-code"></a>
## 示例代码

```c#
// 以读写方式获取单例，因为需要修改单例中的集合数据
ref var ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;

// 立即将 Ghost 实体 entityA 转换为 Predicted，即在 GhostPredictionSwitchingSystem 下一次运行时转换
// 如果该实体正在移动，它会发生瞬移
ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
{
    TargetEntity = entityA,
    TransitionDurationSeconds = 0f,
});

// 在 1 秒内将 Ghost 实体 entityB 转换为 Interpolated
// 系统会自动对 Transform 的 Position 和 Rotation 应用线性插值，以平滑并在一定程度上掩盖时间线变化
ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
{
    TargetEntity = entityA,
    TransitionDurationSeconds = 1f,
});
```
