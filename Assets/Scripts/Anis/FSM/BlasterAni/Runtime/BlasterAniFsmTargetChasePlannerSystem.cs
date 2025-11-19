using Unity.Entities;
using Unity.Burst;
using Unity.NetCode;
using UnityEngine.SocialPlatforms;
using Unity.Transforms;
using Unity.Mathematics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct BlasterAniFsmTargetChasePlannerSystem : ISystem
{

    private BufferLookup<FsmVar> blackboardLookup;

    public void OnCreate(ref SystemState state)
    {
        blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        state.RequireForUpdate<FsmContext>();
    }

    public void OnUpdate(ref SystemState state)
    {
        blackboardLookup.Update(ref state);
        var fsmContext = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (localTransform, entity) in
                 SystemAPI.Query<RefRO<LocalTransform>>().WithAll<AniAttributes>().WithEntityAccess())
        {
            var blackboard = blackboardLookup[entity];

            int commandMode = Blackboard.GetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_CommandMode);

            // 判断当前指令，获取追踪目标
            Entity chaseEntity;
            bool useFormationOffset;

            if (commandMode == (int)BlasterAniFsmCommandMode.Follow)
            {
                chaseEntity = Blackboard.GetEntity(ref blackboard, BlasterAniBlackFsmBoardKeys.K_PlayerEntity);
                useFormationOffset = true;
            }
            else if (commandMode == (int)BlasterAniFsmCommandMode.Find)
            {
                chaseEntity = Blackboard.GetEntity(ref blackboard, BlasterAniBlackFsmBoardKeys.K_TargetEntity);
                useFormationOffset = false;
            }
            else continue;
            
            if (chaseEntity == Entity.Null || !SystemAPI.HasComponent<LocalTransform>(chaseEntity))
                continue;

            // 计算目标位置
            var chaseTransform = SystemAPI.GetComponent<LocalTransform>(chaseEntity);

            float3 desiredWorldPosition;
            if (useFormationOffset)
            {
                int slotIndex = SystemAPI.HasComponent<AniFormationMember>(entity)
                    ? SystemAPI.GetComponent<AniFormationMember>(entity).slotIndex : 0;

                float3 localOffset = AniFormationUtility.CalculateRectangularFormationLocalOffset(
                    slotIndex,
                    AniFormationUtility.FormationColumnCount,
                    AniFormationUtility.FormationHorizontalSpacing,
                    AniFormationUtility.FormationBackwardSpacing);

                float3 worldOffset = AniFormationUtility.RotateLocalOffsetToWorld(localOffset, chaseTransform.Rotation);
                desiredWorldPosition = chaseTransform.Position + worldOffset;
            }
            else
            {
                desiredWorldPosition = chaseTransform.Position;
            }

            // 写回导航目标
            float3 currentWorldPosition = localTransform.ValueRO.Position;
            float distanceSquared = math.lengthsq(desiredWorldPosition - currentWorldPosition);
            bool hasArrived = distanceSquared <= AniFormationUtility.ArrivalRadius * AniFormationUtility.ArrivalRadius;

            Blackboard.SetBool(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavStop, hasArrived);
            if (!hasArrived)
            {
                Blackboard.SetFloat3(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavTargetPosition, desiredWorldPosition);
                Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, (int)fsmContext.Tick);
            }
        }
    }
}