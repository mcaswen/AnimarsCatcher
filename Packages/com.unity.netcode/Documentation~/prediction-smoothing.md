# 预测平滑

预测误差可能由多种原因造成，例如客户端与服务器之间的逻辑差异、数据包丢失和量化误差等。对于预测实体，其最终表现是：从最近可用快照回滚并重新模拟时，重新计算的值与原先预测的值之间可能存在明显差异。`GhostPredictionSmoothingSystem` 系统提供了一种随时间校正并减小这些误差的方法，使两种状态之间的转换更加平滑

对于每种组件，可以在 `GhostPredictionSmoothing` 单例上注册 `Smoothing Action Function`，配置如何随时间平滑处理这些误差

```c#
    public delegate void SmoothingActionDelegate(void* currentData, void* previousData, void* userData);
    // 将 null 作为用户数据传入
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation>(EntityManager, MySmoothingAction);
    // 将指向 MySmoothingActionParams Chunk 组件的指针作为用户数据传入
    GhostPredictionSmoothing.RegisterSmoothingAction<Translation, MySmoothingActionParams>(EntityManager, DefaultTranslateSmoothingAction.Action);
```

用户数据必须是实体上存在的 Chunk 组件。本包提供了用于平滑处理任意位置预测误差的默认实现

```c#
world.GetSingleton<GhostPredictionSmoothing>().RegisterSmootingAction<Translation>(EntityManager, CustomSmoothing.Action);

[BurstCompile]
public unsafe class CustomSmoothing
{
    public static readonly PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>
        Action =
            new PortableFunctionPointer<GhostPredictionSmoothing.SmoothingActionDelegate>(SmoothingAction);

    [BurstCompile(DisableDirectCall = true)]
    private static void SmoothingAction(void* currentData, void* previousData, void* userData)
    {
        ref var trans = ref UnsafeUtility.AsRef<Translation>(currentData);
        ref var backup = ref UnsafeUtility.AsRef<Translation>(previousData);

        var dist = math.distance(trans.Value, backup.Value);
        //UnityEngine.Debug.Log($"自定义平滑，差值 {trans.Value - backup.Value}，距离 {dist}");
        if (dist > 0)
            trans.Value = backup.Value + (trans.Value - backup.Value) / dist;
    }
}
```

<a id="smoothing-frequency"></a>
## 平滑频率

`PredictionSmoothingSystem` 不会在每一个渲染帧都校正预测误差。只有客户端收到服务器发来的新快照，其中包含预测 Ghost，并进行状态校正时，才会调用已注册的校正操作

重新模拟期间，当当前服务器 Tick 等于最近一次预测的完整 Tick 时，系统会对所有 Ghost 应用平滑校正

> [!NOTE]
> 平滑操作运行的唯一条件是客户端回滚并重新模拟其状态，这要求收到预测 Ghost 状态更新。对单个实体应用校正与最近一个数据包中是否包含该实体的状态更新没有关联

<a id="limitations-and-known-issues"></a>
## 限制与已知问题

* 校正质量取决于收到预测 Ghost 数据的频率
   * 连接抖动和延迟突增会影响预测校正
   * 大量复制 Ghost，无论是预测还是插值，或者一段时间内未收到预测 Ghost 更新，也会影响校正频率
* 结构性变化可能阻止系统应用校正，例如在预测 Ghost 实体上添加或移除组件。自上次备份预测历史以来，如果实体更换了 Chunk，或仍位于同一 Chunk 但位置已经移动，则不会应用预测平滑
* 预测平滑以函数指针回调为基础，因此平滑函数实现内部可能没有足够的上下文或灵活性来应用所需逻辑。`Smoothing Action` 委托应保持简单且无状态

## 其他资源

* [预测简介](intro-to-prediction.md)
* [使用预测管理延迟](prediction-n4e.md)
