using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

// 应用迁移：一次性动作（Exit/Enter），不做增删组件
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmEvaluateSystem))]
public partial struct FsmApplyTransitionSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookupRO;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookupRO = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        state.RequireForUpdate<FsmContext>();
    }
    
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookupRO.Update(ref state);

        var context = SystemAPI.GetSingleton<FsmContext>();
        context.BlackboardLookup = _blackboardLookupRO;

        foreach (var (fsm, entity) in SystemAPI.Query<RefRW<Fsm>>().WithEntityAccess()) 
        {
            ref var f = ref fsm.ValueRW;
            if (f.HasPending == 0) continue; //若未处于pending状态，则不执行转换

            // 回调当前状态退出方法
            FsmRegistry.InvokeAction(f.PendingExit, in entity, ref f, context);

            f.Current     = f.Next;
            f.TimeInState = 0f;

            // 回调目标状态进入方法
            FsmRegistry.InvokeAction(f.PendingEnter, in entity, ref f, context);

            f.PendingExit  = ActionId.None;
            f.PendingEnter = ActionId.None;
            f.HasPending   = 0;
        }
    }
}
