# 物理

Netcode for Entities 集成了 [Unity Physics](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/index.html)，便于在网络游戏中使用物理系统。该集成既能处理包含物理组件的插值 Ghost，也支持包含物理组件的预测 Ghost

默认情况下无需配置即可使用此集成，但它假设所有动态物理对象都是 Ghost。这些对象可以由服务器完整模拟，再以插值 Ghost 的形式同步给客户端；也可以由服务器和客户端共同模拟，由客户端按预测 Tick 或服务器 Tick 向前模拟，服务器再修正预测误差。两类 Ghost 可以混合使用。如果某些对象只需要在本地运行物理模拟，则需要额外配置

物理 Ghost 配置检查表：

| `GhostAuthoringComponent` | `Rigidbody` | 是否静态 | 是否位于独立物理世界 | 结果 |
|---|---|---|---|---|
| 有 | 有 | 均可 | 否 | 有效 |
| 无 | 有 | 否 | 是 | 有效 |
| 不要求 | 无 | 是 | 均可 | 有效 |
| 有 | 均可 | 是 | 均可 | 有效 |
| 无 | 位于 Ghost 子对象上 | 否 | 均可 | 错误 |
| 无 | 有 | 否 | 否 | 错误 |

**重要**：当前 Netcode 至少需要场景中存在一个预测 Ghost，物理系统才会运行。满足该条件后，预测更新循环才会执行并驱动物理循环

<a id="interpolated-ghosts"></a>

## 插值 Ghost

对于插值 Ghost，物理模拟只能在服务器上运行。客户端上 Ghost 的位置和旋转由服务器快照控制，因此客户端不应对插值 Ghost 运行物理模拟

为确保这一点，Netcode 会在每帧开始时禁用客户端上相应 Ghost 实体的 `Simulate` 组件标签。这样会将物理对象视为运动学对象，使其不受物理模拟推动

具体行为如下：

- 忽略 `PhysicsVelocity`，并将其设为零
- 保留 `Translation` 和 `Rotation`

<a id="predicted-ghosts-and-physics-simulation"></a>

## 预测 Ghost 与物理模拟

预测物理是指客户端在预测循环内运行物理模拟。客户端可能需要从最后一次收到快照的 Tick 开始，在一次更新中执行多次模拟；服务器则照常运行物理模拟

初始化期间，Netcode 会把 `PhysicsSystemGroup` 以及 `FixedStepSimulationSystemGroup` 中的所有系统移入 `PredictedFixedStepSimulationSystemGroup`。该组是 `FixedStepSimulationSystemGroup` 的预测版本，因此可能按所需预测 Tick 数多次执行。只有 World 中实际存在动态预测物理 Ghost 时，这些组才会更新

所有带物理组件的动态预测 Ghost 都以这种方式参与模拟。与插值 Ghost 类似，每个预测帧开始时会根据需要启用或禁用 `Simulate` 标签，但此处可能需要执行多个模拟步骤

物理模拟通常占用较多 CPU。当一帧需要多次运行物理模拟时，开销可能持续累积：为了追赶多个待预测帧，固定时间步会落后于模拟 Tick 率，随后又需要在一帧内运行更多 Tick，使情况进一步恶化。在服务器上，可以启用 [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html) 的模拟批处理，相关选项为 `MaxSimulationStepBatchSize` 和 `MaxSimulationStepsPerFrame`。客户端的预测批处理选项位于 [`ClientTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTickRate.html)，包括 `MaxPredictionStepBatchSizeFirstTimeTick` 和 `MaxPredictionStepBatchSizeRepeatedTick`。但启用批处理会提高错误预测的概率

默认情况下，变换和速度的[量化](compression.md#quantization)精度为 1000。该精度通常足够，但仍会造成模拟差异，从而产生可见修正或抖动。为物理 Ghost [提高量化精度](ghostfield-synchronize.md#customizing-ghostfieldattribute-serialization)可以让模拟更加精确，但会消耗更多带宽

<a id="using-lag-compensation-predicted-collision-worlds"></a>

### 使用延迟补偿预测碰撞世界

使用预测物理时，客户端看到的预测物理对象与服务器看到的正确权威状态会略有不同，因为客户端会向前预测对象在当前服务器 Tick 上的位置。与这些物理对象交互时，可以使用延迟补偿系统，让服务器查询客户端在特定 Tick 看到的碰撞世界。例如，服务器可以据此更准确地判断客户端是否命中了某个碰撞体

在 [`NetCodePhysicsConfig`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetCodePhysicsConfig.html) 组件中勾选 `EnableLagCompensation` 即可启用该功能。随后可以使用 [`PhysicsWorldHistorySingleton`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PhysicsWorldHistorySingleton.html) 查询特定 Tick 对应的碰撞世界

<a id="requirements-for-predicted-physics"></a>

## 预测物理的运行条件

`PhysicsSystemGroup` 会在 World 首次更新开始时移入 `PredictedFixedStepSimulationSystemGroup`，因此之后只有 `PredictedFixedStepSimulationSystemGroup` 更新时，`PhysicsSystemGroup` 才会更新

`PredictedFixedStepSimulationSystemGroup` 属于 `PredictedSimulationSystemGroup`，而后者要求满足以下条件：

- World 中必须存在 `NetworkStreamInGame` 单例
- World 中必须存在预测 Ghost

Netcode for Entities 为 `PredictedFixedStepSimulationSystemGroup` 和 `PhysicsSystemGroup` 指定的 `RateManager` 还会施加以下要求：

- 系统必须以固定 Tick 率运行
- 除非配置为使用高于 `ClientServerTickRate.SimulationTickRate` 的 Tick 率，否则物理系统不会在[部分 Tick](intro-to-prediction.md#partial-ticks) 中运行
- 必须存在包含 `PhysicsVelocity` 组件的运动学实体，或者必须启用延迟补偿

任一条件在某个时刻不满足时，`PhysicsSystemGroup` 都不会更新

<a id="mitigating-entities-and-lag-compensation-requirements"></a>

### 放宽实体与延迟补偿条件

某些情况下，即使场景中没有预测实体，也需要运行物理模拟。例如，场景只有插值 Ghost，但仍需要向地面执行射线检测

此时可以配置 `ClientTickRate.PredictionLoopUpdateMode` 和 `NetCodePhysicsConfig.PhysicGroupRunMode`，在不完全满足上述[运行条件](#requirements-for-predicted-physics)时继续运行物理模拟

| `PredictionLoopUpdateMode` | `PhysicGroupRunMode` | 实际运行条件 |
|---|---|---|
| `RequirePredictedGhost`（默认） | `LagCompensationEnabledOrKinematicGhosts`（默认） | 固定 Tick 率、存在预测 Ghost，并且存在运动学实体或已启用延迟补偿 |
| `RequirePredictedGhost`（默认） | `LagCompensationEnabledOrAnyPhysicsEntities` | 固定 Tick 率、存在预测 Ghost，并且存在碰撞体或已启用延迟补偿 |
| `RequirePredictedGhost`（默认） | `AlwaysRun` | 固定 Tick 率且存在预测 Ghost |
| `AlwaysRun` | `LagCompensationEnabledOrKinematicGhosts`（默认） | 固定 Tick 率，并且存在运动学实体或已启用延迟补偿 |
| `AlwaysRun` | `LagCompensationEnabledOrAnyPhysicsEntities` | 固定 Tick 率，并且存在碰撞体或已启用延迟补偿 |
| `AlwaysRun` | `AlwaysRun` | 仅要求固定 Tick 率 |

<a id="client-only-physics-simulation-with-multiple-physics-worlds"></a>

## 使用多个物理世界运行仅客户端物理模拟

预测模拟默认即可工作，World 中的一般物理对象都应当是可复制的 Ghost。如果视觉效果、粒子或其他无需复制的物理交互只需要在客户端运行，则需要创建另一个物理世界

默认索引为 0 的主物理世界是预测物理世界。实现自定义物理系统组并为其指定新的物理世界索引，即可创建仅客户端物理世界

<a id="set-up-the-multi-physics-world"></a>

### 配置多物理世界

<a id="authoring-setup"></a>

#### Authoring 配置

1. 在 SubScene 中添加 `NetcodePhysicsConfig` 组件
2. 将 **Client Non Ghost World** 设为非 0 值
3. 为每个需要在仅客户端物理世界中模拟的物理 GameObject 添加 [`PhysicsWorldIndex`](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/api/Unity.Physics.PhysicsWorldIndex.html) 组件
4. 将 World 索引设为步骤 2 中使用的值

在实体烘焙过程中，所有物理实体都会获得 `PhysicsWorldIndex` 共享组件，用于指明该实体属于哪个物理世界模拟

<a id="code-setup"></a>

#### 代码配置

创建用于模拟指定 `PhysicsWorldIndex` 的次级物理 World 组：

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
public partial class VisualizationPhysicsSystemGroup : CustomPhysicsSystemGroup
{
    // 该值应与 Client Non Ghost World 属性中设置的值一致
    public const int WorldToSimulate = 1;

    public VisualizationPhysicsSystemGroup() : base(WorldToSimulate, true)
    {
    }

    protected override void OnCreate()
    {
        base.OnCreate();
        // 在这里执行其他初始化
    }
}
```

传给自定义类构造函数的两个参数分别是 World 索引，以及是否与主物理世界共享静态碰撞体

按这种方式配置的物理模拟会照常在 `FixedStepSimulationSystemGroup` 中运行。有关 `CustomPhysicsSystemGroup` 的更多信息，请参阅 [Unity Physics 文档](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/manual/)

两套模拟可以采用不同的固定时间步，也不要求彼此同步。因此同一帧中可能两者都执行，也可能只独立执行其中一个。预测模拟发生回滚时，可能在同一帧内按每个回滚 Tick 各执行一次；仅客户端模拟则仍然只照常执行一次

> [!NOTE]
> 使用者需要正确配置 Prefab，确保它在目标物理世界中运行。Unity Physics 包提供的 `PhysicsWorldIndexAuthoring` 组件可为刚体设置物理世界索引。更多信息请参阅 [Unity Physics 文档](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/manual/)

<a id="customphysicssystemgroup-update-requirement"></a>

### `CustomPhysicsSystemGroup` 的更新条件

使用多个物理世界时，任何 `CustomPhysicsSystemGroup` 开始更新之前，`PhysicsSystemGroup` 至少需要运行一次

这是因为 `Unity.Physics.SimulationSingleton.Type` 必须先由物理系统内部设置为非 `SimulationType.NoPhysics` 的值

更多信息请参阅[预测物理的运行条件](#requirements-for-predicted-physics)

<a id="interaction-between-predicted-and-client-only-physics-entities"></a>

### 预测物理实体与仅客户端物理实体之间的交互

有时需要让 Ghost 与仅存在于客户端的物理对象交互，例如碎片。但它们属于不同的物理模拟分区，无法直接相互作用。Physics 包为此提供了基于 `Custom Physics Proxy` 实体的工作流

对于预测物理世界中需要与仅客户端物理世界交互的每个物理实体，都要添加 `CustomPhysicsProxyAuthoring` 组件。烘焙过程会自动创建代理实体，为其添加必要的物理组件（`PhysicsBody`、`PhysicsMass`、`PhysicsVelocity`）以及指向根 Ghost 实体的 [`CustomPhysicsProxyDriver`](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/api/Unity.Physics.CustomPhysicsProxyDriver.html)。烘焙过程还会复制 Ghost 的碰撞体，并把代理物理刚体配置为运动学对象

预测世界中模拟的 Ghost 实体会驱动该代理：系统复制必要的组件数据，并设置物理速度，使代理能够在仅客户端世界中移动并与其他物理实体交互

Ghost 代理的位置和旋转由 [`SyncCustomPhysicsProxySystem`](https://docs.unity3d.com/Packages/com.unity.physics@latest/index.html?subfolder=/api/Unity.Physics.Systems.SyncCustomPhysicsProxySystem.html) 自动处理。默认情况下，系统会修改 `PhysicsVelocity`，通过运动学速度移动该实体。可以设置 Prefab 上的 `GenerateGhostPhysicsProxy.DriveMode` 组件属性来更改默认行为，也可以在运行时设置 `PhysicsProxyGhostDriver.driveMode` 动态切换驱动模式

<a id="custom-client-physics"></a>

## 自定义客户端物理

以下情况可能需要自定义客户端物理模拟方式：

- 客户端不需要预测物理
- 需要在建立连接之前运行物理模拟
- 需要在连接实体添加 `NetworkStreamInGame` 之前运行物理模拟

例如：

- Lobby 尚未连接服务器
- 已建立连接，但该连接始终不会开始流式传输 Ghost，因此也不会运行 `PredictedSimulationSystemGroup`
- 某项模拟只在服务器上运行

此时可以在客户端手动[禁用预测物理](#disable-predicted-physics)

<a id="disable-predicted-physics"></a>

### 禁用预测物理

不需要在 `PredictedSimulationSystemGroup` 内运行物理模拟时，可以手动禁用 `PredictedPhysicsConfigSystem`

```csharp
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(PredictedPhysicsConfigSystem))]
public partial struct DisablePhysicsInitializationIfNotConnect : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.World.GetExistingSystemManaged<PredictedPhysicsConfigSystem>().Enabled = false;
    }
}
```

该系统被禁用后，`PhysicsSystemGroup` 会继续照常在 `FixedStepSimulationSystemGroup` 内更新

<a id="enabling-multi-physics-worlds-without-connection-or-predicted-ghosts"></a>

### 在没有连接或预测 Ghost 时启用多物理世界

如果不能禁用 `PredictedPhysicsConfigSystem`，但又需要在开始接收 Ghost 数据前运行物理系统，可以改用[多物理世界配置](#client-only-physics-simulation-with-multiple-physics-worlds)

物理系统只有在 `Unity.Physics.SimulationSingleton` 完成初始化后才能运行，因此可以在一帧开始时强制执行一次 `PhysicsSystemGroup`

```csharp
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class ForcePhysicsInitializationIfNotConnect : SystemBase
{
    protected override void OnUpdate()
    {
        if (SystemAPI.GetSingleton<SimulationSingleton>().Type == SimulationType.NoPhysics)
        {
            // 强制更新一次物理系统，确保必要状态完成初始化
            World.PushTime(new TimeData(0.0, 0f));
            World.GetExistingSystem<PhysicsSystemGroup>().Update(World.Unmanaged);
            World.PopTime();
            Enabled = false;
        }
    }
}
```

此后，即使客户端尚未连接服务器或尚未进入游戏，自定义物理世界也会按固定频率更新

<a id="limitations"></a>

## 限制

联合使用物理系统与 Netcode 时，需要注意以下限制：

- 客户端物理模拟不会使用部分 Tick。如果希望物理表现的更新频率高于实际模拟频率，需要使用物理插值
- 使用多个物理世界时，Unity Physics 调试系统无法正确工作，只会显示默认物理世界

## 其他资源

- [Unity Physics](https://docs.unity3d.com/Packages/com.unity.physics@latest?subfolder=/manual/index.html)
