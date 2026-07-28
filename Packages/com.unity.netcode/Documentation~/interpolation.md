# 插值与外推

在游戏中使用插值和外推，尽量减小不良网络状况对玩法的影响

网络游戏在不稳定或质量较差的网络上运行时，可能遇到延迟和抖动，从而影响玩家体验。插值和外推都是处理方法，目标是在玩家视角下尽量降低网络干扰的影响

本页讨论插值模式下的 Ghost。抖动也会影响预测 Ghost，但[预测](intro-to-prediction.md)会自行解决该问题

<a id="interpolation"></a>
## 插值

插值是对一组已知数据点范围内可能存在的数据点进行估算。在 Netcode for Entities 中，插值特指使用线性插值、[航点路径](#waypoint-pathing)和[缓冲插值](#buffered-interpolation)，在[快照](ghost-snapshots.md#snapshots)中收到的两个或更多已知值之间平滑过渡的过程

如果客户端渲染速率与模拟速率相同，客户端始终会渲染未经插值但仍经过缓冲的快照

<a id="waypoint-pathing"></a>
### 航点路径

航点路径是一种特定的移动或回放形式，实体在节点 `A`、`B` 和 `C` 之间执行线性插值：先从 `A` 移动到 `B`，再从 `B` 移动到 `C`。在 Netcode for Entities 中，每个航点节点都是一份已接收快照。收到的快照越多，插值 Ghost 的回放就越准确。该行为通过 [`ClientTickRate.InterpolationTimeMS`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_InterpolationTimeMS) 设置，它定义[插值缓冲区](#buffered-interpolation)的大小

<a id="buffered-interpolation"></a>
### 缓冲插值

缓冲插值会故意延迟 Tick，等待快照到达后再在快照之间进行插值。缓冲为延迟数据包提供了在数据被需要之前抵达的机会。在真实网络环境下，更大的缓冲窗口可以产生更准确的回放，但代价是增加额外延迟

<a id="extrapolation"></a>
## 外推

外推是对一组已知数据点范围之外可能存在的数据点进行估算。在 Netcode for Entities 中，外推实际上是不受限制的插值。如果目标快照值未及时收到，外推会使数值以相同速率沿相同方向继续变化

外推是一种基础估算形式，经常会出错，但通常仍优于完全不估算。请注意，外推仍有上限，不会永远持续。默认情况下，外推限制为 20 个 Tick；在默认 60Hz 模拟速率下，大约为三分之一秒。可以使用 [`ClientTickRate.MaxExtrapolationTimeSimTicks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_MaxExtrapolationTimeSimTicks) 属性调整该上限

航位推算一词也常在与外推相似的语境中使用，但也可能表示使用更复杂的逻辑猜测轨迹。Netcode for Entities 不使用航位推算

>[!NOTE]
>外推与[客户端预测](intro-to-prediction.md)并不相同。外推是在当前 `interpolationTime` 之前仍未收到快照数据时，应用于插值 Ghost 的简单线性数学运算；客户端预测则会执行复杂的玩法代码模拟，并根据客户端延迟进行调整，尝试复现服务器自身的玩法模拟。换言之，插值 Ghost 可以进行外推，但预测 Ghost 不可以。外推和预测运行在不同的[时间线](#timelines)上

<a id="timelines"></a>
## 时间线

客户端会同时在三条不同时间线上运行：

1. 客户端输入目标 Tick 时间线，即“现在”。它必须领先服务器，以确保轮询到的输入能及时发送到服务器并由服务器处理
2. [客户端预测](intro-to-prediction.md)时间线。客户端在此以非权威方式预测自身未来位置，通常与上述输入采集时间线相同，启用[强制输入延迟](optimizations.md#using-forcedinputlatencyticks)时除外
3. 插值时间线。它落后于服务器权威模拟时间线，因为它会回放已接收快照中的插值 `GhostField` 值，因此延迟为 `RTT/2 + InterpolationTimeNetTicks`。详细信息请参阅[时间同步页面](time-synchronization.md)

服务器端只有一条时间线：

* 服务器权威模拟时间线，即服务器视角下的“现在”

> [!NOTE]
> 服务器拥有当前时间、当前 Tick 以及时间推进速率的权威

因此，总共有四条时间线；如果不使用强制输入延迟，则为三条：

| 时间线                                        | 字段                                                                                                        | 时间线偏移（忽略 `deltaTime` 平滑）                         |
|-----------------------------------------------|-------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------|
| 客户端输入目标 Tick 时间线                   | `NetworkTime.InputTargetTick (ClientWorld)`                                                                | T + (RTT/2 + TargetCommandSlack)                            |
| 客户端预测时间线                             | `NetworkTime.ServerTick (ClientWorld)` &<br/>`NetworkTime.ServerTickFraction (ClientWorld)`                 | T + (RTT/2 + TargetCommandSlack) - ForcedInputLatencyTicks  |
| 服务器权威模拟时间线                         | `NetworkTime.ServerTick (ServerWorld)`                                                                     | T                                                           |
| 客户端插值时间线                             | `NetworkTime.InterpolationTick (ClientWorld)` &<br/>`NetworkTime.InterpolationTickFraction (ClientWorld)`  | T - (RTT/2 + InterpolationTimeNetTicks)                     |

![Timelines.jpg](images/PredictionSteps/Timelines.jpg)

<a id="interpolation-tick-fraction"></a>
### 插值 Tick 比例

`NetworkTime.InterpolationTickFraction` 表示客户端为抵达目标 `InterpolationTick` 而正在插值的进度比例。例如，`InterpolationTick` 为 11、比例为 0.5f，表示客户端当前正在 Tick 10 与 Tick 11 之间插值，并已进行到 Tick 11 的一半位置。这**并不**是 Tick 11.5f。换言之，`InterpolationTick` 是**目标** Tick，`InterpolationTickFraction` 是抵达目标 Tick 的**进度**

当 `InterpolationTickFraction` 为 1.0f 时，客户端已经位于目标 Tick。如果没有部分 Tick，`InterpolationTickFraction` 会始终为 1.0f。预测中的 `ServerTick` 与 `ServerTickFraction` 也遵循相同规则

![TickFraction.jpg](images/TickFraction.jpg)

## 其他资源

* [Ghost 与快照](ghost-snapshots.md)
* [使用 `GhostFieldAttribute` 进行序列化与同步](ghostfield-synchronize.md)
* [使用 `GhostComponentAttribute` 自定义复制行为](ghostcomponentattribute.md)
* [预测模式切换](prediction-switching.md)
* [生成 Ghost 与预生成 Ghost](ghost-spawning.md)
* [物理](physics.md#interpolated-ghosts)
* [时间同步](time-synchronization.md)
* [预测平滑](prediction-smoothing.md)
