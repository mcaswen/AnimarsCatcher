# 预测简介

>需要解决的问题：延迟、安全性与一致性

网络游戏通过互联网传输数据时不可避免地会产生[延迟](https://docs-multiplayer.unity3d.com/netcode/current/learn/lagandpacketloss/)。客户端向服务器发送输入（例如移动角色）后，如果必须等服务器处理输入、返回结果，再把结果显示出来，玩家就会感到[操作延迟且缺乏响应，仿佛角色带有惯性](https://docs-multiplayer.unity3d.com/netcode/current/learn/dealing-with-latency/#tldr)

![延迟问题](images/IssueWithLag.jpg)

缓解延迟的一种方式是采用客户端权威：允许客户端自行模拟玩法并将结果发送给服务器，服务器直接信任客户端提供的状态。但这种方式容易受到作弊攻击，例如传送、飞行，甚至可以传入 `NaN` 令服务器崩溃，或提交越界值

客户端权威还会形成分布式模拟。当不同客户端对游戏状态意见不一致时，需要额外的[冲突解决逻辑](https://docs-multiplayer.unity3d.com/netcode/current/learn/dealing-with-latency/#issue-world-consistency)。例如，两名玩家可能都声称自己的角色正坐在同一个座位上

为了在保持服务器权威的同时获得及时响应，Netcode for Entities 采用客户端预测

<a id="client-prediction"></a>

## 客户端预测

客户端预测允许客户端使用自身输入在本地模拟游戏，无需等待服务器的模拟结果。这里的“预测”并不是猜测未来，而是客户端用自身输入预测服务器将得到的模拟结果；客户端和服务器会运行相同的模拟代码。可以把它理解为“先执行，出错后再由服务器纠正”

使用客户端预测时，客户端会在等待服务器快照期间持续进行本地模拟。每次收到快照后，它先把游戏状态修正为服务器的权威状态，再重新模拟到自身的“当前时刻”，并从中断位置继续运行。由于网络延迟，服务器快照通常比客户端当前时间线落后若干 Tick，因此客户端每次收到快照后，都必须从较早的快照 Tick 重新模拟到当前 Tick。详细过程请参阅[客户端预测时序](#client-prediction-sequence)

这种方式能让客户端在产生输入的同一帧看到结果，同时仍由单一服务器维护安全的权威模拟，因此响应性很好

<a id="mispredictions"></a>

### 错误预测

客户端收到快照时，如果服务器状态与客户端预测状态一致，画面不会发生变化，客户端会继续正常模拟。如果两种状态存在差异，就会发生修正；客户端从快照状态开始的即时重演将得到不同结果。这称为错误预测，可以使用[预测平滑](prediction-smoothing.md)缓解

<a id="cpu-usage"></a>

### CPU 开销

客户端预测是一种 CPU 开销较高的延迟处理方式。客户端每次收到快照后，都必须在同一帧处理多个模拟 Tick，从快照 Tick 重新模拟到当前 Tick。无论客户端与服务器的模拟是否真的存在差异，这项工作都会在收到服务器快照时发生

降低 `NetworkTickRate` 可以减少客户端预测的开销，但较低的 Tick 率也会增加延迟

<a id="client-prediction-sequence"></a>

### 客户端预测时序

整体流程如下：

![预测时序图](images/PredictionSequenceDiagram.jpg)

逐步说明：

1. 客户端模拟自身输入并运行玩法逻辑
   ![预测步骤 1](images/PredictionSteps/Prediction1.jpg)
2. 客户端把输入发送给服务器
   ![预测步骤 2](images/PredictionSteps/Prediction2.jpg)
3. 客户端通过 Command Slack 配置为领先服务器若干 Tick。服务器收到输入后，将其放入等待队列
   ![预测步骤 3](images/PredictionSteps/Prediction3.jpg)
4. 服务器模拟该输入
   ![预测步骤 4](images/PredictionSteps/Prediction4.jpg)
5. 服务器把结果发送给客户端。在此期间客户端仍在继续模拟，此时已经到达 Tick 17
   ![预测步骤 5](images/PredictionSteps/Prediction5.jpg)
6. 客户端收到结果，把模拟回滚到 Tick 10，再重演输入直到 Tick 17。整个过程在一次更新内完成，因此玩家看不到重演过程
   ![预测步骤 6](images/PredictionSteps/Prediction6.jpg)

<a id="the-prediction-loop"></a>

## 预测循环

<a id="server"></a>

### 服务器

服务器拥有最终玩法决策权。使用专用服务器时，这可以防止作弊；同时以单一事实来源替代多个权威之间的协调，改善游戏状态的一致性

服务器以固定的“模拟 Tick 率”运行游戏模拟，参阅 [`ClientServerTickRate.SimulationTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_SimulationTickRate)

模拟本身不必完全确定，但仍应尽量减少非确定性，从而降低修正次数。不同机器上的模拟出现差异时，客户端会根据服务器后续更新持续自我修正，最终使客户端状态与服务器状态一致

服务器以固定的[网络 Tick 率](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#Unity_NetCode_ClientServerTickRate_NetworkTickRate)向客户端发送模拟快照。根据 World 规模，发送的也可能是[部分快照](ghost-snapshots.md#under-the-hood-partial-snapshots)

`SimulationTickRate` 与 `NetworkTickRate` 可以不同，默认值均为 60 Hz；但 `NetworkTickRate` 必须小于或等于 `SimulationTickRate`，并且 `SimulationTickRate` 必须能被它整除。例如，网络 Tick 率为 30 Hz，模拟 Tick 率为 60 Hz

服务器仍然可以在每个模拟 Tick 发送数据，只是每次发送给不同的客户端子集。这样能把 CPU 负载分散到多个模拟 Tick，避免尖峰。例如，当网络 Tick 率为 30 Hz、模拟 Tick 率为 60 Hz 时，服务器可以在一个 Tick 向一半客户端发送快照，下一个 Tick 再向另一半发送。每个客户端仍然每两个模拟 Tick 收到一个数据包，而服务器的 CPU 负载均匀分布在每个 Tick

<a id="client"></a>

### 客户端

客户端会尝试对所有客户端预测 Ghost 运行与服务器相同的模拟。不过客户端渲染帧率可变，因此实际客户端模拟 Tick 率与服务器略有不同，参阅[部分 Tick](#partial-ticks)。为简化说明，以下示例假设客户端与服务器的运行方式完全一致

为了让服务器在下一次更新前收到输入，客户端模拟需要领先服务器，使输入所属 Tick 与服务器之后处理它时的当前模拟 Tick 对齐。这意味着客户端会使用自身输入，预测比服务器时间线稍靠前的 World 状态

客户端领先服务器的 Tick 数取决于网络往返时间（RTT）和 `slack` Tick 数，参阅 [Target Command Slack](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_TargetCommandSlack)。默认 Slack 为两个 Tick

<a id="example"></a>

#### 示例

- 现实时间为午夜 `00:00:00.000`，服务器位于 Tick 10，RTT 为 200 ms，模拟频率为每秒 60 Tick，此时客户端大约正在模拟 Tick 18
- 客户端向服务器发送 Tick 18 的输入
- 大约在 `00:00:00.100`，也就是半个 RTT 后，服务器应当已经推进到 Tick 16 左右。服务器收到 Tick 18 的输入，并将其放入等待队列
- 在 `00:00:00.132`，服务器到达 Tick 18，使用客户端此前发送的 Tick 18 输入

使用 [Command Slack](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_TargetCommandSlack)可以有效吸收少量网络抖动，帮助服务器及时收到输入

![Netcode for Entities 预测循环](images/NetcodeForEntitiesPredictionLoop.jpg)
<!--
TODO：增加一条“小于等于物理模拟次数”的说明
TODO：替换成更清晰的版本
-->

<a id="simulation-groups"></a>

#### 模拟系统组

客户端和服务器上都存在 `PredictedSimulationSystemGroup`，它负责决定何时以及如何运行内部系统与系统组。除少数例外，该组中的所有系统共同构成游戏的近似确定性模拟步骤，并且应在客户端和服务器上同时运行

当项目同时安装 Physics 和 Netcode 包时，物理更新会自动移入预测循环。物理更新还具有额外一级更新频率：`PredictedFixedStepSimulationSystemGroup` 可以在每个模拟 Tick 内多次执行物理步骤。换言之，物理 Tick 率可以高于模拟 Tick 率，但不能低于它

<a id="predicted-and-owner-predicted-modes"></a>

### Predicted 与 Owner Predicted 模式

Netcode 提供两种客户端预测模式：

- Predicted：所有客户端都尝试预测指定 Ghost
- Owner Predicted：只有拥有该 Ghost 的客户端进行预测，其他客户端对它进行插值

以玩家角色为例，使用 Predicted 时，其他客户端也会在自己的模拟中尝试预测你的角色；使用 Owner Predicted 时，只有你自己的客户端预测它，其他客户端只做插值

在 Predicted 模式下，可以启用输入复制来预测其他玩家，即在输入状态上添加 `[GhostField]`。输入会被复制到其他客户端，其他客户端据此执行以下操作：

1. 对于存在对应输入的 Tick，正常使用该输入进行模拟
2. 对于缺少输入的 Tick，使用本地输入历史中的最后一份输入

<a id="rollback-and-replay"></a>

### 回滚与重演

回滚与重演也称为修正与协调。客户端收到包含预测 Ghost 的服务器快照后，会先把它缓存在内部 `SnapshotBuffer` 中，因为数据包可能在任意时刻到达。下一帧中，`GhostUpdateSystem` 会把新状态应用到相应 Ghost

系统只会回滚本次快照中收到更新的 Ghost，而不是整个模拟。这是接收[部分快照](ghost-snapshots.md#partial-snapshots)时采用的选择性回滚策略。Ghost 会通过 `Simulate` 标签表明是否应参与模拟，因此实体查询必须包含该标签

`PredictedSimulationSystemGroup` 会计算重新模拟应从哪个 Tick 开始，然后重新计算直到当前服务器 Tick 的所有 Tick

该循环在逻辑上分为两部分：

- 使用固定 Delta Time（`1 / SimulationTickRate`）模拟 Tick 的循环
- 最后一次模拟更新，用可变步长模拟下一个或当前服务器 Tick；这可能是一个[部分 Tick](#partial-ticks)

![Netcode for Entities 重演循环](images/NetcodeForEntitiesReplayLoop.jpg)

可以通过 `var networkTime = SystemAPI.GetSingleton<NetworkTime>()` 获取 [`NetworkTime`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime) 的标志，例如 `networkTime.IsFirstTimeFullyPredictingTick`

上图的 **PREDICT CURRENT TICK** 步骤会在客户端和服务器上把 `networkTime.ServerTick` 设置为最近模拟的 Tick

请注意，[部分 Tick](#partial-ticks)也会回滚到上一个完整 Tick

<a id="partial-ticks"></a>

### 部分 Tick

客户端可以采用可变帧率，让画面尽可能平滑。客户端仍以与服务器相同的固定频率执行模拟中的确定性部分，但可以在完整 Tick 之间执行部分 Tick 来平滑表现。例如，客户端以 120 FPS 渲染、以每秒 30 Tick 模拟时，可以依次执行一个完整 Tick、三个部分 Tick、下一个完整 Tick，如此循环

- 确定性 Tick 称为“完整 Tick”
  - 完整 Tick 的 `deltaTime` 始终为 `1 / SimulationTickRate`
- 在完整 Tick 之间执行的 Tick 称为“部分 Tick”
  - 部分 Tick 的 `deltaTime` 可变

为了让部分 Tick 按客户端帧率更新，Netcode 会先将部分 Tick 回滚到最近一次预测的完整 Tick，再以适当的 `deltaTime` 模拟一次部分 Tick。需要模拟完整 Tick 时，也会先执行相同的回滚，但此时使用的 `deltaTime` 为 `1 / SimulationTickRate`。`NetworkTime.ServerTick`、DOTS 的 `World.Time` 和 `deltaTime` 都会相应更新

#### 示例

假设模拟频率为 60 Hz，每个完整 Tick 约 16 ms，而客户端实际帧率约为 240 Hz：

- 客户端位于 Tick 10，并保存了该 Tick 的本地状态备份
- 下一帧位于 Tick 10 到 11 之间的 0.1 位置
  - 客户端采样输入
  - 客户端从 10 模拟到 10.1，`deltaTime` 约为 2 ms
- 下一帧位于 Tick 10 到 11 之间的 0.3 位置
  - 客户端采样输入
  - 客户端从 10 模拟到 10.3，`deltaTime` 约为 5 ms
  - 客户端会再次回滚到完整 Tick 10，再模拟部分 Tick 10.3，而不是从 10.1 模拟到 10.3，因为使用一致的基线有助于提高确定性
- 下一帧位于 Tick 10 到 11 之间的 0.7 位置
  - 客户端采样输入
  - 客户端从 10 模拟到 10.7，`deltaTime` 约为 11 ms
- 下一帧位于 Tick 11 到 12 之间的 0.2 位置，系统检测到完整 Tick 已变化
  - 客户端采样输入
  - 客户端向服务器发送累计输入
  - Netcode 提供了 [InputEvent](command-stream.md#input-events)，用于确保部分 Tick 期间的原子输入不会丢失。其他输入值会由最新值覆盖
  - 客户端以 16 ms 的 `deltaTime` 完整模拟 Tick 10 到 11
  - 备份 Tick 11 的状态
  - 客户端从 11 模拟到 11.2，`deltaTime` 约为 3 ms
  - 这里实际执行两步模拟：从 10 到 11，再从 11 到 11.2

当部分 Tick 的 `deltaTime` 落在完整 Tick 时长的正负 5% 范围内时，它会舍入到最近的完整 Tick

![部分 Tick](images/PredictionSteps/PartialTicks.jpg)

![包含部分 Tick 的预测循环](images/NetcodeForEntitiesPredictionLoopPartialTicks.jpg)

服务器上的对应过程：

- 服务器位于 Tick 10
  - 获取所有客户端的 Tick 10 输入；如果没有收到，则使用最后一次收到的输入
  - 完整模拟 Tick 10 到 11

<a id="behind-the-scenes"></a>

#### 内部过程

`GhostUpdateSystem` 使用最近一个完整 Tick 的备份恢复预测 Ghost 状态。`PredictedSimulationSystemGroup` 使用自上次完整模拟 Tick 以来累计的 `deltaTime` 运行模拟更新。如果累计时间超过模拟间隔，客户端会执行一个完整模拟 Tick，再用剩余 `deltaTime` 继续下一个 Tick

系统会像模拟回滚与重演期间一样，按需设置 `NetworkTime` 标志

- 仅执行部分 Tick 时，`FirstPredictionTick` 与 `IsFinalPredictionTick` 指向同一个 Tick，且 `IsPartialTick` 为 `true`
- 需要模拟一个或多个完整 Tick 时，`FirstPredictionTick` 指向首次执行完整重新模拟的 Tick

<a id="batching-and-catch-up"></a>

### 批处理与追赶

如果客户端或服务器因性能问题无法按指定频率模拟所有必要 Tick，也就是实际 `deltaTime` 大于目标 `deltaTime`，Netcode 会尝试把多个 Tick 批量合并，以较少的 Tick 和较大的 `deltaTime` 运行。配置方法请参阅[服务器固定更新循环](client-server-worlds.md#configuring-the-server-fixed-update-loop)

为了尽量保持确定性，如果两个 Tick 之间的输入发生变化，Netcode 不会把它们合并。例如，Tick 10、11、12 的输入均为 `FOO=1`，Tick 13、14、15 变为 `FOO=2` 时，Netcode 可以分别合并 Tick 10 到 12 或 Tick 13 到 15，但不会把 Tick 12 和 13 合并

<!--
### 空闲帧

[单 World 主机](client-server-worlds.md)可能出现不执行任何 Netcode 模拟代码的空闲帧。这是因为客户端以可变帧率呈现画面，但以固定 Tick 率运行 Netcode 模拟

> [!NOTE] `InitializationSystemGroup`、`SimulationSystemGroup` 和 `PresentationSystemGroup` 仍会在空闲帧运行，但 Netcode for Entities 定义的系统组不会运行，参阅 `NetcodeHostRateManager`

可以[利用空闲帧优化项目性能](optimization/off-frame.md)。例如，表现相关系统仍可在空闲帧执行。`ServerTick` 是上一次实际执行 Tick 时计算的 Tick，`InputTargetTick` 则是下一个 Tick，因为系统正在收集下一次 Netcode 模拟 Tick 的输入
-->

<a id="multiple-timelines"></a>

## 多条时间线

客户端同时存在插值时间线和预测时间线，两者行为不同。详情请参阅以下文档：

- 插值页面中的[时间线](interpolation.md#timelines)
- 更具体的实现细节参阅[时间同步](time-synchronization.md#the-networktimesystem)
