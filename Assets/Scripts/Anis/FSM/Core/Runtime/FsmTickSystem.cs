using AnimarsCatcher.Core.Fsm;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

/// <summary>
/// 在迁移应用后更新状态停留时间并执行当前状态的持续动作
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct FsmTickSystem : ISystem
{
    private BufferLookup<FsmVar> _writableBlackboardLookup;

    /// <summary>
    /// 缓存可写黑板查询并等待状态机上下文
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        _writableBlackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        state.RequireForUpdate<FsmContext>();
    }

    /// <summary>
    /// 累加状态停留时间并调用已注册的 OnUpdate 动作
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnUpdate(ref SystemState state)
    {
        _writableBlackboardLookup.Update(ref state);

        var context = SystemAPI.GetSingleton<FsmContext>();
        context.BlackboardLookup = _writableBlackboardLookup;

        foreach (var (fsm, graphRef, entity) in
                 SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>>().WithEntityAccess())
        {
            // 状态停留时间在迁移完成后的当前状态上累加
            ref var fsmData = ref fsm.ValueRW;
            fsmData.TimeInState += context.DeltaTime;

            ref var graph = ref graphRef.ValueRO.Value.Value;
            ref var node = ref graph.States[(int)fsmData.Current];

            // 未配置持续动作的状态只更新时间数据
            if (node.OnUpdate != ActionId.None) {
                FsmRegistry.InvokeAction(node.OnUpdate, in entity, ref fsmData, context);
            }
        }
    }
}
