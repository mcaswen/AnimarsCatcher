# 客户端与服务器 World 网络模型

本文介绍 Netcode for Entities 包采用的客户端与服务器网络模型

Netcode for Entities 使用客户端-服务器模型，并把客户端与服务器逻辑拆分到多个 World 中，包括客户端 World 和服务器 World。<!-- 或 Host World（实验功能），TODO --> [World](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-worlds.html) 概念继承自 Unity Entity Component System（ECS），表示由[实体](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-entities.html)和[系统](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/concepts-systems.html)组成，并按照[系统组](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/systems-update-order.html)组织的集合

除了标准客户端和服务器 World，Netcode for Entities 还支持[瘦客户端](thin-clients.md)，可用于开发阶段的游戏测试

<a id="terminology"></a>

## 术语

“客户端”和“服务器”会随语境表示不同含义：既可以表示 World 承担的角色，也可以表示运行游戏的设备

- 从托管角度看，服务器指运行服务器 World、供客户端设备连接的硬件或虚拟机
- 从角色角度看，服务器指运行权威模拟的 World，客户端指为某名玩家运行本地模拟的 World

客户端设备也可以承担服务器角色，这称为客户端托管服务器，简称 Host

<!--

TODO：本节准备开放给使用者后删除该注释

## 客户端、服务器与 Host World

Netcode for Entities 在客户端-服务器模型中支持多种 World 配置。Host World 是一种特殊的服务器 World，它也运行客户端系统，因此允许某名玩家同时充当服务器，这称为客户端托管服务器

两种概念的区别请参阅 Hosting 与 Roles 文档

| 配置 | 说明 |
|---|---|
| 仅客户端 World 连接仅服务器 World | 向玩家分发客户端构建，由项目方自行使用专用服务器托管服务器构建 |
| 仅客户端 World 连接客户端托管的服务器 World | 向玩家分发客户端-服务器构建，其中一名玩家充当服务器。Host 玩家拥有一个客户端 World，并通过 IPC 连接服务器 World |
| 仅客户端 World 连接客户端托管的单 World 服务器 | 向玩家分发客户端-服务器构建，但 Host 玩家只创建一个同时承担客户端和服务器角色的 World，而不是创建两个 World。该单 World Host 相当于运行了客户端系统的服务器。可见 Ghost 不再进行预测或插值，而是直接渲染权威 Ghost |

### 双 World 与单 World Host 模式

可以通过 `NetcodeConfig` 的 **Host World Mode Selection** 下拉框，在默认的双 World Host 模式与单 World Host 模式之间选择

> [!NOTE]
> 单 World Host 仍是实验功能，还需要在项目中添加 `NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST` 宏才能启用

两种模式各有优缺点

TODO：把以下内容整理成表格

单 World Host 模式的优点：

- 性能：双 World Host 需要在多个额外步骤上消耗 CPU，包括服务器 World 的 `SimulationGroup`、`GhostSendSystem` 序列化、客户端 World 反序列化、回滚到最近快照、重演一个或多个 Tick、执行一个部分 Tick，以及序列化输入并发送给本地服务器 World。单 World Host 只需在本地执行一个 World 和一个模拟 Tick，不需要为本机玩家发送或接收数据
- 同一进程中不再存在两个 World，因此静态状态只对应一个客户端 World 或 Host World

双 World Host 模式的优点：

- 客户端与服务器隔离：`IsClient` 和 `IsServer` 始终互斥，更容易理解和编写客户端、服务器代码。客户端专属逻辑无论运行在普通客户端还是客户端托管服务器上，行为都相同
- 更容易测试：在本地使用独立客户端 World 通过 IPC 连接本地服务器 World 时，已经是在测试客户端连接服务器。第二个客户端连接 Host 时出现客户端专属问题的概率更低，但并非为零，仍需使用构建版本或 [Multiplayer Play Mode](https://docs.unity3d.com/Packages/com.unity.multiplayer.playmode@latest) Clone 测试。例如，很容易忘记为 `Entity somePlayer` 或 `int myHealth` 字段添加 `[GhostField]`。使用单 World Host 时，始终需要另外测试仅客户端行为
- 双 World Host 更接近专用服务器。由于玩法逻辑已经拆分到客户端和服务器 World，更容易迁移到专用服务器托管模型

### 行为差异与迁移注意事项

如果要在项目中途切换双 World 与单 World Host 模式，需要了解以下行为差异：

- 连接实体：执行客户端系统的 World，也就是 Host，可能包含多个带 `NetworkId` 和 `NetworkStreamInGame` 的连接
  - 单 World Host 有一个伪连接实体，它包含单例 `LocalConnection` 组件和 `NetworkId`，但没有 `NetworkStreamConnection`
- 输入：Host 上的客户端系统可以访问其他玩家的输入，需要使用 `GhostOwnerIsLocal` 正确过滤
- `GhostOwnerIsLocal` 在两种模式中的行为不同
  - 双 World Host 中，客户端 World 会在本地拥有的 Ghost 上启用 `GhostOwnerIsLocal`，也就是 Ghost Owner ID 与 `LocalConnection` Network ID 对应的 Ghost；该组件在服务器 World 中的行为未定义
  - 单 World Host 中，Host World 与客户端 World 一样，会在本地拥有的 Ghost 上启用 `GhostOwnerIsLocal`
  - 如果希望在服务器侧运行读取输入的预测代码，而不依赖 `GhostOwnerIsLocal`，应剥离输入组件，使其只出现在预测 Ghost 上。剥离配置请参阅 `GhostComponent`
- 使用单 World Host 时，仅客户端逻辑与服务器系统在同一个 World 中执行
- 相关性与剔除：单 World Host 承担服务器角色，必须把所有服务器 Ghost 实体保留在内存中，才能为其他连接正确执行服务器职责。因此 Host World 无法为 Host 连接启用相关性，尽管其他连接仍可使用。单 World Host 需要手动禁用远处 Ghost 的渲染，不能依赖相关性
- Host 不支持预测模式切换，因此单 World Host 也不能使用该功能
- 使用单 World Host 时，所有 Ghost 都是权威 Ghost，因此需要以不同方式处理插值
  - 建议只平滑视觉表现而不修改权威值，例如对变换使用 `LocalToWorld`
  - 插值并复制数值的示例请参阅 [Health 示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/HelloNetcode/3_Advanced/01_HealthBars)
- 单 World Host 可以使用 RPC 快速路径。自定义序列化应通过 `IsPassthroughRPC` 和 `GetPassthroughActionData` 利用该路径
- 部分 Tick：单 World Host 不支持部分 Tick，可以在 Host 上改为在完整 Tick 之间对 Ghost 插值
- 延迟测试：必须用外部客户端连接 Host。单 World Host 不会序列化或反序列化自身状态，因此无法为本地对象添加人工延迟
- 追赶 Tick 发送快照：仅服务器 World 可以在同一帧发生多个追赶 Tick 时，为每个 Tick 分别发送快照包；单 World Host 每帧只发送一份快照

TODO：根据内部评审意见把以上内容整理成表格

-->

<a id="configuring-system-creation-and-updates"></a>

## 配置系统创建与更新

默认情况下，客户端和服务器 World 中的系统都会创建在 [`SimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SimulationSystemGroup.html) 中并随其更新。如果需要覆盖该行为，例如让某个系统只在客户端 World 创建和运行，可以使用以下两种方式

<a id="target-specific-system-groups"></a>

### 指定系统组

指定系统所属的系统组后，Unity 会在不存在该系统组的 World 中自动过滤该系统。也就是说，系统会继承所属系统组的 World 过滤条件。例如：

```csharp
[UpdateInGroup(typeof(GhostInputSystemGroup))]
public class MyInputSystem : SystemBase
{
    ...
}
```

查看 [`GhostInputSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInputSystemGroup.html) 上的 `WorldSystemFilter` 特性可以发现，该系统组只存在于客户端、瘦客户端和本地离线模拟 World。它还通过 `childDefaultFlags` 参数指定子系统继承的标志，而该参数不包含瘦客户端 World

因此，示例中的 `MyInputSystem` 只会存在于完整客户端和本地模拟 World，除非为它添加 `WorldSystemFilter` 来覆盖默认值

> [!NOTE]
> 在 [`PresentationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.PresentationSystemGroup.html) 中更新的系统只会添加到客户端 World，因为服务器和瘦客户端 World 不会创建 `PresentationSystemGroup`

<a id="use-worldsystemfilter"></a>

### 使用 `WorldSystemFilter`

使用 [`WorldSystemFilter`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldSystemFilter.html) 特性，可以更精确地指定系统所属的一种或多种 World 类型

创建 World 时，可以为其标记特定 [`WorldFlags`](https://docs.unity3d.com/Packages/com.unity.entities@latest/index.html?subfolder=/api/Unity.Entities.WorldFlags.html)，Netcode for Entities 使用这些标志区分 World，并据此应用过滤和更新逻辑

通过 `WorldSystemFilter` 可以在编译期声明系统属于以下 World 类型：

- `LocalSimulation`：不运行任何 Netcode 系统，也不用于运行多人模拟的 World
- `ServerSimulation`：用于运行服务器模拟的 World
- `ClientSimulation`：用于运行客户端模拟的 World
- `ThinClientSimulation`：用于运行瘦客户端模拟的 World

下面的 `MySystem` 只存在于可运行客户端模拟的 World，也就是设置了 `WorldFlags.GameClient` 的 World。如果没有该特性，系统使用 `WorldSystemFilterFlags.Default`，并自动继承父系统组的过滤规则。此例没有指定 `UpdateInGroup`，因此父组是 `SimulationSystemGroup`

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public class MySystem : SystemBase
{
    ...
}
```

<a id="bootstrap"></a>
<a id="creating-client-and-server-worlds-with-bootstrapping"></a>

## 使用 Bootstrap 创建客户端和服务器 World

项目添加 Netcode for Entities 后，会获得默认 [`ClientServerBootstrap`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html) 类。游戏启动或在 Unity Editor 中进入 Play Mode 时，该 Bootstrap 类会配置并创建服务器和客户端 World

默认 Bootstrap 会在启动时自动创建客户端和服务器 World：

```csharp
public virtual bool Initialize(string defaultWorldName)
{
    CreateDefaultClientServerWorlds();
    return true;
}
```

`ClientServerBootstrap` 使用 [Entities](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html) 定义的 Bootstrap 流程。新 World 会根据适用的过滤规则填充所有系统，包括自行定义的 `[WorldSystemFilter(...)]`、系统继承的 `WorldSystemFilterFlags` 规则以及 `DisableAutoCreation` 等特性。Netcode for Entities 还会自动注入大量系统和系统组

在 Editor 中打开游戏场景并进入 Play Mode 时，自动创建 World 最为方便，因为它可以立即迭代测试多人游戏。但独立游戏通常会先显示前端菜单，此时可能需要延迟 World 创建，或者选择要生成哪些 Netcode World

例如，可以比较“托管客户端服务器”与“通过匹配服务，以客户端身份连接专用服务器”两种流程。前者可能需要添加一个进程内服务器 World，并通过 IPC 连接它；后者只需创建客户端 World。这些场景可以通过自定义 Bootstrap 流程实现

<a id="customize-the-bootstrapping-flow"></a>

### 自定义 Bootstrap 流程

创建继承 `ClientServerBootstrap` 的类，例如 `MyGameSpecificBootstrap`，并覆写默认 `Initialize` 方法，即可自定义游戏流程。派生类可以复用现有辅助方法来创建客户端、服务器、瘦客户端和本地模拟 World。详情请参阅 [`ClientServerBootstrap` 方法](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerBootstrap.html)

以下示例覆写默认 Bootstrap，阻止自动创建客户端和服务器 World：

```csharp
public class MyGameSpecificBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        // 只创建不包含多人和 Netcode 系统的本地模拟 World
        CreateLocalWorld(defaultWorldName);
        return true;
    }
}
```

准备创建各类 Netcode World 时，再调用：

```csharp
void OnPlayButtonClicked()
{
    // 通常创建客户端 World
    var clientWorld = ClientServerBootstrap.CreateClientWorld();

    // 按需创建服务器 World
    var serverWorld = ClientServerBootstrap.CreateServerWorld();

    // 也可以为浸泡测试创建瘦客户端
    AutomaticThinClientWorldsUtility.NumThinClientsRequested = 10;
    AutomaticThinClientWorldsUtility.BootstrapThinClientWorlds();

    // 或根据以下信息自动选择要创建的 World
    // - Editor 中 PlayMode Tool 的设置
    // - Player 中当前构建类型
    ClientServerBootstrap.CreateDefaultClientServerWorlds();
}
```

[Netcode 示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/README.md)展示了如何配合这种 World 创建方式管理 Scene 和 SubScene 加载，以及离开玩法循环时如何正确释放 Netcode World

<a id="updating-the-client-and-server"></a>

## 更新客户端与服务器

使用 Netcode for Entities 时，服务器始终按固定时间步更新。这为客户端预测提供基础确定性，但并非严格确定性，同时也有助于物理稳定和帧率独立。包还会限制每帧固定步骤的最大迭代次数，避免服务器陷入模拟单帧就需要数秒的状态

需要注意，这套固定更新不使用 [Unity 标准更新频率](https://docs.unity3d.com/Manual/class-TimeManager.html)，也不使用物理系统的 **Fixed Timestep**。它使用独立的 `ClientServerTickRate.SimulationTickRate`。如果使用 `Unity.Physics`，其频率必须是该值的整数倍，参阅 `ClientServerTickRate.PredictedFixedStepSimulationTickRatio`

客户端通常以动态时间步更新，但[预测代码](intro-to-prediction.md)除外。预测代码始终采用与服务器相同的固定时间步，尝试维持两套模拟之间的确定性关系

当显示刷新率无法与完整 Tick 同步时，预测的处理方式请参阅[部分 Tick](intro-to-prediction.md#partial-ticks)

<a id="configuring-the-server-fixed-update-loop"></a>

### 配置服务器固定更新循环

服务器 World 中的 [`ClientServerTickRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html) 单例组件控制服务器 Tick 率

可以通过它控制服务器模拟循环的多个方面：

- `SimulationTickRate` 配置每秒模拟 Tick 数，默认值为 60
- `NetworkTickRate` 配置服务器向客户端发送快照的频率，默认与 `SimulationTickRate` 相同

<a id="avoiding-performance-issues"></a>

#### 避免性能问题

如果服务器更新频率低于模拟 Tick 率，就会在同一帧执行多个 Tick。例如，上一次服务器更新耗时 50 ms，而正常目标约为 16 ms，则服务器需要在下一帧执行大约三个模拟步骤来追赶，因为 `16 ms * 3 ≈ 50 ms`

该行为可能引发性能恶化循环：服务器为了追赶而在每次更新执行更多步骤，更新因此越来越慢，又进一步落后。`ClientServerTickRate` 可以配置服务器无法维持目标 Tick 率时的行为

- [`MaxSimulationStepsPerFrame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#ClientServerTickRate_MaxSimulationStepsPerFrame) 控制服务器一帧最多运行多少个模拟步骤
- [`MaxSimulationStepBatchSize`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#MaxSimulationStepBatchSize) 允许服务器循环把多个 Tick 合并为一个步骤，并成倍放大 Delta Time。例如，不运行两个步骤，而是运行一个 Delta Time 加倍的步骤

> [!NOTE]
> `MaxSimulationStepBatchSize` 启用的批处理只在特定条件下生效，也有额外细节和限制。游戏逻辑不能假设一个模拟步骤必然等于一个 Tick，也不要硬编码 `TimeData.DeltaTime`
>
> 服务器出现性能问题时可能触发批处理。由于客户端与服务器的模拟粒度不同，这会产生错误预测

最后还可以配置服务器如何消耗空闲时间，以维持目标帧率。[`TargetFrameRateMode`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientServerTickRate.html#TargetFrameRateMode) 提供以下选项：

- `BusyWait`：以最大速度运行
- `Sleep`：使用 `Application.TargetFrameRate` 降低 CPU 负载
- `Auto`：无头服务器使用 `Sleep`，其他环境使用 `BusyWait`

<a id="configuring-the-client-update-loop"></a>

### 配置客户端更新循环

客户端采用动态时间步更新，但[预测代码](intro-to-prediction.md)除外。预测代码始终使用与服务器相同的固定时间步，尝试维持两套模拟之间的确定性关系。预测运行在 [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) 中，由该组应用预测专用的固定时间步

服务器会在首次连接握手期间把 `ClientServerTickRate` 配置发送给客户端，因此客户端预测循环会使用与服务器完全相同的 `SimulationTickRate`

<a id="world-migration"></a>

## World 迁移

如果需要销毁当前 World 并启动另一个 World，同时保留连接状态，可以使用 `DriverMigrationSystem` 保存和加载 Transport 相关信息，从而平滑切换 World

```csharp
public World MigrateWorld(World sourceWorld)
{
    DriverMigrationSystem migrationSystem = default;
    foreach (var world in World.All)
    {
        if ((migrationSystem = world.GetExistingSystem<DriverMigrationSystem>()) != null)
            break;
    }

    var ticket = migrationSystem.StoreWorld(sourceWorld);
    sourceWorld.Dispose();

    var newWorld = migrationSystem.LoadWorld(ticket);

    // 必须先执行 LoadWorld，再向 World 填充所需系统
    // LoadWorld 会创建 MigrationTicket 组件，NetworkStreamReceiveSystem 需要它来加载正确的 Driver

    return ClientServerBootstrap.CreateServerWorld(DefaultWorld, newWorld.Name, newWorld);
}
```

## 其他资源

- [Entities 概述](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/index.html)
- [瘦客户端](thin-clients.md)
- [预测简介](intro-to-prediction.md)
