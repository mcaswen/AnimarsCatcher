# Ghost 优化

通过优化 Ghost 改善游戏性能

- [重要度缩放](#importance-scaling)
- [Ghost 相关性](#ghost-relevancy)
- [预序列化 Ghost](#preserialize-ghosts)
- [Optimization Mode](#optimization-mode)

<a id="importance-scaling"></a>

## 重要度缩放

服务器以固定带宽目标运行，并在每个网络 Tick 发送一个大小可配置的快照数据包。服务器使用 Ghost Chunk 的优先级队列填充数据包，优先发送重要度最高的 Ghost；该队列每个 Tick 都会重建

因此，重要度是在 Ghost Chunk 层级确定的，而不是针对每个实体实例分别计算

每个 Ghost Chunk 的重要度由以下因素决定：

- 可以按 Ghost 类型设置基础 [`GhostAuthoringComponent.Importance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_Importance)
  - Netcode for Entities 会将基础重要度乘以 `ticksSinceLastSent`，而不是 `ticksSinceLastAcked`，还会应用 [`GhostSendSystemData.IrrelevantImportanceDownScale`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_IrrelevantImportanceDownScale) 和 [`GhostSendSystemData.FirstSendImportanceMultiplier`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSendSystemData.html#Unity_NetCode_GhostSendSystemData_FirstSendImportanceMultiplier) 等修正项
- 可以通过 [`GhostImportance.BatchScaleImportanceFunction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_BatchScaleImportanceFunction)，按 Chunk、按连接提供自定义的重要度缩放函数。例如，可以[降低远处 Ghost 的优先级，提高附近 Ghost 的优先级](#distance-based-importance)
- [`GhostAuthoringComponent.MaxSendRate`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_MaxSendRate) 不会直接改变重要度值。它在预处理阶段决定某个 Ghost Chunk 本 Tick 是否能进入优先级队列

数据包达到带宽目标后，服务器就会发送它。本 Tick 未发送的 Ghost 实体会因为 `ticksSinceLastSent` 增长，而更可能进入下一份快照

> [!NOTE]
> Ghost Group 的子实体在离开 Group 之前不支持相关性、重要度、`MaxSendRate`、静态优化等功能，详情请参阅 [Ghost Group](../ghost-groups.md)

<a id="set-up-ghost-importance-scaling"></a>

### 配置 Ghost 重要度缩放

下面演示如何配置 Netcode for Entities 内置的基于距离的重要度缩放。如果需要自定义重要度实现，可以复用内置方案的一部分，也可以完全替换它

<a id="ghostimportance"></a>

#### `GhostImportance`

[`GhostImportance`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostImportance.html) 是重要度缩放的配置组件。只有创建了 `GhostConnectionComponentType` 和 `GhostImportanceDataType`，`GhostSendSystem` 才会调用 `BatchScaleImportanceFunction`

可以设置 `GhostImportance` 的以下字段：

- [`BatchScaleImportanceFunction`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_BatchScaleImportanceFunction)：编写并指定自定义缩放函数，以 Chunk 为粒度缩放重要度
- [`GhostConnectionComponentType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostConnectionComponentType)：添加到每条连接上的类型，用于保存缩放计算所需的连接级数据
- [`GhostImportanceDataType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostImportanceDataType)：可选的单例组件，用于向缩放计算传入自定义静态数据
- [`GhostImportancePerChunkDataType`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostImportance.html#Unity_NetCode_GhostImportance_GhostImportancePerChunkDataType)：添加到各 Chunk 的共享组件，用于保存缩放计算所需的 Chunk 级数据

<a id="order-of-operations"></a>

#### 执行顺序

`GhostSendSystem` 会先为每个 Chunk 调用函数指针，函数返回该 Chunk 内实体的重要度缩放值。方法签名采用 `GhostImportance.ScaleImportanceDelegate` 委托类型，参数是指向上述三种数据类型实例的 `IntPtr`

必须为每条连接添加 `GhostConnectionComponentType` 组件，用于确定该连接应优先处理哪个空间分块。`GhostSendSystem` 会把这份连接级信息传入 `BatchScaleImportanceFunction`

`GhostImportanceDataType` 是一份全局、静态的单例数据，用于配置 Chunk 的构造方式。它是可选项；找不到时系统会传入 `IntPtr.Zero`。`GhostSendSystem` 会读取该单例，再把它传给重要度缩放函数

> [!NOTE]
> `GhostImportanceDataType` 必须与 `GhostImportance` 单例添加到同一个实体上，否则 Editor 中会抛出异常

随后需要为每个 Ghost 添加 `GhostImportancePerChunkDataType`，实质上是强制它进入特定 Chunk。`GhostSendSystem` 要求该类型是共享组件，从而让实体系统把相同值的元素组织在同一 Chunk 内。需要由自定义系统更新每个实体的共享组件值，使实体按需重新分组，下面提供了示例

应仔细评估实体在 Chunk 间迁移的性能影响，因为频繁改变实体所属 Chunk 的效率并不高

<a id="distance-based-importance"></a>

### 基于距离的重要度

Netcode for Entities 内置的重要度缩放实现是 [`GhostDistanceImportance.Scale`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceImportance.html)。它基于距离，并使用空间分块把实体组织成 Chunk。`GhostDistanceData` 组件描述实体分组所用空间分块的大小与边界

<a id="distance-based-importance-in-asteroids"></a>

#### Asteroids 中基于距离的重要度

[Asteroids 示例项目](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/Asteroids)使用 Netcode for Entities 的默认缩放实现。`LoadLevelSystem` 创建一个单例实体，并为其添加 `GhostDistanceData` 与 `GhostImportance`：

```csharp
var gridSingleton = state.EntityManager.CreateSingleton(new GhostDistanceData
{
    TileSize = new int3(tileSize, tileSize, 256),
    TileCenter = new int3(0, 0, 128),
    TileBorderWidth = new float3(16f, 16f, 16f),
});
state.EntityManager.AddComponentData(gridSingleton, new GhostImportance
{
    BatchScaleImportanceFunction = GhostDistanceImportance.ScaleFunctionPointer,
    GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
    GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
    GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
});
```

> [!NOTE]
> 两个单例组件必须添加到同一个实体上

[`GhostDistancePartitioningSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistancePartitioningSystem.html) 会根据上述分块大小，把 World 中的所有 Ghost 拆分到不同 Chunk。通过可配置的 [`GhostConnectionPosition`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostConnectionPosition.html) 组件和 Entities 的 [Chunk](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/components-chunk-introducing.html) 机制，Netcode for Entities 可以建立空间分区，并依据分区与该连接角色控制器或其他关键对象的距离，快速剔除整组实体

`GhostConnectionPosition` 保存玩家实体的位置，在 Asteroids 示例中对应 `Ship.prefab`。`GhostSendSystem` 会把该位置传入 `Scale` 函数，使每条连接分别判断自己应优先处理哪些空间分块或 Chunk

Asteroids 在调用项目自定义的 `RpcLevelLoaded` RPC 时，把该组件添加到连接实体：

```csharp
[BurstCompile(DisableDirectCall = true)]
[AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
{
    var rpcData = default(RpcLevelLoaded);
    rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

    parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new PlayerStateComponentData());
    parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(NetworkStreamInGame));
    parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(GhostConnectionPosition)); // 在这里添加
}
```

随后由 Asteroids 服务器系统中的 `UpdateConnectionPositionSystemJob` 更新它：

```csharp
[BurstCompile]
partial struct UpdateConnectionPositionSystemJob : IJobEntity
{
    [ReadOnly] public ComponentLookup<LocalTransform> transformFromEntity;

    public void Execute(ref GhostConnectionPosition conPos, in CommandTarget target)
    {
        if (!transformFromEntity.HasComponent(target.targetEntity))
            return;
        conPos = new GhostConnectionPosition
        {
            Position = transformFromEntity[target.targetEntity].Position
        };
    }
}
```

<a id="create-a-custom-importance-scaling-function"></a>

### 创建自定义重要度缩放函数

重要度缩放使用的所有组件和函数都可以配置。创建自定义缩放函数需要完成三项工作：

1. 定义上述三种组件：连接级组件、可选的单例配置组件和 Chunk 级共享组件，并在 `GhostImportance` 单例中指定它们
2. 定义缩放函数，并通过 `GhostImportance` 单例指定它
3. 实现自己的 `GhostDistancePartitioningSystem`，通过写入共享组件在 Chunk 之间移动实体

<a id="ghost-relevancy"></a>

## Ghost 相关性

Ghost 相关性也称为 Ghost 过滤，是一项服务器功能，用于定义特定 Ghost 实体在什么条件下向某个客户端复制。它可以用于：

- 为 Ghost 定义最大复制距离，使其只在玩家附近生成
- 实现服务器侧的防作弊战争迷雾，避免客户端获知本不应看到的 Ghost
- 只向特定客户端告知某个 Ghost 的状态，例如隐藏信息游戏中掉落的物品
- 创建客户端专属 Ghost，例如只在玩家完成特定任务条件后可见的 NPC
- 客户端处于特定状态时暂时停止对其进行全部复制，例如玩家死亡并等待重生期间

对于玩家既看不到也无法交互的实体，应使用 Ghost 相关性避免复制它们

> [!NOTE]
> Ghost Group 的子实体在离开 Group 之前不支持相关性、重要度、`MaxSendRate`、静态优化等功能，详情请参阅 [Ghost Group](../ghost-groups.md)

[`GhostRelevancy`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html) 单例组件提供以下控制项：

- [`GhostRelevancyMode`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_GhostRelevancyMode) 定义相关性子系统的行为
  - **Disabled**：默认设置，任何情况下都不应用相关性
  - **SetIsRelevant**：只有添加到相关性集合 `GhostRelevancySet` 的 Ghost 才会被视为与该客户端相关，并尽可能为指定连接序列化。最终一致性和重要度缩放规则仍然适用
  - 如果默认使用该模式，则除非 Ghost 位于 `GhostRelevancySet` 中，否则不会复制给任何客户端。当玩家很少或不可能看到整个 World 时，该模式很有用
  - **SetIsIrrelevant**：添加到 `GhostRelevancySet` 的 Ghost 会被视为与该客户端无关，不会为指定连接序列化。需要为某个客户端明确忽略实体时使用该模式
- [`GhostRelevancySet`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_GhostRelevancySet) 保存“连接与 Ghost”的配对，其行为由 `GhostRelevancyMode` 决定
- [`DefaultRelevancyQuery`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostRelevancy.html#Unity_NetCode_GhostRelevancy_DefaultRelevancyQuery) 是一项全局规则：匹配该查询的所有 Ghost Chunk 默认与所有连接相关，除非相应 Ghost 已加入 `GhostRelevancySet`。它适合定义通用相关性规则，例如追踪玩家分数的实体始终相关。`GhostRelevancySet` 的优先级高于这项规则。示例实现请参阅 [Asteroids 示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/NetcodeSamples/Assets/Samples/Asteroids/Authoring/Server/SetAlwaysRelevantSystem.cs)

```csharp
var relevancy = SystemAPI.GetSingletonRW<GhostRelevancy>();
relevancy.ValueRW.DefaultRelevancyQuery = GetEntityQuery(typeof(AsteroidScore));
```

> [!NOTE]
> 如果某个 Ghost 已复制到客户端，之后又被设为与该客户端无关，客户端会收到该实体已被“销毁”的通知，并在本地执行 Despawn。这个名称容易引起误解，因为 Despawn 并不代表服务器实体真的被销毁
>
> 例如，MOBA 中的敌方单位因进入战争迷雾而在客户端 Despawn 时，不应播放死亡动画或声音、视觉特效。因此，应使用其他数据说明实体进入了哪种销毁状态，例如启用 `IsDead` 或 `IsCorpse` 组件

<a id="relevancy-fast-path-via-importance-scaling"></a>

### 通过重要度缩放使用相关性快速路径

如果相关性可以用与重要度缩放相同的数据表达，就可以把 Ghost 相关性计算合并到批量重要度缩放函数指针中

如 [`GhostDistanceImportance.BatchScaleWithRelevancy` 示例代码](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostDistanceImportance.html#Unity_NetCode_GhostDistanceImportance_BatchScaleWithRelevancyFunctionPointer) 所示，启用该快速路径需要以下步骤：

1. 通过 `SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;` 启用相关性，也可以使用 `SetIsIrrelevant`
2. 为每个 Chunk 设置 `PrioChunk.isRelevant` 标志。该标志不区分 `SetIsRelevant` 和 `SetIsIrrelevant`，因此无论当前采用哪种模式，只要设置 `isRelevant = true`，该 Chunk 就会被视为相关

```csharp
...
data.priority = basePriority;
data.isRelevant = distSq <= 16; // 距离玩家超过四个空间分块的 Chunk 将被视为无关，除非明确加入 GhostRelevancySet
```

使用该快速路径时，无需把 Ghost 实例写入全局 `GhostRelevancySet`，除非它不会通过 Ghost 重要度函数的 `isRelevant` 标志变为相关。例如，某个地图标记 Ghost 远远超出 `BatchScaleWithRelevancy` 的实际半径，但仍然需要复制

> [!NOTE]
> `PrioChunk.isRelevant` 的优先级低于实体级 `GhostRelevancySet`

<a id="preserialize-ghosts"></a>

## 预序列化 Ghost

默认情况下，服务器会为每条连接分别序列化一次 Ghost。序列化按需进行，只有 Ghost 实际要发送给某个客户端时才会处理。当服务器拥有大量连接和 Ghost 时，这项工作可能消耗大量 CPU。可以使用预序列化降低此成本

预序列化允许服务器只序列化一次 Ghost 数据，再为所有连接复用结果。有两种启用方式：

1. 在 Ghost Prefab 的 `GhostAuthoringComponent` Inspector 中启用 [`UsePreserialization`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostAuthoringComponent.html#Unity_NetCode_GhostAuthoringComponent_UsePreSerialization)，使该类型的所有 Ghost 都使用预序列化
2. 在服务器 World 的 Ghost 实体上添加 [`PreSerializedGhost`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSerializedGhost.html) 组件，只让该特定 Ghost 使用预序列化

启用后，服务器只为所有连接序列化一次该 Ghost。但预序列化 Ghost 会在每个 Tick 定期序列化，即使它不会发送给任何客户端。因此，只建议对频繁发送给多个客户端的 Ghost 使用预序列化，否则 CPU 开销可能高于默认的按需序列化

<a id="optimization-mode"></a>

## Optimization Mode

**Optimization Mode** 是 `GhostAuthoringComponent` 上的一项设置，用于改变 Netcode for Entities 重新发送已生成实体的 `GhostField` 的频率。它有两种模式：**Dynamic** 和 **Static**

- **Dynamic**：默认模式，适合预期会频繁变化的 Ghost。无论数据变化与否，它都会尽量减小快照大小
- **Static**：适合预期很少变化的 Ghost。数据变化时不会针对快照大小进行优化，但数据不变时完全不会发送

例如，对于生成后永不移动的对象，应把 **Optimization Mode** 设为 **Static**，避免 Netcode for Entities 重复同步它们的 Transform

`GhostField` 发生变化时，无论采用哪种 **Optimization Mode**，Netcode for Entities 都会发送变更。该选项只优化快照的发送数量和大小

<a id="limitations-with-static-optimized-ghosts"></a>

### Static 优化 Ghost 的限制

- Static 优化 Ghost 会被强制启用 [`UseSingleBaseline`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostPrefabCreation.Config.html#Unity_NetCode_GhostPrefabCreation_Config_UseSingleBaseline)
- [Ghost Group](../ghost-groups.md) 中的 Ghost 无法使用 Static 优化，无论它是根实体还是 Group 子实体；包含任何已复制子组件的 Ghost 也不支持。上述情况下，Ghost 运行时会被视为 **Dynamic**
- 同时采用 Static 优化和插值模式的 Ghost 不会执行 `GhostField` 外推，`SmoothingAction.InterpolateAndExtrapolate` 会被强制改为 `SmoothingAction.Interpolate`

## 其他资源

- [Ghost 与快照](../ghost-snapshots.md)
