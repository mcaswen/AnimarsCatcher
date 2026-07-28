# 生成 Ghost

创建 Ghost Prefab，并定义客户端与服务器之间的[同步方式](ghost-snapshots.md#synchronizing-ghost-components-and-fields)后，可以通过以下方式生成 [Ghost](ghost-snapshots.md#ghosts)：

- 由服务器模拟逻辑实例化。服务器从 Ghost Prefab 实例化的所有实体都会自动生成并复制到客户端，生成与 Despawn 由 Netcode for Entities 包处理
- 在客户端实例化配置为 `Predicted` 或 `OwnerPredicted` 的 Ghost Prefab，使用[预测生成](#implement-predicted-spawning-for-player-spawned-objects)
- 把 Ghost Prefab 实例放入 SubScene，作为场景内放置对象。详情请参阅[预生成 Ghost](#pre-spawned-ghosts)

<a id="spawn-ghosts-on-the-server"></a>

## 在服务器上生成 Ghost

服务器可以通过两种方式生成复制实体：

- 使用 `EntityManager.Instantiate` 或其变体实例化 Prefab
- 使用[预生成 Ghost](#pre-spawned-ghosts)

两种方式下，服务器都可以在任意时间和系统中生成新 Ghost：

- 客户端连接之前
- 客户端连接之后
- `SimulationSystemGroup` 内的任何位置或系统组中，这是建议并优先采用的位置
- `InitializationSystemGroup` 内，但该组不按固定时间步运行，需要谨慎处理

服务器拥有权威，因此默认情况下，服务器上的所有复制实体都会由[快照复制](ghost-snapshots.md#snapshots)子系统自动生成到每个客户端。随后，服务器会按照 Ghost 同步配置向各客户端发送对应 Ghost 的更新

<a id="limit-replicated-entities-on-a-per-client-basis"></a>

### 按客户端限制复制实体

某些场景不适合把所有实体复制给所有客户端。例如，大型虚拟 World 中，客户端通常只会观察和交互玩家附近的区域；团队对战中，部分复制实体可能只属于特定队伍

服务器可以使用[相关性](optimizations.md#relevancy)，按客户端指定哪些实体需要复制、哪些不需要复制

<a id="spawning-ghosts-on-the-client"></a>

## 在客户端生成 Ghost

客户端可以使用以下多种生成类型

<a id="spawn-types"></a>

### 生成类型

**延迟生成或插值生成**

[插值](interpolation.md) Ghost 不使用[预测](prediction-n4e.md)，客户端 World 启动后也不会立即生成它。如果第一份快照到达时立刻显示 Ghost，而快照数据实际属于更晚的插值 Tick，就会出现对象先生成并静止若干 Tick，收到更多服务器数据后才开始插值的现象

因此，插值 Ghost 会在**插值时间线**上生成。生成延迟由插值时间线延迟控制，可以通过 [`ClientTickRate.InterpolationTimeNetTicks`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.InterpolationTimeNetTicks.html) 或 [`ClientTickRate.InterpolationTimeMS`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientTickRate.InterpolationTimeMS.html) 配置。当 [`NetworkTime.InterpolationTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkTime.InterpolationTick.html) 大于或等于 Ghost 的 [`GhostInstance.spawnTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostInstance.spawnTick.html) 时，插值 Ghost 才会生成。插值延迟与插值 Tick 的更多信息请参阅[时间同步](time-synchronization.md)

**客户端预测的玩家生成对象**

这类 Ghost 使用[预测](prediction-n4e.md)，通常由客户端产生的输入触发实例化，例如玩家发射的子弹或火箭。详情请参阅[实现玩家对象的预测生成](#implementing-predicted-spawning-for-player-spawned-objects)

预测生成可以消除生成过程的网络往返延迟，降低感知延迟。服务器权威快照到达后，系统先把本地预测生成实体映射到真实 Ghost 实体，这个过程称为 Ghost 分类；随后 `GhostUpdateSystem` 直接把服务器数据应用到预测 Ghost，并重演此后产生的本地输入。如果客户端错误地执行了预测生成，系统会销毁该预测 Ghost 来修正错误

**预生成 Ghost（Ghost Prespawn）**

所有在 Authoring 阶段拖入 SubScene 的 Ghost Prefab 实例都属于预生成 Ghost。它们通常是关卡专属玩法实体，例如出生点、可破坏岩石、可开关门、宝箱和武器拾取物。详情请参阅[预生成 Ghost](#pre-spawned-ghosts)

Netcode for Entities 不要求为客户端 Ghost 使用专门的生成消息。客户端从服务器收到新的 Ghost ID 时，会将其视为隐式生成，并由一组分类系统为其指定[生成类型](#spawn-types)

确定生成类型后，[`GhostSpawnSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnSystem.html) 负责实例化新实体

> [!NOTE]
> 只有 Ghost Prefab 已加载到 World 时才能生成 Ghost。服务器和客户端必须对各自拥有的 Prefab 达成一致，服务器只会向客户端复制客户端已拥有 Prefab 的 Ghost

<a id="implement-predicted-spawning-for-player-spawned-objects"></a>
<a id="implementing-predicted-spawning-for-player-spawned-objects"></a>

## 为玩家生成对象实现预测生成

与[客户端预测](intro-to-prediction.md#client-prediction)的其他部分一样，预测生成要求客户端和服务器运行相同逻辑，让两者尽可能保持确定性

客户端预测生成包含两个步骤：

1. 在预测循环内运行的系统中创建实体
2. 实体创建后，先将其分类为预测生成，再与服务器发送的权威更新进行匹配

<a id="spawn-predicted-ghosts-on-the-client-side"></a>

### 在客户端生成预测 Ghost

需要把生成系统添加到 [`PredictedSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedSimulationSystemGroup.html)，使客户端代码在与服务器相同的条件下实例化对象，例如玩家按下射击键后

所有配置为生成时预测的 Ghost Prefab 都已添加 [`PredictedGhostSpawnRequest`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawnRequest.html) 组件，因此默认会被视为预测生成

客户端 World 中的系统实例化 Ghost 实体后，它会自动作为预测生成处理。为了确保系统行为正确，只需要在 `networkTime.IsFirstTimeFullyPredictingTick` 为 `false` 时提前退出

客户端收到该实体的第一份快照更新后，系统会检测到它对应一个已由客户端生成的实体，此后的所有更新都会直接应用到该实体

预测循环会回滚并重新模拟数据，因此必须检查 [`NetworkTime.IsFirstTimeFullyPredictingTick`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.NetworkTime.html)，避免同一对象被重复生成

```csharp
public void OnUpdate()
{
    // 角色移动等其他输入可以在这里或其他系统中处理

    var networkTime = SystemAPI.GetSingleton<NetworkTime>();
    if (!networkTime.IsFirstTimeFullyPredictingTick)
        return;

    // 在这里处理实例化子弹等对象的输入
    // ...
}
```

<a id="conditions-to-check-before-spawning-predicted-ghosts"></a>

#### 生成预测 Ghost 前的检查条件

满足以下条件前，客户端不应生成实体：

1. World 中存在 `NetworkStreamInGame` 单例，否则创建的实体会被自动释放
2. `GhostCollectionPrefab` 缓冲区已经初始化，长度大于 0，并且满足以下任一条件
   - 缓冲区中存在与待生成实体 `GhostType` 组件匹配的 Ghost Prefab
   - `GhostCollection.GhostTypeToColletionIndex` 哈希表中存在对应条目，这是更快的检查方式

```csharp
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
public partial class SpawnGhost : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkStreamInGame>();
        RequireForUpdate<GhostCollection>();
    }

    protected override void OnUpdate()
    {
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();

        // 同一 Tick 重新模拟时该标志为 false，确保只生成一次
        if (!networkTime.IsFirstTimeFullyPredictingTick)
            return;

        var prefab = GetPrefabToSpawn();
        var typeToCollection =
            SystemAPI.GetSingleton<GhostCollection>().GhostTypeToColletionIndex;
        var type = EntityManager.GetComponentData<GhostType>(prefab);

        // Prefab 尚未注册，当前不能生成
        if (!typeToCollection.ContainsKey(type))
            return;

        EntityManager.Instantiate(prefab);
    }
}
```

`GhostCollection` 数据条件非常关键，因为 `PredictedGhostSpawningSystem` 需要这些数据初始化新 Ghost

初始化期间，如果在 `GhostCollectionPrefab` 中找不到来源 Prefab，系统会抛出异常，表明当前预测生成无法处理

<a id="specify-rollback-options-for-predicted-spawned-ghosts"></a>

#### 指定预测生成 Ghost 的回滚选项

客户端预测 Ghost 时，无论采用 Owner Predicted 还是 Predicted 模式，都可以指定在收到并确认服务器权威 Ghost 前，预测生成 Ghost 如何处理[预测与回滚](intro-to-prediction.md#rollback-and-replay)

在 Ghost Authoring 组件 Inspector 中勾选 **Rollback Predicted Spawned Ghost State** 后，客户端上的未分类生成 Ghost 会在收到包含该预测 Ghost 的新服务器快照时，从生成 Tick 开始回滚并重新模拟状态

这可以缓解 Ghost 之间交互造成的部分错误预测，参阅[预测错误及缓解方式](prediction-details.md#predicted-spawn-interactions-with-other-predicted-ghosts)

<a id="ghost-classification-and-entity-matching"></a>

### Ghost 分类与实体匹配

把预测生成 Ghost 与服务器权威对象配对的过程称为分类。如果分类失败，本地预测生成对象会在宽限期后被删除

Netcode for Entities 提供默认分类策略，由 [`GhostSpawnClassificationSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnClassificationSystem.html) 自动处理客户端生成的所有预测对象

`GhostSpawnClassificationSystem` 根据 Ghost 类型与生成 Tick，在五个 Tick 的窗口内，将新收到的 Ghost 与客户端预测生成对象进行匹配

默认实现存在以下限制：

- 在五 Tick 窗口内，同一类型通常只能可靠匹配一个预测生成 Ghost。例如，同一 Tick 生成多枚子弹时，只有一枚能够匹配，而且可能匹配错误。仅凭生成 Tick 无法区分每枚子弹的身份
- 如果启用 [Tick 批处理](client-server-worlds.md#avoiding-performance-issues)，服务器合并 Tick 后，输入可能在不同于产生它的 Tick 上应用，实体生成 Tick 也会略有偏差。这会影响 `GhostSpawnClassificationSystem`，导致匹配失败

如果需要更精确或更复杂的生成匹配逻辑，可以[添加自定义分类系统](#add-your-own-classification-system)覆盖默认行为

<a id="add-your-own-classification-system"></a>

### 添加自定义分类系统

自定义分类系统必须：

- 在 [`GhostSimulationSystemGroup`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSimulationSystemGroup.html) 中更新
- 在 [`GhostSpawnClassificationSystem`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnClassificationSystem.html) 之后运行

分类系统通过以下方式工作：读取单例 [`GhostSpawnQueueComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.GhostSpawnQueue.html) 实体上的 [`GhostSpawnBuffer`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnBuffer.html)，检查待生成 Ghost，并修改其 `SpawnType`

应当把 `GhostSpawnQueueComponent` 列表中的每个条目，与包含 [`PredictedGhostSpawnList`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawnList.html) 组件的单例实体上的 [`PredictedGhostSpawn`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictedGhostSpawn.html) 缓冲区条目进行比较

如果两者类型相同且匹配，分类系统应设置 `GhostSpawnBuffer` 元素的 `PredictedSpawnEntity`，并从 `PredictedGhostSpawn` 缓冲区删除相应条目

```csharp
public void Execute(DynamicBuffer<GhostSpawnBuffer> ghosts,
    DynamicBuffer<SnapshotDataBuffer> data)
{
    var predictedSpawnList = PredictedSpawnListLookup[spawnListEntity];
    for (int i = 0; i < ghosts.Length; ++i)
    {
        var newGhostSpawn = ghosts[i];
        if (newGhostSpawn.SpawnType != GhostSpawnBuffer.Type.Predicted ||
            newGhostSpawn.HasClassifiedPredictedSpawn ||
            newGhostSpawn.PredictedSpawnEntity != Entity.Null)
            continue;

        // 即使对象不是由本机预测生成，也要把该类型的所有生成标记为已分类
        // 否则默认分类系统运行时，可能错误选中其他玩家生成的对象
        if (newGhostSpawn.GhostType == ghostType)
            newGhostSpawn.HasClassifiedPredictedSpawn = true;

        // 查找快照中的新 Ghost，并与本系统处理的预测生成类型匹配
        // 匹配函数可以通过 SnapshotDataBufferLookup 检查所接收快照中的组件
        for (int j = 0; j < predictedSpawnList.Length; ++j)
        {
            if (newGhostSpawn.GhostType != predictedSpawnList[j].ghostType)
                continue;

            if (YOUR_FUZZY_MATCH(newGhostSpawn, predictedSpawnList[j]))
            {
                newGhostSpawn.PredictedSpawnEntity = predictedSpawnList[j].entity;
                predictedSpawnList[j] = predictedSpawnList[predictedSpawnList.Length - 1];
                predictedSpawnList.RemoveAt(predictedSpawnList.Length - 1);
                break;
            }
        }

        ghosts[i] = newGhostSpawn;
    }
}
```

分类系统可以使用 `SnapshotDataBufferLookup`：

- 检查 Ghost Archetype 中是否存在某个组件
- 从新 Ghost 对应的快照数据中读取任意已复制组件类型

<a id="pre-spawned"></a>
<a id="pre-spawned-ghosts"></a>

## 预生成 Ghost

直接保存在 SubScene 中的 Ghost 称为预生成 Ghost。当客户端和服务器 World 加载 SubScene 时，它们会随场景生成。如果预生成 Ghost 在服务器上的值与最初烘焙到 SubScene 时相比没有变化，则无需快照更新即可在客户端激活，系统会把它视为已经确认。即使存在少量变更，这些变更也会相对 SubScene 基线进行 Delta 压缩

预生成最适合以下 Ghost：

- **持久对象**：生命周期与 SubScene 相同。销毁预生成 Ghost 再用新实例替换的效率很低，因为后来加入的客户端不仅要处理原始预生成对象的销毁，还要把替代对象作为全新 Ghost 复制
- **静态优化对象**：常规情况下大部分数据都能通过 Delta 压缩接近 0，从而获益

适合预生成的对象包括可砍伐并重新生长的树，以及可由玩家开关的门。树木被砍伐时应禁用而不是销毁

> [!NOTE]
> 对预生成 Ghost 使用 [Ghost 相关性](optimizations.md#relevancy)时要注意持久性。与客户端无关的预生成 Ghost 仍需先在该客户端加载，再由服务器标记删除，最后通过快照事件在客户端删除
>
> 因此，无关 Ghost 会遇到与手动销毁预生成对象相同的问题。把预生成 Ghost 视为始终相关，并尽可能使用静态优化，通常效率更高
>
> 另一种方式是把经常无关的预生成对象改为运行时生成，并用仅服务器生成器替换其 SubScene 条目，使客户端一开始就只接收真正相关的 Ghost

在 Unity Editor 中把 Ghost Prefab 放入 SubScene，即可创建预生成 Ghost：

1. 在 Inspector 的 **Hierarchy** 中单击右键，再单击 **New Subscene**
2. 把 Ghost Prefab 实例拖入新建的 SubScene

<img src="images/prespawn-ghost.png" alt="预生成 Ghost" width="700">

<a id="pre-spawned-ghost-limitations"></a>

### 预生成 Ghost 的限制

- 预生成 Ghost 必须是 Ghost Prefab 的实例
- 与其他 Authoring 或烘焙实体一样，预生成 Ghost 必须放入 SubScene，不能直接放在 Scene 中
- 同一 Scene 中的预生成 Ghost 不能与另一个预生成对象拥有完全相同的位置和旋转，因为系统使用 `LocalTransform` 对它们进行确定性排序
- 预生成 Ghost 必须放在主 Scene Section，也就是 Section 0
- 预生成 Ghost 上的 `GhostAuthoringComponent` 不能采用不同于来源 Prefab 的配置，因为这些数据按 Ghost 类型而不是按 Scene 实例处理，所以 Inspector 中会显示为只读。其他 Authoring 数据仍可正常修改

<a id="how-pre-spawned-ghosts-work"></a>

### 预生成 Ghost 的工作原理

在[烘焙阶段](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/manual/baking-overview.html)，每个 SubScene 会为其中的 Ghost 分配 [`PreSpawnedGhostIndex`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSpawnedGhostIndex.html)，作为该 SubScene 内 Ghost 的唯一 ID

系统按确定性哈希对 Ghost 排序。哈希会根据 Ghost 类型或 Prefab ID，以及 Scene Section 的 [`SceneGUID`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SceneSectionData.SceneGUID.html) 区分对象。如果同一 SubScene Section 中存在两个或更多相同类型的 Ghost，系统还会把实体的 `Position` 和 `Rotation` 加入 ID 来保证唯一性。这就是为什么不支持在同一位置预生成两个或更多相同类型的 Ghost

之所以需要这套流程，是因为烘焙或构建时无法为预生成 Ghost 分配全局唯一且确定的 Ghost ID

每个 SubScene 最终会得到一个组合哈希，其中包含所有 Ghost 的计算哈希。系统提取该值并用于：

- 为 Scene 中所有 Ghost 添加 [`SubSceneGhostComponentHash`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SubSceneGhostComponentHash.html) 共享组件，按 SubScene 对预生成 Ghost 分组
- 为 SubScene 的第一个 [`SceneSection`](https://docs.unity3d.com/Packages/com.unity.entities@latest?subfolder=/api/Unity.Entities.SceneSection.html) 添加 [`SubSceneWithPrespawnGhosts`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SubSceneWithPrespawnGhosts.html)，运行时通过该组件处理包含预生成 Ghost 的 SubScene

> [!NOTE]
> 出于安全考虑，每个预生成 Ghost 在烘焙时还会获得 `Disabled` 组件，在运行时完成重新初始化之前对用户系统隐藏。重新初始化包括 Scene 加载和序列化基线计算

运行时加载 SubScene 后，客户端和服务器都会处理它：

- 为每个预生成 Ghost 提取预生成基线，在第一次发送该 Ghost 组件时用于 Delta 压缩，节省带宽
- 服务器为每个 SubScene 分配唯一的 Ghost ID 范围，再结合 [`PreSpawnedGhostIndex`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PreSpawnedGhostIndex.html)，为新实例化的预生成 Ghost 分配不同 ID
- 服务器使用内部 Ghost 实体 `PrespawnSceneList`，复制每个 SubScene 分配到的 ID 范围。SubScene 由 `SubSceneWithPrespawnGhosts` 上的哈希标识
- 客户端加载 SubScene 并收到 Ghost ID 范围后
  - 为每个预生成 Ghost 分配服务器权威 Ghost ID
  - 通过 [RPC](rpcs.md) 告知服务器，它已准备好流式接收这些预生成 Ghost

客户端和服务器处理完 SubScene 并分配 Ghost ID 后，会在主 `SceneSection` 上添加内部组件 `PrespawnsSceneInitialized`

客户端会自动追踪包含预生成 Ghost 的 SubScene 何时加载或卸载，并通知服务器停止向它流式发送已卸载 SubScene 对应的预生成 Ghost

上述预生成 Ghost ID 配置完全自动完成，无需执行额外操作来维持客户端与服务器之间的同步

> [!NOTE]
> 如果预生成 Ghost 在进入游戏前，或在基线正确计算前被移动，数据可能无法正确复制，因为快照 Delta 压缩会失败
>
> 服务器和客户端都会计算基线的循环冗余校验（CRC），并在客户端连接时验证该哈希。不匹配会导致断开连接。这也是 Scene 加载时 Ghost 带有 `Disabled` 的原因

<a id="dynamically-loading-subscenes-with-pre-spawned-ghosts"></a>

#### 动态加载包含预生成 Ghost 的 SubScene

进入游戏后仍可在运行时加载包含预生成 Ghost 的 SubScene，这些 Ghost 会自动处理和同步。也可以按需卸载此类 SubScene，Netcode for Entities 会自动停止向客户端报告其已卸载 Section 中的预生成 Ghost

## 其他资源

- [Ghost 与快照](ghost-snapshots.md)
- [使用 `GhostFieldAttribute` 序列化和同步](ghostfield-synchronize.md)
- [`GhostSpawnSystem` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSpawnSystem.html)
- [预测简介](intro-to-prediction.md)
- [`ClientPopulatePrespawnedGhostsSystem` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientPopulatePrespawnedGhostsSystem.html)
- [`ClientTrackLoadedPrespawnSections` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ClientTrackLoadedPrespawnSections.html)
- [`ServerPopulatePrespawnedGhostsSystem` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ServerPopulatePrespawnedGhostsSystem.html)
- [`ServerTrackLoadedPrespawnSections` API 文档](https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/api/Unity.NetCode.ServerTrackLoadedPrespawnSections.html)
