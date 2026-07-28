# 降低预测开销

降低预测的 CPU 开销以提升游戏性能

* [物理调度](#物理调度)
* [预测模式切换](#预测模式切换)
* [使用 `MaxSendRate` 降低客户端预测成本](#使用-maxsendrate-降低客户端预测成本)
* [使用 `ForcedInputLatencyTicks`](#使用-forcedinputlatencyticks)
* [限制结构性变化后的重新模拟](#限制结构性变化后的重新模拟)

## 物理调度

在游戏中使用[物理](../physics.md)时，`PhysicsSimulationGroup` 会在 `PredictedFixedStepSimulationSystemGroup` 内运行。在高 Ping 环境下，例如需要重新模拟 20 帧以上时，可能会产生调度开销。可以通过强制大部分物理工作在主线程执行来降低该开销：向场景添加一个 [`Physics Step`](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/component-step.html) 单例，并将 __Multi Threaded__ 设为 `false`

## 预测模式切换

预测成本会随预测 Ghost 数量增加。为优化该成本，可以根据一组条件选择不预测某个 Ghost，例如 Ghost 与客户端角色控制器之间的距离

详细信息请参阅[预测模式切换页面](../prediction-switching.md)

## 使用 `MaxSendRate` 降低客户端预测成本

[`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) 设置对预测 Ghost 的影响尤其明显，因为预测 Ghost 只有在快照中被接收后才会回滚并重新模拟

降低 Ghost Chunk 加入快照的频率，会间接降低预测 Ghost 的重新模拟频率，从而节省客户端 CPU 周期。但是，这可能导致更大的客户端误预测误差，进而产生幅度更大、玩家更容易察觉的校正

> [!NOTE]
> Ghost 组中的子项在离开组之前不支持 `MaxSendRate`，也不支持 Relevancy、Importance、Static-Optimization 等设置。详细信息请参阅 [Ghost 组页面](../ghost-groups.md)

## 使用 `ForcedInputLatencyTicks`

[`ClientTickRate.ForcedInputLatencyTicks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.ForcedInputLatencyTicks.html) 能够减少客户端平均每帧需要执行的预测步骤数，但代价是明显增加输入延迟，使玩家感觉游戏响应变慢

它还有另外两个优点：

* 较高的值会降低预测错误的发生概率和严重程度，尤其是在网络连接良好时
* 使用恒定的强制输入延迟值时，玩家可以逐渐适应固定延迟，并将其感知为角色控制器固有的“重量感”，因此可能不会察觉该延迟。如果 UI、动画或特效等非模拟内容能够在轮询到新输入的同一帧作出响应，这一点尤其明显；对于本身通常具有较高延迟的移动端和主机平台也同样如此

> [!NOTE]
> 若要在输入命令代码中正确处理强制输入延迟，请参阅 `NetworkTime.InputTargetTick`。在某些情况下，应使用它代替 `ServerTick`

## 限制结构性变化后的重新模拟

默认情况下，`RollbackPredictionOnStructuralChanges` 设为 true。当客户端上的预测 Ghost 发生结构性变化时，例如添加或移除复制组件，系统会重新模拟该 Ghost，以尽量保证预测准确。重新模拟从最近收到的服务器快照开始，一直执行到当前客户端预测 Tick，成本可能很高，尤其涉及物理时，因为必须重建整个 World

可以在 `GhostAuthoringComponent` Inspector 中取消勾选 `RollbackPredictionOnStructuralChanges`，按预制体禁用这种重新模拟。当 `RollbackPredictionOnStructuralChanges` 设为 false 时，`GhostUpdateSystem` 会复用现有预测历史，以潜在的预测偏差为代价节省大量 CPU 处理

总体而言，将 `RollbackPredictionOnStructuralChanges` 设为 false 可以成为有效的性能优化，尤其是游戏不要求极高预测精度时。但是，[移除并重新添加组件](#移除并重新添加组件)时可能产生竞态条件

### 移除并重新添加组件

如果在运行时从 Ghost 移除再重新添加复制组件，同时将 `RollbackPredictionOnStructuralChanges` 设为 false，可能导致结果不一致

收到该 Ghost 的新更新时，快照数据包含服务器发送的最近值。但是，如果此时组件不存在，组件值就不会被恢复。之后重新添加该组件时，由于实体没有回滚并重新预测，组件的当前状态会保持为默认值，即全部为零。相比之下，如果启用 `RollbackPredictionOnStructuralChanges`，实体会被重新预测，重新添加的组件值也会得到正确恢复

## 其他资源

* [预测](../intro-to-prediction.md)
* [预测模式切换](../prediction-switching.md)
