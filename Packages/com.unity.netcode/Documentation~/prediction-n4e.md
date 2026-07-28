# 使用预测管理延迟

可以使用客户端预测减轻延迟对玩家体验的影响。概述请参阅[客户端预测页面](intro-to-prediction.md)

本页介绍如何在游戏中实现客户端预测

还需要注意一些[客户端预测边界情况](prediction-details.md)

<a id="prediction-in-netcode-for-entities"></a>
## Netcode for Entities 中的预测

预测只会对同时具有 [`PredictedGhost`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhost.html) 和 [`Simulate`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.Simulate.html) 组件的实体运行。Unity 会为客户端上的所有预测 Ghost 以及服务器上的所有 Ghost 添加 `PredictedGhost` 组件。在客户端上，该组件还包含预测所需的数据，例如已经应用到 Ghost 的快照

预测基于固定时间步循环，由 [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) 控制。该系统组同时在客户端和服务器上运行，通常包含确定性 Ghost 模拟的核心部分

预测涉及的主要 API 元素如下：

- `Simulate` 标签，用于筛选需要模拟的 Ghost
- `PredictedSimulationSystemGroup` 系统组，预测模拟应在其中运行。与 `FixedUpdate` 类似，它可以在一帧内运行多次
- `IInputComponentData` 接口，用于发送与 Tick 关联的输入
- `GhostAuthoringComponent` 上的 `HasOwner`、`AutoCommandTarget`、`SupportedGhostMode` 和 `DefaultGhostMode`，用于设置 Ghost 是否采用预测模式
- `NetworkTime` 单例
  - `NetworkTime.ServerTick` 表示最近的模拟 Tick，包括客户端预测 Tick 和服务器端 Tick。它会根据延迟领先最近收到的快照 Tick 一定距离
  - `NetworkTime.InterpolationTick` 表示当前客户端插值 Tick，通常落后于最近收到的快照 Tick
  - 上述两个 Tick 都有对应的 `XXFraction` 字段
  - `NetworkTime.IsPartialTick` 表示当前预测的 Tick 是否只是完整 Tick 的一部分。当渲染时序与固定步长模拟时序不一致时，该标志很有用
  - `NetworkTime.IsFirstTimeFullyPredictingTick` 是预测系统中常用的标志，用于保护只应执行一次的操作，例如实例化预测生成 Ghost，以及在客户端播放视觉和音效，使这些操作不会因重新模拟而重复发生

可将时间线大致理解为：`IT | | Snapshot | | | | | | | | | | ST`

<a id="client-side-predictedsimulationsystemgroup"></a>
## 客户端 `PredictedSimulationSystemGroup`

预测运行时，`PredictedSimulationSystemGroup` 会在 ECS `TimeData` 结构中设置当前预测 Tick 对应的正确时间。它还会把 [`NetworkTime`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html) 单例中的 `ServerTick` 设为正在预测的 Tick，并设置其他相关字段与辅助状态

> [!NOTE]
> 回滚和预测重新模拟可能给每帧带来显著开销
> 例如，在 300ms 延迟的连接上，预计每帧需要重新模拟约 22 帧。换言之，物理以及 `PredictedSimulationSystemGroup` 中的所有其他系统会在一个渲染帧内更新约 22 次
> 可以在 `PlayMode Tools Window` 中设置较高的模拟 Ping 来测试这一点
> 详细信息请参阅[优化](optimizations.md)页面

<a id="simulate-tag-partial-snapshots"></a>
## `Simulate` 标签与部分快照

Netcode for Entities 支持部分快照。如果 World 状态更新无法放入一个数据包，Netcode 会跨多个 Tick 传输状态，每份快照只包含部分实体。预测循环会从任意实体所应用的最旧 Tick 开始运行，而有些实体可能已经具有更新的数据，因此**必须检查每个实体是否需要模拟**。模拟标签在客户端和服务器上都会启用，因此两端可以复用同一份代码

有两种方法可以执行该检查，第二种仅为兼容旧代码而保留

<a id="check-which-entities-to-predict-using-the-simulate-tag-component-preferred"></a>
### 使用 `Simulate` 标签组件检查需要预测的实体，推荐方式

客户端使用所有 World 实体上都存在的 `Simulate` 标签，指定是否应预测某个 Ghost 实体

- 预测循环开始时，所有 `Predicted` Ghost 的 `Simulate` 标签都会被禁用
- 对于每个预测 Tick，需要在该 Tick 模拟的所有实体都会启用 `Simulate` 标签
- 预测循环结束时，系统保证所有预测 Ghost 实体的 `Simulate` 组件处于启用状态

在 `PredictedSimulationSystemGroup` 或其任意子组中运行的游戏系统，应向查询添加 `Simulate` 条件。对于已经弃用的 `Entities.ForEach` 使用 `.WithAll<Simulate>()`，对于惯用 `foreach` 查询同样使用 `.WithAll<Simulate>()`。这样 Job 或函数会自动获得当前需要处理的正确实体集合

例如：

```c#
Entities
    .WithAll<PredictedGhost, Simulate>()
    .ForEach((ref Translation translation) =>
{
    // 在此编写更新逻辑
});
```

<a id="check-which-entities-to-predict-using-the-predictedghostshouldpredict-helper-method-legacy"></a>
### 使用 `PredictedGhost.ShouldPredict` 检查需要预测的实体，旧方式

这是一种仍受支持但不推荐使用的旧检查方式。更新实体前，可以调用静态方法 [`PredictedGhost.ShouldPredict`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedGhost.html#Unity_NetCode_PredictedGhost_ShouldPredict_System_UInt32_)。更新实体的方法或 Job 大致如下：

```c#
var serverTick = GetSingleton<NetworkTime>().ServerTick;
Entities
    .WithAll<PredictedGhost, Simulate>()
    .ForEach((ref Translation translation) =>
{
    if (!PredictedGhost.ShouldPredict(serverTick))
        return;

    // 在此编写更新逻辑
});
```

如果某个实体自上次预测运行以来没有收到新的网络数据，并且上次预测以模拟完整 Tick 结束，那么预测会从上次结束的位置继续，而不会重新应用网络数据。使用动态时间步时，并不总是以完整 Tick 结束

<a id="server-simulation"></a>
## 服务器模拟

在服务器上，预测循环始终只运行一次，也不会更新 `TimeData` 结构，因为其中的时间已经正确。服务器上的模拟不是预测，而是正式、权威的游戏模拟。`NetworkTime` 单例中的 `ServerTick` 同样具有正确值，因此同一份代码可以同时在客户端和服务器上运行

因此，在服务器上调用 `PredictedGhost.ShouldPredict` 始终返回 true，`Simulate` 组件也始终处于启用状态

> [!NOTE]
> 对于预测玩法系统，只需编写一次代码，就能同时在客户端和服务器上工作，无需区分当前运行位置

<a id="remote-player-prediction"></a>
## 远程玩家预测

<a id="remote-player-prediction-with-iinputcomponentdata"></a>
### 使用 `IInputComponentData` 预测远程玩家

如果输入配置为序列化到其他玩家，参阅 [Ghost 快照](ghost-snapshots.md#icommanddata-and-iinputcomponentdata-serialization)，则可以使用远程玩家的输入对其执行客户端预测，方式与预测本地玩家相同

客户端收到新快照时，`PredictedSimulationSystemGroup` 会从任意实体所应用的最旧 Tick 开始运行，直到预测目标 Tick。每个实体需要预测的范围可能不同，因此始终必须只处理具有 `Simulate` 组件的实体，以检查该实体是否需要在特定 Tick 更新和应用输入

Netcode 会自动更新当前模拟 Tick 对应的输入数据

```c#
protected override void OnUpdate()
{
    Entities
        .WithAll<PredictedGhost, Simulate>()
        .ForEach((Entity entity, ref Translation translation, in MyInput input) =>
    {
        // 在此编写更新逻辑
    }).Run();
}
```

<a id="legacy-commands"></a>
### 旧命令 API

如果使用旧命令 API，需要自行检查或获取输入缓冲区

```c#
protected override void OnUpdate()
{
    var tick = GetSingleton<NetworkTime>().ServerTick;
    Entities
        .WithAll<Simulate>()
        .ForEach((Entity entity, ref Translation translation, in DynamicBuffer<MyInput> inputBuffer) =>
        {
            if (!inputBuffer.GetDataAtTick(tick, out var input))
                return;

            // 在此编写移动逻辑
        }).Run();
}
```
