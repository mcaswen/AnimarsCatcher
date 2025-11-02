using Unity.Entities;
using Unity.Burst;
using Unity.NetCode;
using UnityEngine.SocialPlatforms;
using Unity.Transforms;
[BurstCompile]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct FollowPlannerSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var context = SystemAPI.GetSingleton<FsmContext>();
        foreach (var blackboard in SystemAPI.Query<DynamicBuffer<FsmVar>>())
        {
            var bb = blackboard;

            if (Blackboard.GetBool(ref bb, BlasterAniBlackBoardKeys.K_IsFollowing)) // 当前处在 Follow 模式
            {
                if (!SystemAPI.TryGetSingletonEntity<NetworkStreamInGame>(out var connection)) return;
                var playerTargetEntity = SystemAPI.GetComponent<CommandTarget>(connection).targetEntity;

                var playerPosition = SystemAPI.GetComponent<LocalTransform>(playerTargetEntity).Position;
                
                Blackboard.SetFloat3(ref bb, BlasterAniBlackBoardKeys.K_NavTargetPosition, playerPosition);
                Blackboard.SetBool(ref bb, BlasterAniBlackBoardKeys.K_NavStop, false);
                Blackboard.SetInt(ref bb, BlasterAniBlackBoardKeys.K_NavRequestVersion, (int)context.Tick);
            }
        }
        
    }
}