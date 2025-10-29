using Unity.Entities;
using Unity.Burst;
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct FollowPlannerSystem : ISystem
{
    public void OnUpdate(ref SystemState s)
    {
        var context = SystemAPI.GetSingleton<FsmContext>();
        foreach (var blackboard in SystemAPI.Query<DynamicBuffer<FsmVar>>())
        {
            var bb = blackboard;
            if (Blackboard.GetBool(ref bb, BlasterAniBlackBoardKeys.K_IsFollow)) // 当前处在 Follow 模式
            {
                var playerPosition = Blackboard.GetFloat3(ref bb, BlasterAniBlackBoardKeys.K_PlayerPosition);
                Blackboard.SetFloat3(ref bb, BlasterAniBlackBoardKeys.K_NavTargetPosition, playerPosition);
                Blackboard.SetBool(ref bb, BlasterAniBlackBoardKeys.K_NavStop, false);
                Blackboard.SetInt(ref bb, BlasterAniBlackBoardKeys.K_NavRequestTick, (int)context.Tick);
            }
        }
        
    }
}