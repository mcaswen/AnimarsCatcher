using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

/// <summary>
/// 在服务器应用已经选定的迁移，依次执行退出动作、切换状态和进入动作
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmEvaluateSystem))]
public partial struct FsmApplyTransitionSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookupRO;

    /// <summary>
    /// 缓存可写黑板查询并等待状态机上下文
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        _blackboardLookupRO = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        state.RequireForUpdate<FsmContext>();
    }
    
    /// <summary>
    /// 一次性消费 Pending 迁移且不执行结构组件变更
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookupRO.Update(ref state);

        var context = SystemAPI.GetSingleton<FsmContext>();
        context.BlackboardLookup = _blackboardLookupRO;

        foreach (var (fsm, entity) in SystemAPI.Query<RefRW<Fsm>>().WithEntityAccess()) 
        {
            ref var f = ref fsm.ValueRW;
            if (f.HasPending == 0) continue; // 只有评估阶段选中的迁移才能进入应用阶段

            // 退出动作仍在旧状态上下文中执行
            FsmRegistry.InvokeAction(f.PendingExit, in entity, ref f, context);

            f.Current     = f.Next;
            f.TimeInState = 0f;

            // 状态切换完成后再执行目标状态进入动作
            FsmRegistry.InvokeAction(f.PendingEnter, in entity, ref f, context);

            f.PendingExit  = ActionId.None;
            f.PendingEnter = ActionId.None;
            f.HasPending   = 0;
        }
    }
}
