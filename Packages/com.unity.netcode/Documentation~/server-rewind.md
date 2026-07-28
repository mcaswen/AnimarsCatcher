# 服务器回溯

在游戏中设置服务器回溯，降低延迟对多人玩法的影响

服务器回溯是一种允许服务器将游戏状态回退到较早时间点的技术，用于验证高延迟客户端发来的信息。它也称为延迟补偿

可以将服务器回溯与[预测](intro-to-prediction.md)结合使用，进一步降低延迟对游戏的影响

<a id="introduction-to-server-side-rewind"></a>
## 服务器回溯简介

玩家在客户端与对象交互时，例如向敌人射击，看到的是这些对象的[插值](interpolation.md)版本，而玩家自身通常是[预测](intro-to-prediction.md)的。预测时间线与插值[时间线](interpolation.md#timelines)之间的差异会随延迟变化，通常可能达到数百毫秒。如果不进行干预，这会引发严重的玩法问题，例如玩家必须提前瞄准。玩家行为也很难预测，尤其是他们正在躲避攻击时，因此仅依靠客户端预测通常无法产生一致体验

服务器回溯是处理这种潜在偏差的推荐方法。服务器按 Tick 保存游戏状态历史，便可将来自不同时间线的客户端输入与对应历史 Tick 进行验证，相当于为了验证而将服务器状态回退到与客户端一致的时刻。输入验证完成后，服务器会把必要变化更新到当前状态中，例如扣除生命值或判定玩家死亡，再将这些变化同步给客户端

还可以在特定游戏场景中选择性禁用服务器回溯，例如玩家处于无敌状态或正在使用闪避能力时

<a id="implement-server-side-rewind"></a>
## 实现服务器回溯

若要在项目中实现服务器回溯，需要从 `PhysicsWorldHistorySingleton` 组件获取碰撞历史。该组件保存服务器物理状态历史。随后使用 [`CommandDataInterpolationDelay`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.CommandDataInterpolationDelay.html) 提供的延迟值，判断服务器应回退多少时间来验证客户端输入。客户端和服务器可以使用相同的碰撞计算逻辑，但客户端计算输入时不带延迟

以下代码展示一种服务器回溯逻辑实现。完整上下文请参阅 [`ShootingSystem` 示例](https://github.com/Unity-Technologies/EntityComponentSystemSamples/blob/master/NetcodeSamples/Assets/Samples/HelloNetcode/2_Intermediate/03_HitScanWeapon/ShootingSystem.cs)

```csharp
var collisionHistory = SystemAPI.GetSingleton<PhysicsWorldHistorySingleton>();
var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
var networkTime = SystemAPI.GetSingleton<NetworkTime>();
var ghostComponentFromEntity = SystemAPI.GetComponentLookup<GhostInstance>();
var localToWorldFromEntity = SystemAPI.GetComponentLookup<LocalToWorld>();
var lagCompensationEnabledFromEntity = SystemAPI.GetComponentLookup<LagCompensationEnabled>();
var predictingTick = networkTime.ServerTick;
// 回滚时不执行命中扫描，只在模拟最新 Tick 时执行
if (!networkTime.IsFirstTimeFullyPredictingTick)
    return;

foreach (var (character, interpolationDelay, hitComponent) in SystemAPI.Query<CharacterAspect, RefRO<CommandDataInterpolationDelay>, RefRW<Hit>>().WithAll<Simulate>())
{
    if (character.Input.SecondaryFire.IsSet)
    {
        hitComponent.ValueRW.Victim = character.Self;
        hitComponent.ValueRW.Tick = predictingTick;
        continue;
    }
    if (!character.Input.PrimaryFire.IsSet)
    {
        continue;
    }

    // 获取服务器 Tick T 的 CollisionWorld 时，需要考虑用户实际上在上一 Tick 提交了该输入
    // 更准确地说，是在上一渲染帧提交
    const int additionalRenderDelay = 1;

    // 时序拆解：
    // - 客户端正在预测 ServerTick 100，以此为例
    // - InterpolationDelay 为 2 个 Tick
    // - 假定渲染延迟为 1 个 Tick；受双/三缓冲、流水线、显示器刷新与绘制延迟影响，实际可能超过 1
    // - 客户端视觉上看到 Tick 97，其中渲染延迟减 1，延迟补偿减 2
    // - CommandDataInterpolationTick.Delay 是 CurrentCommand.Tick 与 InterpolationTick 的差值，因此为 -2
    //   换言之，其中已经计入 InterpolationDelay
    // - 服务器在 ServerTick 100 处理该输入
    // - 应用 CommandDataInterpolationTick.Delay -2 后得到 98
    // - 因此服务器还需要减去渲染延迟，才能与客户端看到并用于查询的 Tick 97 保持一致
    var delay = lagCompensationEnabledFromEntity.HasComponent(character.Self)
        ? interpolationDelay.ValueRO.Delay + additionalRenderDelay
        : additionalRenderDelay;

    collisionHistory.GetCollisionWorldFromTick(predictingTick, delay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);

    bool hit = collWorld.CastRay(rayInput, out var closestHit);
}
```

<a id="implementation-considerations"></a>
### 实现注意事项

实现服务器回溯时，建议将服务器状态历史限制在 250 到 500ms 的备份范围内。否则，低延迟和高延迟客户端的时间线差异过大，可能导致玩家体验下降，例如从玩家视角看已经躲到墙后仍然中弹

<a id="test-server-side-rewind"></a>
## 测试服务器回溯

通过人为增加延迟并观察客户端与服务器行为来测试服务器回溯实现。通常测试 50ms、150ms 和 500ms 延迟，已经足以覆盖大部分常见网络条件。可以使用 Netcode for Entities PlayMode Tool 在项目中[模拟网络条件](playmode-tool.md#emulate-client-network-conditions)

可以手动测试，也可以使用机器人模拟客户端输入，检查服务器回溯是否按预期工作。以射击示例为例，可以添加测试代码，分别统计客户端和服务器的命中数，然后确保两者保持接近，并在停止射击后收敛

<a id="limitations"></a>
## 限制

Netcode for Entities 只保存服务器物理状态的备份。如果玩家还有其他会影响操作结果的状态，例如无敌状态，则还需要根据项目玩法按 Tick 跟踪这些状态的历史

## 其他资源

* [预测](intro-to-prediction.md)
