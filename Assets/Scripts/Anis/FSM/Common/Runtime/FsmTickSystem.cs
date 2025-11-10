using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct FsmTickSystem : ISystem
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
                 SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>>().WithEntityAccess())
        {
            // 更新上下文
            ref var f = ref fsm.ValueRW;
            f.TimeInState += context.DeltaTime;

            ref var graph = ref graphRef.ValueRO.Value.Value;
            ref var node = ref graph.States[(int)f.Current];

            // 执行状态更新
            if (node.OnUpdate != ActionId.None) {
                FsmRegistry.InvokeAction(node.OnUpdate, in entity, ref f, context);
            }
        }
    }
}