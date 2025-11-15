using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

// 评估转换：只写 Pending，不做结构改动
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct FsmEvaluateSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookupRO;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookupRO = state.GetBufferLookup<FsmVar>(isReadOnly: true);
        state.RequireForUpdate<FsmContext>(); 
    }

    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookupRO.Update(ref state);

        var context = SystemAPI.GetSingleton<FsmContext>();
        context.BlackboardLookup = _blackboardLookupRO;

        foreach (var (fsm, graphRef, entity) in
                 SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>>()
                 .WithEntityAccess())
        {
            ref var f = ref fsm.ValueRW;
            if (f.HasPending == 1) continue; //若处于pending状态，则不进行转换评估

            ref var graph = ref graphRef.ValueRO.Value.Value;
            ref var node = ref graph.States[(int)f.Current];

            for (int i = 0; i < node.Transitions.Length; i++)
            {
                // 获取每一个可能的目标节点的转换条件
                var transition = node.Transitions[i];
                if (FsmRegistry.InvokeCondition(transition.Condition, entity, context)) {
                    f.Next         = transition.To;
                    f.PendingExit  = transition.OnExit;
                    f.PendingEnter = transition.OnEnter;
                    f.HasPending   = 1;
                    break;
                }
            }
        }
    }
}