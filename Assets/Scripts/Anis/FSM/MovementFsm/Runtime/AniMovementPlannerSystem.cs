using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public static class AniMovementConfig
{
    // 服务器 TickRate = 60，3 个 Tick ≈ 0.05s
    public const int NavUpdateIntervalTicks = 3;
}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct AniMovementPlannerSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookup;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        state.RequireForUpdate<FsmContext>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);
        var fsmContext = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (localTransform, attributes, entity) in
                 SystemAPI.Query<RefRO<LocalTransform> ,RefRO<AniAttributes>>().WithEntityAccess())
        {
            var blackboard = _blackboardLookup[entity];

            var commandMode = (AniMovementCommandMode)
                Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.K_CommandMode);

            switch (commandMode)
            {
                case AniMovementCommandMode.Idle:
                {
                    HandleIdle(ref blackboard);
                    break;
                }

                case AniMovementCommandMode.Follow:
                {
                    var playerEntity = Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.K_PlayerEntity);
                    if (playerEntity == Entity.Null || !SystemAPI.HasComponent<LocalTransform>(playerEntity))
                    {
                        UnityEngine.Debug.LogError("AniMovementPlannerSystem: Player entity is null or missing LocalTransform.");
                        break;
                    } 

                    var leaderTransform = SystemAPI.GetComponent<LocalTransform>(playerEntity);

                    bool hasFormationMember = SystemAPI.HasComponent<AniFormationMember>(entity);
                    int slotIndex = hasFormationMember
                        ? SystemAPI.GetComponent<AniFormationMember>(entity).slotIndex
                        : 0;

                    HandleFollow(
                        in localTransform.ValueRO,
                        in leaderTransform,
                        hasFormationMember,
                        slotIndex,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                case AniMovementCommandMode.Find:
                {
                    var targetEntity = Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.K_TargetEntity);
                    if (targetEntity == Entity.Null || !SystemAPI.HasComponent<LocalTransform>(targetEntity))
                    {
                        // 找不到目标，直接当作 Idle 处理
                        HandleIdle(ref blackboard);
                        break;
                    }

                    var targetTransform = SystemAPI.GetComponent<LocalTransform>(targetEntity);

                    HandleFind(
                        in localTransform.ValueRO,
                        in targetTransform,
                        in attributes.ValueRO,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                case AniMovementCommandMode.MoveTo:
                {
                    float3 moveToPosition =
                        Blackboard.GetFloat3(ref blackboard, AniMovementBlackboardKeys.K_MoveToPosition);

                    HandleMoveTo(
                        in localTransform.ValueRO,
                        in moveToPosition,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                default:
                {
                    // 未知指令，保守处理成 Idle
                    HandleIdle(ref blackboard);
                    break;
                }
            }
        }
    }

    // 模式级处理函数
    private static void HandleIdle(ref DynamicBuffer<FsmVar> blackboard)
    {
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop,      true);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived,  true);
    }

    private static void HandleFollow(
        in LocalTransform aniTransform,
        in LocalTransform leaderTransform,
        bool hasFormationMember,
        int slotIndex,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float3 desiredWorldPosition;

        if (hasFormationMember)
        {
            float3 localOffset = AniFormationUtility.CalculateRectangularFormationLocalOffset(
                slotIndex,
                AniFormationUtility.FormationColumnCount,
                AniFormationUtility.FormationHorizontalSpacing,
                AniFormationUtility.FormationBackwardSpacing);

            float3 worldOffset =
                AniFormationUtility.RotateLocalOffsetToWorld(localOffset, leaderTransform.Rotation);

            desiredWorldPosition = leaderTransform.Position + worldOffset;
        }
        else
        {
            desiredWorldPosition = leaderTransform.Position;
        }

        // Follow：使用阵列系统的 ArrivalRadius
        float arrivalRadius = AniFormationUtility.ArrivalRadius;

        ApplyDestination(
            aniTransform.Position,
            desiredWorldPosition,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    private static void HandleFind(
        in LocalTransform aniTransform,
        in LocalTransform targetTransform,
        in AniAttributes aniAttributes,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float attackRange    = aniAttributes.AttackRange;
        float arrivalRadius  = attackRange * 0.7f;

        ApplyDestination(
            aniTransform.Position,
            targetTransform.Position,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    private static void HandleMoveTo(
        in LocalTransform aniTransform,
        in float3 moveToPosition,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        const float epsilon = 0.01f;
        float arrivalRadius = epsilon; // 避免浮点误差

        ApplyDestination(
            aniTransform.Position,
            moveToPosition,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    // 公共的 Nav 目标写入
    private static void ApplyDestination(
        in float3 currentPosition,
        in float3 desiredPosition,
        float arrivalRadius,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float3 delta = desiredPosition - currentPosition;
        float distanceSquared = math.lengthsq(delta);

        float arrivalRadiusSq = arrivalRadius * arrivalRadius;
        bool  hasArrived = distanceSquared <= arrivalRadiusSq;

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, hasArrived);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop, hasArrived);

        if (!hasArrived)
        {
            Blackboard.SetFloat3(ref blackboard, AniMovementBlackboardKeys.K_NavTargetPosition, desiredPosition);
            Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavRequestVersion, (int)fsmContext.Tick);
        }

        // 没到达：检查是否到了下次更新 Nav 的时间
        int currentTick = (int)fsmContext.Tick;
        int nextUpdateTick = Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick);

        // 如果还没写过 nextUpdateTick（默认为 0），第一次直接更新
        bool shouldUpdateNav = (nextUpdateTick == 0) || (currentTick >= nextUpdateTick);

        if (!shouldUpdateNav)
        {
            // 还没到更新时间，直接返回
            return;
        }

        // 更新目的地
        Blackboard.SetFloat3(ref blackboard, AniMovementBlackboardKeys.K_NavTargetPosition, desiredPosition);
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavRequestVersion, currentTick);

        // 记录下一次刷新时间
        int newNextTick = currentTick + AniMovementConfig.NavUpdateIntervalTicks;
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick, newNextTick);
    }
}
