using AnimarsCatcher.Core.Fsm;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Entities;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 创建全局状态机上下文并管理函数指针注册表生命周期
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial struct FsmRegistryBootstrapSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.EntityManager.CreateSingleton(new FsmContext()); // 供运行时系统注入时间和黑板查询
            FsmRegistry.Initialize();
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
}
