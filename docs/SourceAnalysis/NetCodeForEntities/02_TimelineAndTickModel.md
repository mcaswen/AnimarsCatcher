# NetCode 时间线与 Tick 模型

[返回源码分析目录](../README.md) | [上一篇：架构分析](01_Architecture.md)

> 分析版本：`com.unity.netcode 1.9.0`
>
> 主要源码：`Runtime/PredictionTicking`、`Runtime/Command`、`Runtime/Connection`
>
> 项目基线：Unity `6000.2.7f2`

## 1. 先区分五个容易混淆的概念

理解 NetCode 时间模型前，必须先把 Frame、Tick、Step、Snapshot 和 Physics Step 分开。

- **Unity Frame**：一次 PlayerLoop。渲染帧率决定一秒调用多少次 PlayerLoop，但不直接决定服务器模拟多少 Tick
- **Simulation Tick**：权威玩法时间的离散刻度，频率由 `SimulationTickRate` 决定。60 Hz 时一个 Tick 是 `1 / 60 = 16.667 ms`
- **System Step**：某个 System Group 的一次实际执行。一次 Step 通常覆盖一个 Tick，也可能通过 batching 覆盖多个逻辑 Tick
- **Snapshot**：服务器在某个 Simulation Tick 上构建的 Ghost 状态包。`NetworkTickRate` 只控制 Snapshot 频率，不创建第二套独立 Tick
- **Predicted Fixed Step**：预测循环内部的物理或 KCC 子步，频率为 `SimulationTickRate * PredictedFixedStepSimulationTickRatio`

因此，“一帧执行了几次”和“一次执行覆盖了几个 Tick”是两个不同问题：

```text
一帧多次 Step：服务器在补赶，或客户端在回滚重模拟
一次 Step 多个 Tick：服务器或客户端预测循环启用了 Tick batching
一帧零次服务器 Step：帧时间尚未积累到一个固定 Tick，SimulationSystemGroup 被跳过
一帧一次客户端 Step：客户端仍可能只推进了一个 Partial Tick，而不是完整 Tick
```

## 2. NetworkTick 如何表示时间刻度

[`NetworkTick`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTick.cs) 内部只有一个 `uint m_Value`，但最低位被用作有效标记。构造逻辑等价于：

```csharp
m_Value = (tickIndex << 1) | 1u;
```

由此得到：

- `default(NetworkTick)` 的内部值是 0，因此表示 Invalid
- Tick Index 0 的内部序列化值是 1，仍然是有效 Tick
- Tick Index 1 的内部序列化值是 3
- 每次 `Increment()` 实际让内部值加 2
- `SerializedData` 包含 Tick Index 和有效位，不能把它当连续递增的普通 Tick 序号

`TickIndexForValidTick` 才是去掉有效位后的索引，但源码明确提醒它只适合显示或有限范围计算，因为 Tick 会回绕。

### 2.1 为什么不能直接比较 uint

Tick 是环形序号。接近最大值后再递增会回到较小数值，因此下面的普通整数比较不可靠：

```csharp
newTick.SerializedData > oldTick.SerializedData
```

源码提供两种主要比较方式：

- `newTick.IsNewerThan(oldTick)` 判断环形序号上的新旧关系
- `newTick.TicksSince(oldTick)` 返回两者相差的 Tick 数，可为负数

`TicksSince` 的核心是先进行无符号减法，再转成有符号整数并右移一位。这样在不跨越半个序号空间的正常网络历史窗口内，可以正确处理回绕。

项目代码涉及 Snapshot、Command、SpawnTick、冷却结束 Tick 或预测事件去重时，都应使用这两个 API。`SerializedData` 适合网络传输、哈希键和原样保存，不适合做大于、小于或加减运算。

## 3. NetworkTime 不是一个 Tick，而是一组时间线

[`NetworkTime`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTime.cs) 是 Client 和 Server World 都存在的 Singleton。最重要的三个 Tick 是：

- `ServerTick`：当前系统正在模拟的服务器 Tick
- `InputTargetTick`：客户端此刻采集的输入应该写入哪个 Tick
- `InterpolationTick`：客户端远端插值 Ghost 当前显示哪个历史 Tick

客户端的时间顺序可以理解为：

```text
更旧                                                               更未来
InterpolationTick -> 最近收到的 Snapshot Tick -> ServerTick -> InputTargetTick
远端显示时间          已知权威状态                 本地预测时间    输入发送目标
```

这四个位置不会始终保持固定距离。`NetworkTimeSystem` 会根据 RTT、Snapshot 到达间隔、丢包、抖动和 Command Age 缓慢调整预测速度与插值速度。

### 3.1 同名 ServerTick 在两端含义不同

服务端的 `ServerTick` 是权威模拟刻度：

- 始终是 Full Tick
- 单调向前
- 普通 Step 加一，batched Step 可能一次跳过多个 Tick Index
- `InterpolationTick` 在服务端等于 `ServerTick`

客户端的 `ServerTick` 是“客户端认为此刻应该模拟到的服务器 Tick”：

- 可以是 Partial Tick
- 进入预测循环后会临时退回到历史 Tick，再逐步重放回来
- 网络严重失同步时可能整体回退或跳前
- 退出预测循环后恢复为本帧当前预测目标

所以客户端业务系统不能把 `ServerTick` 理解成“服务器最后确认给我的 Tick”。后者更接近 `NetworkSnapshotAck.LastReceivedSnapshotByLocal`。

### 3.2 Fraction 表达 Partial Tick

`ServerTickFraction` 和 `InterpolationTickFraction` 表示当前 Tick 已推进的比例，范围是 `(0, 1]`。Fraction 小于 1 时，`NetworkTime.IsPartialTick` 为 true。

例如客户端时间是 `Tick 101, Fraction 0.4`，表示时间线已经完成 Tick 100，正在 Tick 101 内推进 40%。如果系统只接受完整 Tick，可以使用 `NetworkTimeHelper.LastFullServerTick`，它会在 Partial Tick 时返回 100。

客户端渲染帧率通常不等于 60 Hz。以 144 FPS 和 60 Hz Simulation 为例，每个渲染帧只经过约 `0.4167` 个 Simulation Tick，因此客户端需要 Partial Tick 才能平滑推进，而不能像服务端一样只在完整 Tick 上更新。

## 4. 配置参数分别控制什么

[`ClientServerTickRate`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerTickRate.cs) 控制服务器和预测循环的基础节奏：

- `SimulationTickRate`：玩法模拟频率，默认 60 Hz
- `NetworkTickRate`：每个客户端接收 Snapshot 的目标频率，默认等于 Simulation Tick Rate
- `PredictedFixedStepSimulationTickRatio`：预测物理相对 Simulation Tick 的整数倍频
- `MaxSimulationStepsPerFrame`：服务器一帧最多执行多少次 Simulation Step
- `MaxSimulationStepBatchSize`：一次 Step 最多合并多少个逻辑 Tick
- `SendSnapshotsForCatchUpTicks`：服务器补赶时是否为中间 Step 也发送 Snapshot
- `ClampPartialTicksThreshold`：客户端靠近完整 Tick 时是否吸附到 Tick 边界

[`ClientTickRate`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerTickRate.cs) 控制客户端时间线：

- `TargetCommandSlack`：希望输入比服务器消费时间提前多少 Tick 到达
- `MaxPredictAheadTimeMS`：客户端最多愿意承担多长预测窗口，超过部分转为输入延迟
- `ForcedInputLatencyTicks`：主动延迟输入，减少预测窗口
- `InterpolationTimeNetTicks` 或 `InterpolationTimeMS`：基础插值缓冲
- `PredictionTimeScaleMin/Max`：预测时间线纠偏时允许的速度范围
- `InterpolationTimeScaleMin/Max`：插值时间线纠偏时允许的速度范围
- `MaxPredictionStepBatchSizeRepeatedTick`：已预测过的历史 Tick 最大合并数
- `MaxPredictionStepBatchSizeFirstTimeTick`：首次预测的新 Tick 最大合并数

Simulation、Snapshot 和 Physics 三种频率的关系是：

```text
SimulationTickRate = 60
NetworkTickRate = 30
PredictedFixedStepSimulationTickRatio = 2

权威玩法：60 次/秒
每个客户端 Snapshot：30 个/秒，正常间隔 2 个 Simulation Tick
预测物理：120 次/秒，每个 Simulation Tick 内 2 个物理子步
```

Command 和 RPC 的发送频率不受 `NetworkTickRate` 控制。该参数只控制 Ghost Snapshot。

## 5. 服务端如何把帧时间换成 Tick

服务端主链路是：

```text
Unity Frame DeltaTime
  -> NetcodeServerRateManager
  -> NetcodeTimeTracker 累积时间
  -> 计算本帧需要补多少逻辑 Tick
  -> 按 MaxSimulationStepsPerFrame 决定执行几次 Step
  -> 按 MaxSimulationStepBatchSize 决定每次 Step 覆盖几个 Tick
  -> PushTime 写入该 Step 的 DeltaTime 和 ElapsedTime
  -> SimulationSystemGroup 执行
```

核心实现在 [`NetcodeTimeTracker`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeTimeTracker.cs) 和 [`NetcodeServerRateManager`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeServerRateManager.cs)。

### 5.1 累加器算法

源码逻辑可简化为：

```text
accumulatedTime += frameDeltaTime
logicalTicks = floor(accumulatedTime / fixedTimeStep)

如果 logicalTicks <= MaxSimulationStepsPerFrame：
    每个 Step 覆盖 1 Tick
否则：
    batchLength = ceil(logicalTicks / MaxSimulationStepsPerFrame)
    batchLength = min(batchLength, MaxSimulationStepBatchSize)
    最多执行 MaxSimulationStepsPerFrame 次 Step

从 accumulatedTime 中扣除本帧实际消费的时间
无法消费的剩余时间留到下一帧
```

NetCode 优先执行多个独立 Step，因为它比 batching 准确。只有待补 Tick 数超过 `MaxSimulationStepsPerFrame`，才开始加大每个 Step 的 Batch Size。

### 5.2 五个 Tick 延迟的例子

假设配置已经应用：

```text
SimulationTickRate = 60
MaxSimulationStepsPerFrame = 2
MaxSimulationStepBatchSize = 4
本帧积累时间 = 5 个 Tick
```

算法得到：

```text
需要 5 个逻辑 Tick
一帧最多 2 次 Step
ceil(5 / 2) = 3

Step 1：SimulationStepBatchSize = 3，DeltaTime = 3 * 16.667 ms
Step 2：SimulationStepBatchSize = 2，DeltaTime = 2 * 16.667 ms
```

如果帧开始时 `ServerTick = 100`，两次执行看到的 Tick 大致是 103 和 105。Tick 101、102、104 没有分别执行完整 System Group，而是被较大的 DeltaTime 合并覆盖。

这能追回正确的总游戏时间，但会丢失中间离散状态。例如按每 Tick 检查一次碰撞、每 Tick 产生一个单位、每 Tick 递增一次计数的逻辑，都不能只依赖“一次 OnUpdate 等于一次 Tick”的假设。

### 5.3 超过最大吞吐能力

仍使用上面的配置，如果一帧积累了 10 个 Tick：

```text
ceil(10 / 2) = 5
Batch Size 被限制为 4
本帧两次 Step 最多覆盖 8 个 Tick
剩余 2 个 Tick 留在累加器中
```

此时服务器游戏时间仍落后于现实时间。后续帧负载降低后，Rate Manager 会继续补赶。如果持续积压，说明服务器无法满足目标 Simulation Tick Rate，调大参数只能改变退化方式，不能消除真实 CPU 瓶颈。

### 5.4 IsCatchUpTick 与 batching 不是同义词

`NetworkTime.IsCatchUpTick` 在一帧执行多次 Simulation Step 时，对非最终 Step 为 true，最终 Step 为 false。单次 Step 即使 `SimulationStepBatchSize > 1`，也不一定标记为 CatchUp Tick。

因此应这样判断：

- `SimulationStepBatchSize > 1`：这一次执行合并了多个逻辑 Tick
- `IsCatchUpTick`：本帧后面还有用于补赶的服务器 Step

[`WarnAboutBatchedTicksSystem`](../../../Packages/com.unity.netcode/Runtime/Connection/WarnAboutBatchedTicksSystem.cs) 检查的是 `SimulationStepBatchSize` 的滚动平均。窗口为 4、初值为 1 时，一次 Batch Size 2 会得到：

```text
1 - 1 / 4 + 2 / 4 = 1.25
```

所以 1.25 不是在证明最近四帧一定执行了五个独立 Tick，而是在表示滚动平均刚被一次二合一 batching 拉高。

## 6. 客户端怎样估算预测时间线

客户端时间同步中心是 [`NetworkTimeSystem`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTimeSystem.cs)。它在 `InitializationSystemGroup` 中运行，根据 `NetworkSnapshotAck` 更新内部 `NetworkTimeSystemData`。

第一份 Snapshot 到达前，客户端没有有效网络时间。收到 Tick 为 `S` 的第一份 Snapshot 后，系统先计算：

```text
rttInTicks = ceil(EstimatedRTT * SimulationTickRate / 1000)
rawInputAhead = rttInTicks + TargetCommandSlack
maxPredictionTicks = ceil(MaxPredictAheadTimeMS * SimulationTickRate / 1000)
effectiveInputLatency = max(rawInputAhead - maxPredictionTicks, ForcedInputLatencyTicks, 0)
predictAhead = rawInputAhead - effectiveInputLatency
predictTargetTick = S + predictAhead
```

以 60 Hz、RTT 100 ms、Slack 2 为例：

```text
rttInTicks = 6
rawInputAhead = 8
MaxPredictAheadTimeMS = 500 ms -> 30 Tick
effectiveInputLatency = 0
客户端 ServerTick 初始目标约为 S + 8
InputTargetTick 也约为 S + 8
```

若 RTT 变为 600 ms：

```text
rttInTicks = 36
rawInputAhead = 38
最大预测窗口 = 30 Tick
effectiveInputLatency = 8
客户端 ServerTick 约为 S + 30
InputTargetTick = ServerTick + 8，仍约为 S + 38
```

这就是 `InputTargetTick` 与 `ServerTick` 分离的主要场景：客户端不愿再扩大预测 CPU 成本和误差窗口，于是把超出的部分转为本地输入延迟。

### 6.1 Command Age 反馈闭环

服务器 [`CommandReceiveClearSystem`](../../../Packages/com.unity.netcode/Runtime/Command/CommandReceiveSystem.cs) 计算：

```text
age = currentServerTick - mostRecentFullCommandTick
ServerCommandAge = EMA(age)
```

值使用 8 位小数定点数保存。客户端收到 Snapshot 后，把 Command Age 与 `TargetCommandSlack` 比较：

```text
commandAge = ServerCommandAge / 256 + TargetCommandSlack
predictionTimeScale = clamp(
    1 + CommandAgeCorrectionFraction * commandAge,
    PredictionTimeScaleMin,
    PredictionTimeScaleMax)
```

如果命令比期望更晚，客户端时间线略微加速；如果提前过多，则略微减速。默认范围是 0.9 到 1.1，不会每次误差都直接跳 Tick。误差超过约 10 Tick 时，源码才会重置预测目标，严重情况下可能看到客户端 `ServerTick` 回退。

## 7. 客户端怎样维护插值时间线

插值时间线不直接从预测 `ServerTick` 倒推，而是从 `latestSnapshotEstimate` 倒推。这是为了避免预测时间线因 Command Age 纠偏而加速时，把远端插值也无意义地一起加速。

首次 Snapshot 的初始公式近似为：

```text
snapshotInterval = SimulationTickRate / NetworkTickRate
interpolationDelay = InterpolationTimeNetTicks * snapshotInterval
                     + 2 * estimatedJitter
InterpolationTick = firstSnapshotTick - interpolationDelay
```

如果 Simulation 是 60 Hz、Snapshot 是 30 Hz、`InterpolationTimeNetTicks = 2`、初始抖动接近 0：

```text
一个 Snapshot 间隔 = 2 个 Simulation Tick
基础插值窗口 = 2 * 2 = 4 个 Simulation Tick
收到 Snapshot 100 时，初始 InterpolationTick 约为 96
```

后续系统同时观察：

- 相邻 Snapshot 的 Tick 差移动平均
- Snapshot Tick 差的偏差
- 本地包到达间隔移动平均
- 用户配置的基础插值窗口

最终插值窗口取基础窗口和网络实际抖动需求中的较大者，再通过 `InterpolationTimeScaleMin/Max` 平滑追赶。突变超过 10 Tick 时才直接跳到新目标。

低 `NetworkTickRate`、丢包或高抖动都会自然扩大插值窗口。代价是远端对象显示得更晚，但可用于插值的历史样本更多。

## 8. Client Simulation Frame 与 Partial Tick

[`NetcodeClientRateManager`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeClientRateManager.cs) 从 `NetworkTimeSystemData` 读取预测 Tick、插值 Tick 和 Fraction，然后计算本帧网络 DeltaTime：

```text
networkDeltaTime =
    (currentTick - previousTick
     + currentFraction - previousFraction)
    * fixedTimeStep
```

它把这个 DeltaTime 通过 `World.PushTime` 写入 Client World。因此客户端业务系统读取 `SystemAPI.Time.DeltaTime` 时，得到的是网络时间线推进量，不一定等于原始 Unity Frame DeltaTime。

原始未缩放的 Unity 时间保存在 `UnscaledClientTime`。UI、纯视觉动画或不应随网络时间纠偏的逻辑需要使用未缩放时间，而不是预测时间。

`ClampPartialTicksThreshold` 默认 5。Fraction 距离 Tick 边界在 5% 内时，系统会吸附到完整 Tick，减少数值噪声和频繁极短 Partial Step。

## 9. 新 Snapshot 如何触发回滚重模拟

客户端预测循环由 [`NetcodeClientPredictionRateManager`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeClientPredictionRateManager.cs) 驱动，但回滚起点来自 `GhostUpdateSystem` 和预测历史备份。

完整链路是：

```text
收到新 Snapshot
  -> GhostReceiveSystem 写入每个 Ghost 的 Snapshot 历史
  -> GhostUpdateSystem 找出哪些 Predicted Ghost 收到更新
  -> 为每个 Ghost 计算 PredictionStartTick
  -> 汇总 AppliedPredictedTicks
  -> Prediction Rate Manager 找最旧需要恢复的 Tick
  -> 从 Snapshot 或 GhostPredictionHistory 恢复状态
  -> 重放后续 Full Tick
  -> 必要时执行当前 Partial Tick
  -> 最后一个 Full Tick 后重新备份预测状态
```

假设当前客户端目标是 `110 + 0.4`，新权威 Snapshot 要求从 106 恢复：

```text
恢复 Tick 106 的状态
重放 Full Tick 107
重放 Full Tick 108
重放 Full Tick 109
执行 Tick 110 的 0.4 Partial Step
退出预测循环后，NetworkTime 恢复为 110 + 0.4
```

同一个 Unity Frame 中，`PredictedSimulationSystemGroup` 因此可能执行四次。Ping 越高、Snapshot 越晚、预测误差越频繁，重放 Tick 数通常越多。

### 9.1 预测标记的准确含义

预测循环会修改 `NetworkTime.Flags`：

- `IsInPredictionLoop`：当前在预测组内部
- `IsFirstPredictionTick`：本轮预测循环的第一次执行
- `IsFinalFullPredictionTick`：本轮最后一个完整 Tick
- `IsFinalPredictionTick`：本轮最后一次执行，可能是 Partial Tick
- `IsFirstTimeFullyPredictingTick`：这个 Full Tick 是客户端第一次真正预测，而不是历史重放

这些标记不能互相替代。

一次性音效、VFX 或无法回滚的外部副作用，最有价值的是 `IsFirstTimeFullyPredictingTick`，因为历史重放时它不会再次置位。`IsFinalPredictionTick` 只表示本轮循环结束，同一个预测目标在后续帧仍可能再次成为 Final Tick，不能单独作为永久去重条件。

### 9.2 Simulate 只让需要的 Ghost 参与

部分 Snapshot 可能只更新少量 Ghost。NetCode 不会强制所有 Predicted Ghost 都回滚到最旧 Tick，而是通过 Enableable `Simulate` 组件控制每个 Ghost 在当前重放 Tick 是否运行。

预测玩法查询应尊重 `Simulate` 的 Enabled State。忽略该状态会让不需要回滚的 Ghost 重复模拟，也可能破坏部分 Snapshot 优化。

## 10. 预测 batching 与服务器 batching 的区别

两者都通过 `SimulationStepBatchSize` 告诉业务系统一次更新覆盖多个 Tick，但用途不同：

- 服务器 batching 是因为现实时间积压，目标是避免补赶进入 CPU 死循环
- 客户端预测 batching 是合并已经预测过的历史 Tick，目标是降低回滚重放成本

客户端由两个参数分别限制：

- `MaxPredictionStepBatchSizeRepeatedTick` 控制历史重放
- `MaxPredictionStepBatchSizeFirstTimeTick` 控制第一次预测的新 Tick

默认 0 在运行时会解析为 1，即不合并。首次预测 Tick 一般比历史重放更不适合 batching，因为它可能包含新的输入边沿、碰撞和玩法事件。

业务系统使用 `SystemAPI.Time.DeltaTime` 可以自然处理连续积分，但以下逻辑必须额外考虑 Batch Size：

- 每 Tick 只允许执行一次的离散规则
- 依赖中间碰撞结果的高速运动
- 每 Tick 消费一条队列元素
- 以 Tick 次数而不是经过秒数计算的冷却或生成逻辑

如果逻辑必须观察每个中间 Tick，应保持对应 Batch Size 为 1，或在系统内部显式按 `SimulationStepBatchSize` 展开，但展开时还要保证每个子 Tick 使用正确输入和状态，不能只循环相同结果。

## 11. Command 应在哪条时间线上写和读

[`CommandDataUtility.AddCommandData`](../../../Packages/com.unity.netcode/Runtime/Command/ICommandData.cs) 的源码备注明确要求：

```text
ICommandData.Tick = NetworkTime.InputTargetTick
```

写入与读取的职责不同：

```text
GhostInputSystemGroup：
    用 InputTargetTick 创建和写入 Command

PredictedSimulationSystemGroup：
    用当前 ServerTick 从 Command Buffer 读取

GetDataAtTick：
    返回不晚于目标 Tick 的最新 Command
```

发送组也按 `InputTargetTick` 去重，每个目标 Tick 只序列化和发送一次 Command，并额外发送历史命令用于抗丢包。

## 12. AnimarsCatcher 当前实现映射

项目的读取路径是正确方向：[`ThirdPersonCharacterPredictedMoveSystem`](../../../Assets/Scripts/Player/Movement/ThirdPersonCharacterPredictedMoveSystem.cs) 位于 `PredictedFixedStepSimulationSystemGroup`，用当前 `NetworkTime.ServerTick` 调用 `GetDataAtTick`，将该预测 Tick 对应的输入写入角色控制组件。

当前写入路径存在一处需要后续整改的时间线偏差：

- [`ClientBuildThirdPersonMoveCommandWithFixedCameraSystem`](../../../Assets/Scripts/Player/Movement/Client/ClientBuildThirdPersonMoveCommandWithFixedCameraSystem.cs) 使用 `networkTime.ServerTick`
- [`ClientBuildThirdPersonMoveCommandWithOrbitCameraSystem`](../../../Assets/Scripts/Player/Movement/Client/ClientBuildThirdPersonMoveCommandWithOrbitCameraSystem.cs) 同样使用 `networkTime.ServerTick`
- [`ClientPlayerInputSystem`](../../../Assets/Scripts/Player/Input/Client/ClientPlayerInputSystem.cs) 也用 `ServerTick.SerializedData` 标记离散输入脉冲

在当前默认 `EffectiveInputLatencyTicks = 0` 时，`InputTargetTick` 通常等于 `ServerTick`，所以问题不明显。以下情况会让两者分离：

- 配置 `ForcedInputLatencyTicks > 0`
- RTT 超过 `MaxPredictAheadTimeMS` 能承受的预测窗口
- Single World Host 的 off-frame

后续修复时不能只改 `InputCommand.Tick` 一处。离散输入脉冲的采集 Tick 和 Command Buffer 的目标 Tick 必须一起改为 `InputTargetTick`，否则按键事件可能被写入另一个 Tick 后无法命中。

## 13. 当前配置资产并未成为 Global 配置

仓库存在 [`SO_NetCode_Default.asset`](../../../Assets/SO/Networking/SO_NetCode_Default.asset)，其中填写了：

```text
SimulationTickRate = 60
NetworkTickRate = 30
MaxSimulationStepsPerFrame = 2
MaxSimulationStepBatchSize = 4
InterpolationTimeNetTicks = 2
TargetCommandSlack = 2
```

但当前 [`NetCodeClientAndServerSettings.asset`](../../../ProjectSettings/NetCodeClientAndServerSettings.asset) 中：

```text
GlobalNetCodeConfig: {fileID: 0}
```

同时 SO 自身的 `IsGlobalConfig` 为 0。项目代码中也没有创建 `ClientServerTickRate` 或 `ClientTickRate` Singleton 的替代逻辑。

根据 [`ConfigureServerWorldSystem` 和 `ConfigureClientWorldSystem`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerBootstrap.cs)，只有 `NetCodeConfig.Global` 存在时才会把 SO 配置写入 World。因此当前仓库静态状态下，运行时应回退到包默认值：

```text
SimulationTickRate = 60
NetworkTickRate = 60
MaxSimulationStepsPerFrame = 1
MaxSimulationStepBatchSize = 4
客户端其余参数使用 NetworkTimeSystem.DefaultClientTickRate
```

也就是说，SO 中计划的 30 Hz Snapshot 和每帧最多 2 次服务器 Step 目前并未真正应用。要启用它，应通过 `Project Settings > Entities > NetCode` 把该资产设为 Global，使 Unity 同步更新 ProjectSettings、Preloaded Assets 和 `IsGlobalConfig`，而不是只修改 YAML 字段。

## 14. 项目代码使用 Tick 的规则

结合源码，项目后续应统一遵守以下规则：

1. 采集 `ICommandData` 或 `IInputComponentData` 时使用 `InputTargetTick`
2. 预测玩法系统按当前 `ServerTick` 读取输入并更新可回滚 ECS 状态
3. 完整 Tick 专用逻辑使用 `NetworkTimeHelper.LastFullServerTick`
4. Tick 新旧与距离使用 `IsNewerThan` 和 `TicksSince`
5. `SerializedData` 只用于序列化、原样存储和稳定键，不用于普通算术
6. 连续运动使用 NetCode 推入的 `SystemAPI.Time.DeltaTime`
7. 离散规则显式考虑 `SimulationStepBatchSize`
8. 不可回滚表现避免在历史重放 Tick 重复触发，至少检查 `IsFirstTimeFullyPredictingTick`
9. 预测 Ghost 查询必须尊重 `Simulate` Enabled State
10. UI 和纯表现计时优先使用 `UnscaledClientTime` 或普通 Unity 时间，不跟随预测时间回退

## 15. 调试时应该观察什么

`NetworkTime.ToFixedString()` 已经把关键字段整理为一行：

```text
ServerTick 与 Fraction
SimulationStepBatchSize
PredictedTickIndex / NumPredictedTicksExpected
InputTargetTick 与 EffectiveInputLatencyTicks
InterpolationTick 与预测到插值的距离
Prediction 和 CatchUp Flags
```

定位问题时建议同时观察：

- `NetworkSnapshotAck.EstimatedRTT` 与 `DeviationRTT`
- `ServerCommandAge / 256f`
- `LastReceivedSnapshotByLocal`
- `ServerTick.TicksSince(InterpolationTick)`
- `SimulationStepBatchSize`
- 每帧 `PredictedTickIndex` 最终值
- Snapshot 实际到达间隔和 Packet Loss

常见现象可按下面理解：

- `SimulationStepBatchSize > 1`：服务器或预测循环正在合并 Tick
- `PredictedTickIndex` 很高：本帧重放了很多预测 Tick
- `EffectiveInputLatencyTicks > 0`：预测窗口达到上限或主动配置了输入延迟
- 插值距离持续扩大：Snapshot 到达抖动、丢包或服务器发送频率下降
- 客户端 ServerTick 回退：时间估算出现超过阈值的大误差
- Command Age 持续为正：输入到达服务器偏晚，可能有网络、帧率或 Tick 标记问题

## 16. 阅读源码的最短路径

继续调试时间问题时，建议按以下顺序阅读：

1. [`NetworkTick.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTick.cs) 理解序号和回绕
2. [`NetworkTime.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTime.cs) 理解公开时间线与标记
3. [`ClientServerTickRate.cs`](../../../Packages/com.unity.netcode/Runtime/ClientServerWorld/ClientServerTickRate.cs) 理解全部配置
4. [`NetcodeTimeTracker.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeTimeTracker.cs) 理解服务端累加和 batching
5. [`NetworkTimeSystem.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/NetworkTimeSystem.cs) 理解客户端时间同步
6. [`NetcodeClientRateManager.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeClientRateManager.cs) 理解 Partial Tick
7. [`NetcodeClientPredictionRateManager.cs`](../../../Packages/com.unity.netcode/Runtime/PredictionTicking/UpdateRateManagement/NetcodeClientPredictionRateManager.cs) 理解回滚循环
8. [`ICommandData.cs`](../../../Packages/com.unity.netcode/Runtime/Command/ICommandData.cs) 和 [`CommandSendSystem.cs`](../../../Packages/com.unity.netcode/Runtime/Command/CommandSendSystem.cs) 理解输入 Tick
9. `EditorRateManagerTests.cs`、`InterpolationTests.cs` 和 `PredictionTests.cs` 核对边界行为
