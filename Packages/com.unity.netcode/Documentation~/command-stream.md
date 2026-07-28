# 使用命令流处理输入

当 [`NetworkStreamConnection`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamConnection.html) 被标记为 InGame 后，每个客户端都会持续向服务器发送命令流。该流包含所有输入和最近收到快照的确认信息，通常每个 `NetworkTime.ServerTick` 发送一个数据包

即使客户端没有控制任何实体，或者没有需要发送给服务器的输入，连接也会始终保持活跃。命令数据包会按照[客户端收集并发送输入](#collecting-and-sending-input-from-the-client)中描述的时间尺度定期发送，同时自动确认收到的快照，并向服务器报告其他重要信息

<a id="ownership-in-netcode-for-entities"></a>

## Netcode for Entities 中的所有权

Netcode for Entities 将“拥有者”定义为对某个 Ghost 具有输入权威的连接。如果客户端拥有某个 Ghost，就可以通过输入影响该 Ghost。其他客户端也可以预测该 Ghost，但没有精确预测它所需的本地输入，只能依赖当前速度、加速度等其他启发式信息

例如，客户端预测生成一枚导弹后，虽然该客户端不会继续直接向导弹 Ghost 发送输入，但它的输入仍然导致了导弹存在。因此，该客户端对导弹拥有输入权威，也就是拥有该 Ghost

> [!NOTE]
> 输入权威与模拟权威是两个不同的概念。模拟权威是指对由输入驱动的 Ghost，拥有模拟其权威结果权限的机器或角色。在 Netcode for Entities 中，默认由服务器角色承担模拟权威
>
> 例如，FPS 角色控制器的输入权威属于某名玩家，由 `GhostOwner` 标记，但客户端只能预测该角色控制器的结果，真正的模拟权威仍然是服务器。服务器角色还可以销毁、重新生成或以其他方式修改该角色控制器实体
>
> 同理，客户端对其他 Ghost（例如导弹）不具有模拟权威，因此只能预测这些 Ghost 的生成

<a id="creating-inputs-commands"></a>

## 创建输入命令

要创建新的输入类型，定义一个实现 [`ICommandData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandData.html) 接口的结构体，并提供用于访问 `Tick` 的属性

`ICommandData` 的序列化和注册代码会自动生成，也可以禁用自动生成并[手动编写序列化](#manual-serialization)

可以在烘焙时通过 Authoring 组件，或在运行时，把 `ICommandData` 缓冲区添加到玩家控制的实体上。在运行时添加缓冲区时，确保服务器和客户端上都存在该动态缓冲区

<a id="collecting-and-sending-input-from-the-client"></a>

### 客户端收集并发送输入

客户端负责轮询输入源，并把 `ICommand` 添加到其控制实体的缓冲区中。负责写入命令缓冲区的系统必须全部运行在 [`GhostInputSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInputSystemGroup.html) 内

`CommandSendPacketSystem` 会在每个完整 Tick 的第一次[部分 Tick](intro-to-prediction.md#partial-ticks)结束时，自动发送当前排队的命令，以及前 `n` 个 Tick 的命令作为丢包冗余。`n` 由 [`ClientTickRate.TargetCommandSlack`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_TargetCommandSlack) 加上 [`ClientTickRate.NumAdditionalCommandsToSend`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_NumAdditionalCommandsToSend) 决定

例如，在 Tick 10.3 收集输入时，输入会作为 Tick 10 的输入发送给服务器。在 Tick 10.7，输入已发生变化，但不会再次发送。在 Tick 11.2，前一个完整 Tick 10 的输入会被发送，参阅 [`ICommandData` 序列化与负载限制](#icommanddata-serialization-and-payload-limit)。如果服务器还没有模拟 Tick 10，就会更新 Tick 10 的输入

<a id="icommanddata-serialization-and-payload-limit"></a>

### `ICommandData` 序列化与负载限制

使用 `ICommandData` 时，Netcode for Entities 会在 [`CommandSendSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.CommandSendSystemGroup.html) 中自动生成命令序列化代码。每个命令由自己的代码生成系统序列化，并排入网络连接上的 [`OutgoingCommandDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.OutgoingCommandDataStreamBuffer.html)。然后，`CommandSendPacketSystem` 按[客户端收集并发送输入](#collecting-and-sending-input-from-the-client)中描述的时间尺度刷新发送缓冲区

除了最新输入，还会包含前 `n` 个 Tick 的输入以提供丢包冗余。`n` 由 [`ClientTickRate.TargetCommandSlack`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_TargetCommandSlack) 加上 [`ClientTickRate.NumAdditionalCommandsToSend`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.html#Unity_NetCode_ClientTickRate_NumAdditionalCommandsToSend) 决定，默认值为 4。每条冗余命令都会针对当前 Tick 的命令进行 Delta 压缩。最终序列化数据大致如下：

```
| Tick, Command | CommandDelta(Tick-1, Tick) | CommandDelta(Tick-2, Tick) | CommandDelta(Tick-3, Tick) |
```

命令负载限制为 1024 字节。命令序列化到发送缓冲区时会检查该限制，如果编码后的负载超过 1024 字节，就会向应用报告错误

<a id="receiving-commands-on-the-server"></a>

### 服务器接收命令

[`NetworkStreamReceiveSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamReceiveSystem.html) 会在服务器上自动接收 `ICommandData`，并将其添加到 [`IncomingCommandDataStreamBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IncomingCommandDataStreamBuffer.html)。随后，`CommandReceiveSystem` 将命令数据分发给命令所属的实体

> [!NOTE]
> 服务器只能接收客户端发送的命令，不应覆盖或更改客户端传入的输入

<a id="automatically-handling-commands-autocommandtarget"></a>

## 自动处理命令（`AutoCommandTarget`）

只要把 `ICommandData` 组件添加到 Ghost，并在以下 **GhostAuthoring** 选项中启用：

1. 设置 `Has Owner`
2. 设置 `Support Auto Command Target`

<img src="images/enable-autocommand.png" width="500" alt="启用自动命令目标"/>

自动命令目标还要求 Ghost 满足以下条件：

- Ghost 由当前客户端拥有，即服务器把 [`GhostOwner`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostOwner.html) 设置为当前客户端的 [`NetworkId.Value`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkId.html#Unity_NetCode_NetworkId_Value)
- Ghost 是 `Predicted` 或 `OwnerPredicted`，不能使用 `ICommandData` 控制插值 Ghost
- [`AutoCommandTarget.Enabled`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.AutoCommandTarget.html) 必须设为 `true`

如果不使用 `AutoCommandTarget`，游戏代码必须在连接实体上设置 [`CommandTarget`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.CommandTarget.html)，使其引用附加了 `ICommandData` 组件的实体。游戏中可以存在多个 `ICommandData`，Netcode for Entities 只会发送 `CommandTarget` 指向实体上的 `ICommandData`

需要从缓冲区读取输入时，可以对 `DynamicBuffer<ICommandData>` 使用 `GetDataAtTick` 扩展方法，获取指定帧对应的输入。还可以使用 `AddCommandData` 工具方法，由它负责向环形缓冲区添加更多命令

> [!NOTE]
> 在预测循环内更新模拟状态时，只能依赖给定输入类型的 `ICommandData` 缓冲区中的命令。直接使用 `UnityEngine.Input` 轮询输入，或依赖实现 `ICommandData` 接口的结构体中不存在的输入信息，都会导致客户端错误预测

<a id="checking-ghost-ownership-on-the-client"></a>

## 在客户端检查 Ghost 所有权

> [!NOTE]
> 以下命令要正常工作，必须使用并实现 `GhostOwner` 功能，例如在 `GhostAuthoringComponent` 中勾选 `Has Owner`

Ghost 通常共享同一个 `CommandBuffer`。向缓冲区添加新输入前，必须检查实体是否属于本地玩家，避免覆盖其他玩家的输入

可以通过以下方式检查 Ghost 所有权：

- 使用 [`GhostOwnerIsLocal` 组件（推荐）](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostOwnerIsLocal.html)
- 使用 [`GhostOwner` 组件](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostOwner.html)

<a id="use-the-ghostownerislocal-component-recommended"></a>

### 使用 `GhostOwnerIsLocal` 组件（推荐）

所有 Ghost 都有一个可启用的 `GhostOwnerIsLocal` 组件，用于过滤不属于本地玩家的 Ghost

例如：

```csharp
Entities
    .WithAll<GhostOwnerIsLocal>()
    .ForEach((ref MyComponent myComponent) =>
    {
        // 这里的逻辑只会应用于本地玩家拥有的实体
    }).Run();
```

`GhostOwnerIsLocal` 应用于客户端操作，例如处理本地玩家输入，或将摄像机位置匹配到本地玩家位置。该组件在服务器侧的行为未定义

<a id="use-the-ghostowner-component"></a>

### 使用 `GhostOwner` 组件

可以手动检查实体 `GhostOwner.NetworkId` 是否等于玩家的 `NetworkId`，从而过滤实体

```csharp
var localPlayerId = GetSingleton<NetworkId>().Value;
Entities
    .ForEach((ref MyComponent myComponent, in GhostOwner owner) =>
    {
        if (owner.NetworkId == localPlayerId)
        {
            // 这里的逻辑只会应用于本地玩家拥有的实体
        }
    }).Run();
```

<a id="automatic-command-input-iinputcomponentdata"></a>

## 自动命令输入（`IInputComponentData`）

> [!NOTE]
> 以下命令要正常工作，必须使用并实现 `GhostOwner` 功能，例如在 `GhostAuthoringComponent` 中勾选 `Has Owner`

上一节描述的大部分功能都可以自动管理。定义一个实现 [`IInputComponentData`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.IInputComponentData.html) 接口的输入组件数据结构体，然后在处理输入时向命令缓冲区添加并读取命令即可。只要分别设置输入收集系统和输入处理系统，Unity 就会通过代码生成系统自动处理输入

由于实现 `IInputComponentData` 的输入结构体会被 `ICommandData` 烘焙，[命令负载 1024 字节限制](#icommanddata-serialization-and-payload-limit)同样适用

> [!NOTE]
> Ghost Authoring 组件 Inspector 中针对 Prefab 的覆盖设置，对输入组件及其配套缓冲区无效。在输入组件代码中添加 Ghost 组件特性后，该特性也会应用到缓冲区

<a id="input-events"></a>

### 输入事件

在 `IInputComponentData` 输入中使用 [`InputEvent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.InputEvent.html) 类型，确保一次性事件（例如通过 `UnityEngine.Input.GetKeyDown` 收集的事件）能与服务器正确同步，并且只注册一次，即使首次注册该事件的输入 Tick 在发送到服务器的途中丢失

<a id="how-it-works"></a>

### 工作原理

标准输入组件数据结构通常需要配置以下系统：

- 输入收集系统（客户端循环）
  - 获取输入事件并保存到输入组件数据中，在 [`GhostInputSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInputSystemGroup.html) 中执行
- 输入处理系统（服务器或预测循环）
  - 读取当前输入组件并处理其值，通常在 [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html) 中执行

使用 `IInputComponentData` 后，代码生成系统会自动完成以下流程：

- 输入收集系统（客户端循环）
  - 获取输入事件并保存到输入组件数据中，在 `GhostInputSystemGroup` 中执行
- 将输入复制到命令缓冲区（客户端循环）
  - 读取当前输入数据组件，将其添加到命令缓冲区，并记录当前 Tick
- 将当前 Tick 的输入应用到输入组件数据（服务器或预测循环）
  - 从命令缓冲区读取当前 Tick 的输入，并应用到输入组件。发生预测回滚时，可能会多次应用输入
- 输入处理系统（服务器或预测循环）
  - 读取当前输入组件并处理其值，通常在 `PredictedSimulationSystemGroup` 中执行

第一步和最后一步与单机输入处理相同，也是唯一需要自行编写或管理的系统。启用 Netcode 的输入与普通输入处理有一项重要区别：由于系统会处理之前 Tick 的回滚，输入处理系统可能在一个 Tick 内被调用多次

<a id="example-code"></a>

### 示例代码

带跳跃功能的角色移动输入：

```csharp
using Unity.Entities;
using Unity.NetCode;

[GenerateAuthoringComponent]
public struct PlayerInput : IInputComponentData
{
    public int Horizontal;
    public int Vertical;
    public InputEvent Jump;
}
```

输入收集系统从本地玩家实体读取当前输入，并写入其输入组件数据：

```csharp
[UpdateInGroup(typeof(GhostInputSystemGroup))]
[AlwaysSynchronizeSystem]
public partial class GatherInputs : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<PlayerInput>();
    }

    protected override void OnUpdate()
    {
        bool jump = UnityEngine.Input.GetKeyDown("space");
        bool left = UnityEngine.Input.GetKey("left");
        //...

        var networkId = GetSingleton<NetworkId>().Value;
        Entities.WithName("GatherInput").WithAll<GhostOwnerIsLocal>().ForEach((ref PlayerInput inputData) =>
            {
                inputData = default;

                if (jump)
                    inputData.Jump.Set();
                if (left)
                    inputData.Horizontal -= 1;
                //...
            }).ScheduleParallel();
    }
}
```

输入处理系统读取玩家输入组件中的当前值，并执行对应的移动操作：

```csharp
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
public partial class ProcessInputs : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<PlayerInput>();
    }

    protected override void OnUpdate()
    {
        var movementSpeed = Time.DeltaTime * 3;
        Entities.WithAll<Simulate>().WithName("ProcessInputForTick").ForEach(
            (ref PlayerInput input, ref Translation trans, ref PlayerMovement movement) =>
            {
                if (input.Jump.IsSet)
                    movement.JumpVelocity = 10; // 开始跳跃流程

                // 处理跳跃事件、移动逻辑等
            }).ScheduleParallel();
    }
}
```

<a id="manual-serialization"></a>

## 手动序列化

要手动序列化命令：

1. 在实现 `ICommandData` 接口的结构体上添加 [`[NetCodeDisableCommandCodeGen]`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetCodeDisableCommandCodeGenAttribute.html) 特性
2. 创建一个实现 [`ICommandDataSerializer<T>`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandDataSerializer-1.html) 的结构体，其中 `<T>` 是你的 `ICommandData` 结构体

[`ICommandDataSerializer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ICommandDataSerializer-1.html) 有两组 `Serialize` 和 `Deserialize` 方法：一组处理原始值，另一组处理 Delta 压缩值。每个命令数据包包含多个输入，第一条数据包包含原始数据，后续数据使用 Delta 压缩。由于输入变化率通常较低，Delta 压缩对输入效果很好

除了创建序列化器结构体，还需要创建泛型系统 `CommandSendSystem` 和 `CommandReceiveSystem` 的具体实例。可以继承基础系统，例如：

```csharp
[UpdateInGroup(typeof(CommandSendSystemGroup))]
[BurstCompile]
public partial struct MyCommandSendCommandSystem : ISystem
{
    CommandSendSystem<MyCommandSerializer, MyCommand> m_CommandSend;

    [BurstCompile]
    struct SendJob : IJobChunk
    {
        public CommandSendSystem<MyCommandSerializer, MyCommand>.SendJobData data;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            data.Execute(chunk, unfilteredChunkIndex);
        }
    }

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        m_CommandSend.OnCreate(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!m_CommandSend.ShouldRunCommandJob(ref state))
            return;
        var sendJob = new SendJob { data = m_CommandSend.InitJobData(ref state) };
        state.Dependency = sendJob.Schedule(m_CommandSend.Query, state.Dependency);
    }
}

[UpdateInGroup(typeof(CommandReceiveSystemGroup))]
[BurstCompile]
public partial struct MyCommandReceiveCommandSystem : ISystem
{
    CommandReceiveSystem<MyCommandSerializer, MyCommand> m_CommandRecv;

    [BurstCompile]
    struct ReceiveJob : IJobChunk
    {
        public CommandReceiveSystem<MyCommandSerializer, MyCommand>.ReceiveJobData data;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
            bool useEnabledMask, in v128 chunkEnabledMask)
        {
            data.Execute(chunk, unfilteredChunkIndex);
        }
    }

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        m_CommandRecv.OnCreate(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var recvJob = new ReceiveJob { data = m_CommandRecv.InitJobData(ref state) };
        state.Dependency = recvJob.Schedule(m_CommandRecv.Query, state.Dependency);
    }
}
```
