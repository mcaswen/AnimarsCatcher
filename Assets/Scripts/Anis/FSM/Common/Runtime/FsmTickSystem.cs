using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct FsmTickSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var context = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (fsm, graphRef, blackboard) in
                 SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>, DynamicBuffer<FsmVar>>())
        {
            // 更新上下文
            ref var f = ref fsm.ValueRW;
            f.TimeInState += context.DeltaTime;

            ref var graph = ref graphRef.ValueRO.Value.Value;
            ref var node = ref graph.States[(int)f.Current];
            var bb = blackboard;

            // 执行状态更新
            if (node.OnUpdate != ActionId.None) {
                FsmRegistry.InvokeAction(node.OnUpdate, ref f, ref bb, context);
            }
        }
    }
}