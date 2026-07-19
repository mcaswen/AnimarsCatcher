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
        /// <summary>
        /// 创建上下文单例并初始化持久化函数指针表
        /// </summary>
        /// <param name="state">系统运行状态</param>
        public void OnCreate(ref SystemState state)
        {
            state.EntityManager.CreateSingleton(new FsmContext()); // 供运行时系统注入时间和黑板查询
            FsmRegistry.Initialize();
        }

        /// <summary>
        /// 在世界销毁时释放函数指针注册表
        /// </summary>
        /// <param name="state">系统运行状态</param>
        public void OnDestroy(ref SystemState state)
        {
            FsmRegistry.Dispose();
        }

        /// <summary>
        /// 每帧更新所有状态机共享的时间增量和单调 Tick
        /// </summary>
        /// <param name="state">系统运行状态</param>
        public void OnUpdate(ref SystemState state)
        {
            var context = SystemAPI.GetSingletonRW<FsmContext>();
            context.ValueRW.DeltaTime = SystemAPI.Time.DeltaTime;
            context.ValueRW.Tick++;
        }
    }
}
