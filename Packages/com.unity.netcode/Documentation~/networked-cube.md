# 联网立方体

开始创建简单的客户端-服务器模拟前，请先按照[安装指南](installation.md)正确配置项目

本教程简要介绍创建客户端-服务器游戏时最常用的概念

<a id="creating-an-initial-scene"></a>

## 创建初始 Scene

首先配置一套在客户端与服务器之间共享数据的方式。Netcode for Entities 通过 [Entities 包](https://docs.unity3d.com/Packages/com.unity.entities@latest)，为服务器和每个客户端分别创建[不同的 World](client-server-worlds.md)，从而隔离两端逻辑

创建共享数据 Scene：

1. 在 Unity Editor 的 **Hierarchy** 窗口中单击右键
2. 选择 **New Subscene** > **Empty Scene...**
3. 将新 Scene 命名为 `SharedData`

![](images/create_subscene.png)

接下来，在客户端和服务器 World 中都生成一个平面。右键单击 `SharedData` SubScene，选择 **3D Object** > **Plane**，系统会在 `SharedData` 下创建一个平面

![包含平面的 Scene](images/initial-scene.png)<br/>_包含平面的 Scene_

单击 **Play**，再选择 **Window** > **Entities** > **Hierarchy**，可以看到两个 World：`ClientWorld` 和 `ServerWorld`。两者都包含刚刚创建的 `SharedData` Scene 及其平面

![Hierarchy 视图](images/hierarchy-view.png)<br/>_Hierarchy 视图_

<a id="establish-a-connection"></a>

## 建立连接

客户端和服务器需要先建立[连接](network-connection.md)才能通信。在 Netcode for Entities 中，最简单的方式是使用自动连接：继承 `ClientServerBootstrap`，再把 `AutoConnectPort` 设为所选端口

在 **Assets** 文件夹创建 `Game.cs`，添加以下代码：

```csharp
using System;
using Unity.Entities;
using Unity.NetCode;

// 创建启用自动连接的自定义 Bootstrap
// 还可以通过 Bootstrap 配置其他设置，并根据用户输入决定创建客户端或服务器 World
[UnityEngine.Scripting.Preserve]
public class GameBootstrap : ClientServerBootstrap
{
    public override bool Initialize(string defaultWorldName)
    {
        AutoConnectPort = 7979; // 启用自动连接
        return base.Initialize(defaultWorldName); // 使用标准 Bootstrap 流程
    }
}
```

<a id="communicate-with-the-server"></a>

## 与服务器通信

建立连接后即可与服务器通信。Netcode for Entities 中有一个关键概念 `InGame`：连接被标记为 InGame 后，表示它已经准备好开始[同步](synchronization.md)

进入 `InGame` 状态前，只能通过 RPC 与 Netcode for Entities 服务器通信。因此需要创建一条充当“进入游戏”消息的 RPC，例如通知服务器客户端已准备好接收[快照](ghost-snapshots.md)

在 **Assets** 文件夹创建 `GoInGame.cs`，添加以下代码：

```csharp
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 允许独立构建与 Editor 在测试期间互发 RPC
/// 完成本示例后，可用它让服务器客户端独立构建连接配置为客户端的 Editor 实例
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
    WorldSystemFilterFlags.ServerSimulation |
    WorldSystemFilterFlags.ThinClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
        state.Enabled = false;
    }
}

// 客户端请求服务器进入 InGame，并开始发送快照与输入
public struct GoInGameRequest : IRpcCommand
{
}

// 客户端连接获得 NetworkId 后进入 InGame，并通知服务器同步进入 InGame
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
    WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct GoInGameClientSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<NetworkId>()
            .WithNone<NetworkStreamInGame>();
        state.RequireForUpdate(state.GetEntityQuery(builder));
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        foreach (var (id, entity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                .WithEntityAccess()
                .WithNone<NetworkStreamInGame>())
        {
            commandBuffer.AddComponent<NetworkStreamInGame>(entity);
            var req = commandBuffer.CreateEntity();
            commandBuffer.AddComponent<GoInGameRequest>(req);
            commandBuffer.AddComponent(req,
                new SendRpcCommandRequest { TargetConnection = entity });
        }

        commandBuffer.Playback(state.EntityManager);
    }
}

// 服务器收到进入游戏请求后进入 InGame，并删除请求
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct GoInGameServerSystem : ISystem
{
    private ComponentLookup<NetworkId> networkIdFromEntity;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<GoInGameRequest>()
            .WithAll<ReceiveRpcCommandRequest>();
        state.RequireForUpdate(state.GetEntityQuery(builder));
        networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var worldName = state.WorldUnmanaged.Name;
        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        networkIdFromEntity.Update(ref state);

        foreach (var (reqSrc, reqEntity) in
            SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                .WithAll<GoInGameRequest>()
                .WithEntityAccess())
        {
            commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);
            var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

            Debug.Log($"'{worldName}' 将连接 '{networkId.Value}' 设为 InGame");
            commandBuffer.DestroyEntity(reqEntity);
        }

        commandBuffer.Playback(state.EntityManager);
    }
}
```

<a id="create-a-ghost-prefab"></a>

## 创建 Ghost Prefab

要在客户端与服务器之间同步对象，需要先创建网络对象定义，称为 **Ghost**

创建 Ghost Prefab：

1. 右键单击 Scene，选择 **3D Object** > **Cube**，在 Scene 中创建立方体
2. 在 Scene 中选中 **Cube GameObject**，将其拖入 Project 的 **Assets** 文件夹，创建立方体 Prefab
3. 创建 Prefab 后，可以删除 Scene 中的立方体，但不要删除 Prefab

![创建立方体 Prefab](images/cube-prefab.png)<br/>_创建立方体 Prefab_

为了让 Netcode for Entities 识别并同步立方体 Prefab，需要创建并烘焙一个 `IComponentData`。创建 `CubeAuthoring.cs`，输入以下代码：

```csharp
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

public struct Cube : IComponentData
{
}

[DisallowMultipleComponent]
public class CubeAuthoring : MonoBehaviour
{
    class Baker : Baker<CubeAuthoring>
    {
        public override void Bake(CubeAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<Cube>(entity);
        }
    }
}
```

把该组件添加到 `Cube.prefab`，然后在 Inspector 中为 Prefab 添加 **Ghost Authoring Component**

添加后，Unity 会自动序列化 Translation 和 Rotation 组件

移动立方体前，还需要修改新增的 **Ghost Authoring Component**：

1. 勾选 **Has Owner**。系统会自动添加并勾选 **Support Auto Command Target**，后文会进一步说明
2. 把 **Default Ghost Mode** 改为 **Owner Predicted**。后续需要在代码中设置 **Ghost Owner Component** 的 **NetworkId** 字段。这样可以确保客户端预测自己拥有对象的移动

![Ghost Authoring 组件](images/ghost-config.png)<br/>_Ghost Authoring 组件_

<a id="create-a-spawner"></a>

## 创建生成器

为了让 Netcode for Entities 知道要使用哪些 Ghost，需要从 SubScene 引用对应 Prefab。先为生成器创建组件：新建 `CubeSpawnerAuthoring.cs` 并添加以下代码：

```csharp
using Unity.Entities;
using UnityEngine;

public struct CubeSpawner : IComponentData
{
    public Entity Cube;
}

[DisallowMultipleComponent]
public class CubeSpawnerAuthoring : MonoBehaviour
{
    public GameObject Cube;

    class Baker : Baker<CubeSpawnerAuthoring>
    {
        public override void Bake(CubeSpawnerAuthoring authoring)
        {
            CubeSpawner component = default(CubeSpawner);
            component.Cube = GetEntity(authoring.Cube, TransformUsageFlags.Dynamic);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, component);
        }
    }
}
```

1. 右键单击 `SharedData`，选择 **Create Empty**
2. 将其重命名为 `Spawner`，再添加 **CubeSpawner**
3. 客户端与服务器都需要知道这些 Ghost，因此把生成器放入 **SharedData SubScene**
4. 在 Inspector 中，把立方体 Prefab 拖入生成器的 Cube 字段

![Ghost 生成器设置](images/ghost-spawner.png)<br/>_Ghost 生成器设置_

<a id="spawning-our-prefab"></a>

## 生成 Prefab

要生成 Prefab，需要更新 `GoInGame.cs`。如前所述，准备让服务器开始同步时，客户端必须发送 **GoInGame** RPC。现在可以扩展该代码，同时生成立方体

<a id="update-goingameclientsystem-and-goingameserversystem"></a>

### 更新 `GoInGameClientSystem` 和 `GoInGameServerSystem`

`GoInGameClientSystem` 和 `GoInGameServerSystem` 只应在存在 `CubeSpawner` 组件数据时运行。为此，在两个系统的 `OnCreate` 中添加 [`SystemState.RequireForUpdate`](https://docs.unity3d.com/Packages/com.unity.entities@1.0/api/Unity.Entities.SystemState.RequireForUpdate.html)：

```csharp
state.RequireForUpdate<CubeSpawner>();
```

更新后的 `GoInGameClientSystem.OnCreate`：

```csharp
[BurstCompile]
public void OnCreate(ref SystemState state)
{
    // 仅在存在 CubeSpawner 组件数据时运行
    state.RequireForUpdate<CubeSpawner>();

    var builder = new EntityQueryBuilder(Allocator.Temp)
        .WithAll<NetworkId>()
        .WithNone<NetworkStreamInGame>();
    state.RequireForUpdate(state.GetEntityQuery(builder));
}
```

更新后的 `GoInGameServerSystem.OnCreate`：

```csharp
[BurstCompile]
public void OnCreate(ref SystemState state)
{
    state.RequireForUpdate<CubeSpawner>();

    var builder = new EntityQueryBuilder(Allocator.Temp)
        .WithAll<GoInGameRequest>()
        .WithAll<ReceiveRpcCommandRequest>();
    state.RequireForUpdate(state.GetEntityQuery(builder));
    networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
}
```

还需要在 `GoInGameServerSystem.OnUpdate` 中完成以下工作：

- 获取待生成 Prefab
- 额外读取 Prefab 名称，用于日志消息
- 对每条接收的 `ReceiveRpcCommandRequest` 实例化一个 Prefab
- 将每个 Prefab 实例的 `GhostOwner.NetworkId` 设为请求客户端的 `NetworkId`
- 把新实例添加到 `LinkedEntityGroup`，使客户端断开时自动销毁实体

把 `GoInGameServerSystem.OnUpdate` 更新为：

```csharp
[BurstCompile]
public void OnUpdate(ref SystemState state)
{
    // 获取待实例化 Prefab
    var prefab = SystemAPI.GetSingleton<CubeSpawner>().Cube;

    // 获取待实例化 Prefab 的名称
    state.EntityManager.GetName(prefab, out var prefabName);
    var worldName = new FixedString32Bytes(state.WorldUnmanaged.Name);

    var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
    networkIdFromEntity.Update(ref state);

    foreach (var (reqSrc, reqEntity) in
        SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
            .WithAll<GoInGameRequest>()
            .WithEntityAccess())
    {
        commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);

        // 获取发出请求客户端的 NetworkId
        var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

        // 记录连接 NetworkId 和生成的 Prefab 名称
        UnityEngine.Debug.Log(
            $"'{worldName}' 将连接 '{networkId.Value}' 设为 InGame，" +
            $"并为其生成 Ghost '{prefabName}'");

        // 实例化 Prefab
        var player = commandBuffer.Instantiate(prefab);

        // 将 Prefab 实例与已连接客户端的 NetworkId 关联
        commandBuffer.SetComponent(player,
            new GhostOwner { NetworkId = networkId.Value });

        // 添加到 LinkedEntityGroup，使其在连接断开时自动销毁
        commandBuffer.AppendToBuffer(reqSrc.ValueRO.SourceConnection,
            new LinkedEntityGroup { Value = player });
        commandBuffer.DestroyEntity(reqEntity);
    }

    commandBuffer.Playback(state.EntityManager);
}
```

此时完整的 `GoInGame.cs` 如下：

```csharp
using UnityEngine;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Burst;

/// <summary>
/// 允许独立构建与 Editor 在测试期间互发 RPC
/// 完成本示例后，可用它让服务器客户端独立构建连接配置为客户端的 Editor 实例
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
    WorldSystemFilterFlags.ServerSimulation |
    WorldSystemFilterFlags.ThinClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[CreateAfter(typeof(RpcSystem))]
public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
        state.Enabled = false;
    }
}

// 客户端请求服务器进入 InGame，并开始发送快照与输入
public struct GoInGameRequest : IRpcCommand
{
}

// 客户端连接获得 NetworkId 后进入 InGame，并通知服务器同步进入 InGame
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
    WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct GoInGameClientSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 仅在存在 CubeSpawner 组件数据时运行
        state.RequireForUpdate<CubeSpawner>();

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<NetworkId>()
            .WithNone<NetworkStreamInGame>();
        state.RequireForUpdate(state.GetEntityQuery(builder));
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        foreach (var (id, entity) in
            SystemAPI.Query<RefRO<NetworkId>>()
                .WithEntityAccess()
                .WithNone<NetworkStreamInGame>())
        {
            commandBuffer.AddComponent<NetworkStreamInGame>(entity);
            var req = commandBuffer.CreateEntity();
            commandBuffer.AddComponent<GoInGameRequest>(req);
            commandBuffer.AddComponent(req,
                new SendRpcCommandRequest { TargetConnection = entity });
        }

        commandBuffer.Playback(state.EntityManager);
    }
}

// 服务器收到进入游戏请求后进入 InGame，并删除请求
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct GoInGameServerSystem : ISystem
{
    private ComponentLookup<NetworkId> networkIdFromEntity;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<CubeSpawner>();

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<GoInGameRequest>()
            .WithAll<ReceiveRpcCommandRequest>();
        state.RequireForUpdate(state.GetEntityQuery(builder));
        networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // 获取待实例化 Prefab
        var prefab = SystemAPI.GetSingleton<CubeSpawner>().Cube;

        // 获取待实例化 Prefab 的名称
        state.EntityManager.GetName(prefab, out var prefabName);
        var worldName = new FixedString32Bytes(state.WorldUnmanaged.Name);

        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        networkIdFromEntity.Update(ref state);

        foreach (var (reqSrc, reqEntity) in
            SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>()
                .WithAll<GoInGameRequest>()
                .WithEntityAccess())
        {
            commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);

            // 获取发出请求客户端的 NetworkId
            var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

            // 记录连接 NetworkId 和生成的 Prefab 名称
            UnityEngine.Debug.Log(
                $"'{worldName}' 将连接 '{networkId.Value}' 设为 InGame，" +
                $"并为其生成 Ghost '{prefabName}'");

            // 实例化 Prefab
            var player = commandBuffer.Instantiate(prefab);

            // 将 Prefab 实例与已连接客户端的 NetworkId 关联
            commandBuffer.SetComponent(player,
                new GhostOwner { NetworkId = networkId.Value });

            // 添加到 LinkedEntityGroup，使其在连接断开时自动销毁
            commandBuffer.AppendToBuffer(reqSrc.ValueRO.SourceConnection,
                new LinkedEntityGroup { Value = player });
            commandBuffer.DestroyEntity(reqEntity);
        }

        commandBuffer.Playback(state.EntityManager);
    }
}
```

现在单击 **Play**，Game 视图和 Entity Hierarchy 视图中应当出现已复制的立方体

![已复制的立方体](images/replicated-cube.png)<br/>_已复制的立方体_

<a id="moving-the-cube"></a>

## 移动立方体

配置 Ghost 组件时启用了 **Support Auto Command Target**，因此可以使用 `IInputComponentData` 结构体保存输入数据。该结构体定义需要序列化和反序列化的输入数据，还需要一个负责填充输入数据的系统

创建 `CubeInputAuthoring.cs`，添加以下代码：

```csharp
using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

public struct CubeInput : IInputComponentData
{
    public int Horizontal;
    public int Vertical;
}

[DisallowMultipleComponent]
public class CubeInputAuthoring : MonoBehaviour
{
    class CubeInputBaking : Unity.Entities.Baker<CubeInputAuthoring>
    {
        public override void Bake(CubeInputAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<CubeInput>(entity);
        }
    }
}

[UpdateInGroup(typeof(GhostInputSystemGroup))]
public partial struct SampleCubeInput : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
        state.RequireForUpdate<CubeSpawner>();
    }

    public void OnUpdate(ref SystemState state)
    {
        foreach (var playerInput in
            SystemAPI.Query<RefRW<CubeInput>>().WithAll<GhostOwnerIsLocal>())
        {
            playerInput.ValueRW = default;
            if (Input.GetKey("left"))
                playerInput.ValueRW.Horizontal -= 1;
            if (Input.GetKey("right"))
                playerInput.ValueRW.Horizontal += 1;
            if (Input.GetKey("down"))
                playerInput.ValueRW.Vertical -= 1;
            if (Input.GetKey("up"))
                playerInput.ValueRW.Vertical += 1;
        }
    }
}
```

把 `CubeInputAuthoring` 组件添加到立方体 Prefab，再创建一个读取 `CubeInput` 并移动玩家的系统

新建 `CubeMovementSystem.cs`，添加以下代码：

```csharp
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.Burst;

[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[BurstCompile]
public partial struct CubeMovementSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var speed = SystemAPI.Time.DeltaTime * 4;
        foreach (var (input, trans) in
            SystemAPI.Query<RefRO<CubeInput>, RefRW<LocalTransform>>()
                .WithAll<Simulate>())
        {
            var moveInput = new float2(input.ValueRO.Horizontal, input.ValueRO.Vertical);
            moveInput = math.normalizesafe(moveInput) * speed;
            trans.ValueRW.Position += new float3(moveInput.x, 0, moveInput.y);
        }
    }
}
```

<a id="test-the-code"></a>

## 测试代码

代码配置完成后，打开 **Multiplayer** > **PlayMode Tools**，把 **PlayMode Type** 设为 **Client & Server**。进入 Play Mode 后立方体会生成，按方向键即可移动它

<a id="build-standalone-build-and-connect-an-editor-based-client"></a>

## 构建独立版本并连接 Editor 客户端

客户端与服务器已经可以在 Editor 中运行。要继续测试另一个客户端连接，可以执行以下步骤：

- 确认 **Project Settings** > **Entities** > **Build** > **NetCode Client Target** 已设为 **ClientAndServer**
- 创建 Development Build，并运行该独立构建
- 从 **Multiplayer** 菜单打开 Editor 的 **PlayMode Tools** 窗口
  - 把 **PlayMode Type** 设为 **Client**
  - 把 **Auto Connect Port** 设为 `7979`
  - 此时可以按需停靠或关闭窗口
- 进入 Play Mode

现在，服务器客户端独立构建中应当能看到来自 Editor 客户端的立方体，并且可以看到两个立方体分别移动
