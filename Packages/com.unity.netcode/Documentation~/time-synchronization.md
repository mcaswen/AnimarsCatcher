# 时间同步

Netcode 使用服务器权威模型，即服务器根据距上次更新所经过的时间执行固定时间步
因此，为使该模型正常工作，客户端需要始终与服务器时间保持一致

## NetworkTimeSystem

[NetworkTimeSystem](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTimeSystem.html) 负责计算应在客户端呈现的服务器时间
网络时间系统会根据往返时间和服务器最近发送的快照，计算服务器时间的初始估计值
客户端获得初始估计后，会对时间推进进行小幅调整，而不是大幅改变当前时间
为了准确调整，服务器会跟踪命令被使用前在缓冲区中保留了多长时间
该信息会发回客户端，客户端据此调整自身时间，使命令在服务器需要使用前刚好抵达

客户端向服务器发送命令，这些命令会在未来某个时刻抵达。服务器收到命令后，使用它们运行游戏模拟
客户端需要估算服务器会在哪个 Tick 上应用这些命令并呈现该 Tick，否则客户端与服务器会在不同模拟步骤应用输入

客户端估算服务器将应用命令的 Tick 称为**预测 Tick**。预测时间只应当用于本地玩家等预测对象

对于插值对象，客户端应呈现已有接收数据所对应的状态。该时间称为**插值 Tick**。`插值 Tick` 以相对于`预测 Tick` 的偏移量计算
该时间偏移称为**预测延迟** <br/>
`插值延迟` 会综合考虑往返时间、抖动和数据包到达率，这些数据通常都能在客户端获得
系统还会根据网络 Tick 率增加一段额外时间，以确保能够承受一定程度的数据包丢失。可以在快照可视化工具 [Network Debugger](ghost-snapshots#Snapshot-visualization-tool) 中查看时间偏移与缩放

`NetworkTimeSystem` 会以较小增量缓慢调整`预测 Tick`和`插值延迟`，使其平滑推进，并确保插值 Tick 与预测 Tick 都不会倒退

### 配置客户端插值

客户端 World 中的 [ClientTickRate](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html) 单例实体可用于配置系统估算预测 Tick 和插值延迟的方式

| 参数                         | 说明                                                                                                                                                                                                                                                     |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| InterpolationTimeNetTicks    | 用作插值 Ghost 缓冲区的模拟 Tick 数量                                                                                                                                                                                                                    |
| MaxExtrapolationTimeSimTicks | 数据缺失时，客户端最多可向前外推的模拟 Tick 数                                                                                                                                                                                                           |
| MaxPredictAheadTimeMS        | 可接受的最大 Ping。客户端计算服务器 Tick 时会将 RTT 限制在该值以内，因此当 Ping 更高时，服务器会收到过时命令 <br/>增大该值可使客户端处理更高 Ping，但客户端会运行更多预测步骤并消耗更多 CPU 时间                                                       |
| TargetCommandSlack           | 客户端尝试确保命令在服务器使用前已经抵达的模拟 Tick 数量                                                                                                                                                                                                 |

还可以进一步自定义客户端时间计算。详细信息请参阅 [ClientTickRate](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html) 文档

## 在应用中获取时间信息

Netcode for Entities 提供 [NetworkTime](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) 单例，
应用应使用它获取当前模拟/预测服务器 Tick、插值 Tick 以及其他时间相关属性

```csharp
var networkTime = SystemAPI.GetSingleton<NetworkTime>();
var currentTick = networkTime.ServerTick;
...
```

无论在客户端还是服务器、预测循环内还是预测循环外，都可以使用 `NetworkTime` <br/>
对于预测循环，`NetworkTime` 会为当前模拟 Tick 添加一些标志，可用于实现特定逻辑，例如：

- IsFirstPredictionTick：当前服务器 Tick 是从该实体最近收到的快照开始预测的第一个 Tick
- IsFinalPredictionTick：当前服务器 Tick 是本次预测的最后一个 Tick
- IsFirstTimeFullyPredictingTick：当前服务器 Tick 是完整 Tick，并且这是它第一次作为非部分 Tick 被预测，适合实现只能执行一次的操作

此外还有许多其他标志。详细信息请参阅 [NetworkTime 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html)

## 客户端 `DeltaTime`、`ElapsedTime` 与 `Unscaled` 时间

客户端连接服务器后，经过的 `DeltaTime` 和总 `ElapsedTime` 会以不同方式处理。客户端需要使预测 Tick 与服务器保持同步，因此应用感知到的 `DeltaTime` 会被放大或缩小，从而加快或减慢模拟

这种时间缩放还会产生以下影响：

- 对于在 `SimulationSystemGroup` 及其子组中更新的所有系统，`Time.DeltaTime` 和 `Time.ElapsedTime` 反映缩放后的经过时间
- 对于在 `PresentationSystemGroup`、`InitializationSystemGroup` 或通常在 `SimulationSystemGroup` 外更新的系统，报告的是应用循环正常提供的时间

因此，在模拟组内部与外部看到的 `Time.ElapsedTime` 通常不同

如果需要在 `SimulationSystemGroup` 内访问真实、未经缩放的增量时间和经过时间，可以使用 [`UnscaledClientTime`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) 单例

`UnscaledClientTime.DeltaTime` 和 `UnscaledClientTime.ElapsedTime` 中的值就是应用循环正常报告的值
