using Unity.Burst;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;

// 引导系统：初始化/回收注册表
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
public partial struct FsmRegistryBootstrapSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        FsmRegistry.InitIfNeeded();
        state.EntityManager.CreateSingleton(new FsmContext()); // 供 context 注入
    }

    public void OnDestroy(ref SystemState state)
    {
        FsmRegistry.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        var context = SystemAPI.GetSingletonRW<FsmContext>();
        context.ValueRW.DeltaTime = SystemAPI.Time.DeltaTime;
        context.ValueRW.Tick++;
    }
}