using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

// 评估转换：只写 Pending，不做结构改动
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct FsmEvaluateSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var context = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (fsm, graphRef, blackboard) in
                 SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>, DynamicBuffer<FsmVar>>())
        {
            ref var f = ref fsm.ValueRW;
            if (f.HasPending == 1) continue; //若处于pending状态，则不进行转换评估

            ref var graph = ref graphRef.ValueRO.Value.Value;
            ref var node = ref graph.States[(int)f.Current];
            var bb = blackboard;

            for (int i = 0; i < node.Transitions.Length; i++)
            {
                // 获取每一个可能的目标节点的转换条件
                var t = node.Transitions[i];
                if (FsmRegistry.InvokeCondition(t.Condition, ref f, ref bb, context)) {
                    f.Next         = t.To;
                    f.PendingExit  = t.OnExit;
                    f.PendingEnter = t.OnEnter;
                    f.HasPending   = 1;
                    break;
                }
            }
        }
    }
}