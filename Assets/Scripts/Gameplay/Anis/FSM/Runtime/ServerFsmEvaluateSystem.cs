using AnimarsCatcher.Core.Fsm;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;
using Unity.Burst;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器按声明顺序评估当前状态的迁移条件并记录首个匹配结果
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerFsmEvaluateSystem : ISystem
    {
        private BufferLookup<FsmVar> _writableBlackboardLookup;

        public void OnCreate(ref SystemState state)
        {
            _writableBlackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
            state.RequireForUpdate<FsmContext>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _writableBlackboardLookup.Update(ref state);

            var context = SystemAPI.GetSingleton<FsmContext>();
            context.BlackboardLookup = _writableBlackboardLookup;

            foreach (var (fsm, graphRef, entity) in
                     SystemAPI.Query<RefRW<Fsm>, RefRO<FsmGraphRef>>()
                     .WithEntityAccess())
            {
                ref var fsmData = ref fsm.ValueRW;
                if (fsmData.HasPending == 1) continue; // 待应用迁移尚未消费时不能再次评估

                ref var graph = ref graphRef.ValueRO.Value.Value;
                ref var node = ref graph.States[(int)fsmData.Current];

                for (int i = 0; i < node.Transitions.Length; i++)
                {
                    // 边的声明顺序同时定义条件优先级，首个满足条件的边获胜
                    var transition = node.Transitions[i];
                    if (FsmRegistry.InvokeCondition(transition.Condition, entity, context)) {
                        fsmData.Next         = transition.To;
                        fsmData.PendingExit  = transition.OnExit;
                        fsmData.PendingEnter = transition.OnEnter;
                        fsmData.HasPending   = 1;
                        break;
                    }
                }
            }
        }
    }
}
