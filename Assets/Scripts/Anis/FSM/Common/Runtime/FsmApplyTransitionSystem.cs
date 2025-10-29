using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

// 应用迁移：一次性动作（Exit/Enter），不做增删组件
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmEvaluateSystem))]
public partial struct FsmApplyTransitionSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var context = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (fsm, blackboard) in SystemAPI.Query<RefRW<Fsm>, DynamicBuffer<FsmVar>>()) 
        {
            ref var f = ref fsm.ValueRW;
            if (f.HasPending == 0) continue; //若未处于pending状态，则不执行转换

            var bb = blackboard;

            // 回调当前状态退出方法
            FsmRegistry.InvokeAction(f.PendingExit, ref f, ref bb, context);

            f.Current     = f.Next;
            f.TimeInState = 0f;

            // 回调目标状态进入方法
            FsmRegistry.InvokeAction(f.PendingEnter, ref f, ref bb, context);

            f.PendingExit  = ActionId.None;
            f.PendingEnter = ActionId.None;
            f.HasPending   = 0;
        }
    }
}
