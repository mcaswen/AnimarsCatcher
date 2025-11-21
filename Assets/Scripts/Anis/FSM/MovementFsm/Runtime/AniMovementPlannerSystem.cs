using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
// using Unity.NetCode; // 如果 FsmContext 是 NetCode 里的的话，记得加

public struct AniMovementConfig
{
    public const int NavUpdateIntervalTicks = 5; // 每隔多少 Tick 更新一次导航目标
}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
public partial struct AniMovementPlannerSystem : ISystem
{
    private BufferLookup<FsmVar>       _blackboardLookup;
    private ComponentLookup<PickerAniTag>  _pickerLookup;
    private ComponentLookup<BlasterAniTag> _blasterLookup;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        _pickerLookup = state.GetComponentLookup<PickerAniTag>(isReadOnly: true);
        _blasterLookup = state.GetComponentLookup<BlasterAniTag>(isReadOnly: true);

        state.RequireForUpdate<FsmContext>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);
        _pickerLookup.Update(ref state);
        _blasterLookup.Update(ref state);

        var fsmContext = SystemAPI.GetSingleton<FsmContext>();

        foreach (var (transform, attributes, entity) in
                 SystemAPI.Query<RefRO<LocalTransform>, RefRO<AniAttributes>>()
                     .WithEntityAccess())
        {
            if (!_blackboardLookup.HasBuffer(entity))
                continue;

            var blackboard = _blackboardLookup[entity];

            var commandMode = (AniMovementCommandMode)
                Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.K_CommandMode);

            bool hasFormationMember = SystemAPI.HasComponent<AniFormationMember>(entity);
            Entity leaderEntity = Entity.Null;
            int slotIndex  = 0;

            if (hasFormationMember)
            {
                var member = SystemAPI.GetComponent<AniFormationMember>(entity);
                leaderEntity = member.leader; // leader = 控制它的玩家实体
                slotIndex   = member.slotIndex;
            }

            bool isPicker  = _pickerLookup.HasComponent(entity);
            bool isBlaster = _blasterLookup.HasComponent(entity);
            float attackRange = attributes.ValueRO.AttackRange;

            switch (commandMode)
            {
                case AniMovementCommandMode.Idle:
                {
                    HandleIdle(ref blackboard);
                    break;
                }

                case AniMovementCommandMode.Follow:
                {
                    if (!hasFormationMember ||
                        leaderEntity == Entity.Null ||
                        !SystemAPI.HasComponent<LocalTransform>(leaderEntity))
                    {
                        HandleIdle(ref blackboard);
                        break;
                    }

                    var leaderTransform = SystemAPI.GetComponent<LocalTransform>(leaderEntity);

                    HandleFollow(
                        in transform.ValueRO,
                        in leaderTransform,
                        slotIndex,
                        isPicker,
                        isBlaster,
                        attackRange,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                case AniMovementCommandMode.Find:
                {
                    if (!hasFormationMember ||
                        leaderEntity == Entity.Null ||
                        !SystemAPI.HasComponent<LocalTransform>(leaderEntity))
                    {
                        HandleIdle(ref blackboard);
                        break;
                    }

                    var targetEntity =
                        Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.K_TargetEntity);

                    if (targetEntity == Entity.Null ||
                        !SystemAPI.HasComponent<LocalTransform>(targetEntity))
                    {
                        HandleIdle(ref blackboard);
                        break;
                    }

                    var leaderTransform = SystemAPI.GetComponent<LocalTransform>(leaderEntity);
                    var targetTransform = SystemAPI.GetComponent<LocalTransform>(targetEntity);

                    HandleFind(
                        in transform.ValueRO,
                        in leaderTransform,
                        in targetTransform,
                        slotIndex,
                        isPicker,
                        isBlaster,
                        in attributes.ValueRO,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                case AniMovementCommandMode.MoveTo:
                {
                    if (!hasFormationMember ||
                        leaderEntity == Entity.Null ||
                        !SystemAPI.HasComponent<LocalTransform>(leaderEntity))
                    {
                        UnityEngine.Debug.LogWarning($"[AniMovementPlannerSystem] Ani Entity={entity.Index} has no valid leader or leader transform.");

                        HandleIdle(ref blackboard);
                        break;
                    }

                    UnityEngine.Debug.Log($"[AniMovementPlannerSystem] Handling MoveTo command for Ani Entity={entity.Index}");

                    var leaderTransform = SystemAPI.GetComponent<LocalTransform>(leaderEntity);

                    HandleMoveTo(
                        in transform.ValueRO,
                        in leaderTransform,
                        slotIndex,
                        isPicker,
                        isBlaster,
                        in attributes.ValueRO,
                        ref blackboard,
                        in fsmContext);

                    break;
                }

                default:
                {
                    HandleIdle(ref blackboard);
                    break;
                }
            }
        }
    }

    // ================= 通用逻辑 =================

    private static void HandleIdle(ref DynamicBuffer<FsmVar> blackboard)
    {
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop,     true);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, true);
    }

    /// <summary>
    /// 给定阵型中心和朝向，基于 slotIndex 算出 Ani 目标位置，并写入 Nav blackboard。
    /// 模式：targetPoint(=formationCenterBase) 已经算好，
    /// 所有“前排/后排”都通过 localOffset → Rotate → 加到 formationCenter 上。
    /// </summary>
    private static void PlanFormationMovement(
        in float3 aniPosition,
        in float3 formationCenter,
        in quaternion formationRotation,
        int slotIndex,
        float arrivalRadius,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float3 desiredPosition = formationCenter;

        if (slotIndex >= 0)
        {
            float3 localOffset = AniFormationUtility.CalculateRectangularFormationLocalOffset(
                slotIndex,
                AniFormationUtility.FormationColumnCount,
                AniFormationUtility.FormationHorizontalSpacing,
                AniFormationUtility.FormationBackwardSpacing);

            float3 worldOffset =
                AniFormationUtility.RotateLocalOffsetToWorld(localOffset, formationRotation);

            desiredPosition = formationCenter + worldOffset;
        }

        ApplyDestination(
            aniPosition,
            desiredPosition,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    // 由“leader 位置 + 目标位置”推导阵型朝向（前向 = leader → target）。
    private static quaternion ComputeFormationRotationFromLeaderToTarget(
        float3 leaderPosition,
        float3 targetPosition,
        in quaternion leaderFallbackRotation)
    {
        float3 dir = targetPosition - leaderPosition;
        dir.y = 0f;

        if (math.lengthsq(dir) < 0.0001f)
        {
            // leader 和目标几乎重合，就用 leader 自己朝向
            return leaderFallbackRotation;
        }

        float3 forward = math.normalize(dir);
        return quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
    }

    // =============== Follow（目标点 = 玩家脚下） ===============

    private static void HandleFollow(
        in LocalTransform aniTransform,
        in LocalTransform leaderTransform,
        int slotIndex,
        bool isPicker,
        bool isBlaster,
        float attackRange,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float3 leaderPos     = leaderTransform.Position;
        quaternion rotation  = leaderTransform.Rotation;
        float3 forward       = math.mul(rotation, new float3(0, 0, 1));

        // “目标点”定义为玩家脚下，然后统一从 targetPoint 往 -forward 偏移
        float3 targetPoint = leaderPos;

        float backOffset = 0f;
        if (isPicker)
        {
            backOffset = AniFormationUtility.PickerFollowBackOffset;
        }
        else if (isBlaster)
        {
            backOffset = attackRange * AniFormationUtility.BlasterFollowBackFactor;
        }

        float3 formationCenter = targetPoint - forward * backOffset;
        quaternion formationRotation = rotation;

        float arrivalRadius = AniFormationUtility.ArrivalRadius;

        PlanFormationMovement(
            aniTransform.Position,
            formationCenter,
            formationRotation,
            slotIndex,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    // =============== Find（目标点 = 敌人位置） ===============

    private static void HandleFind(
        in LocalTransform aniTransform,
        in LocalTransform leaderTransform,
        in LocalTransform targetTransform,
        int slotIndex,
        bool isPicker,
        bool isBlaster,
        in AniAttributes aniAttributes,
        ref DynamicBuffer<FsmVar> blackboard,
        in FsmContext fsmContext)
    {
        float3 leaderPos  = leaderTransform.Position;
        float3 targetPos  = targetTransform.Position;

        quaternion formationRotation =
            ComputeFormationRotationFromLeaderToTarget(leaderPos, targetPos, leaderTransform.Rotation);

        float3 forward = math.mul(formationRotation, new float3(0, 0, 1));

        // 统一规则：目标点 = 敌人位置，然后从目标点沿 -forward 偏移
        float3 targetPoint = targetPos;

        float backOffset = 0f;
        if (isBlaster)
        {
            backOffset = aniAttributes.AttackRange * AniFormationUtility.BlasterFindBackFactor; // 建议 0.5f
        }

        float3 formationCenter = targetPoint - forward * backOffset;

        float arrivalRadius = aniAttributes.AttackRange * 0.7f;

        PlanFormationMovement(
            aniTransform.Position,
            formationCenter,
            formationRotation,
            slotIndex,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    // =============== MoveTo（目标点 = 点击点） ===============

    private static void HandleMoveTo(
    in LocalTransform aniTransform,
    in LocalTransform leaderTransform, // 现在只是兜底用，不再决定朝向
    int slotIndex,
    bool isPicker,
    bool isBlaster,
    in AniAttributes aniAttributes,
    ref DynamicBuffer<FsmVar> blackboard,
    in FsmContext fsmContext)
    {
        // 从黑板里拿“第一次点击时”缓存的阵列锚点
        float3 targetPoint = Blackboard.GetFloat3(ref blackboard,
            AniMovementBlackboardKeys.K_MoveFormationTargetPoint);

        float3 forward = Blackboard.GetFloat3(ref blackboard,
            AniMovementBlackboardKeys.K_MoveFormationForward);

        // 如果 forward 还是默认的 0，说明没被正确初始化，兜底用当前 leader → target 的逻辑
        if (math.lengthsq(forward) < 0.0001f)
        {
            float3 leaderPos = leaderTransform.Position;
            float3 fallbackTarget = Blackboard.GetFloat3(ref blackboard,
                AniMovementBlackboardKeys.K_MoveToPosition);

            float3 dir = fallbackTarget - leaderPos;
            dir.y = 0f;

            if (math.lengthsq(dir) < 0.0001f)
            {
                float3 f = math.mul(leaderTransform.Rotation, new float3(0, 0, 1));
                f.y = 0f;
                if (math.lengthsq(f) < 0.0001f)
                    f = new float3(0, 0, 1);

                forward = math.normalize(f);
            }
            else
            {
                forward = math.normalize(dir);
            }

            targetPoint = fallbackTarget;
        }

        quaternion formationRotation =
            quaternion.LookRotationSafe(forward, new float3(0, 1, 0));

        float backOffset = 0f;
        if (isBlaster)
        {
            backOffset = aniAttributes.AttackRange * AniFormationUtility.BlasterMoveToBackFactor;
        }

        float3 formationCenter = targetPoint - forward * backOffset;
        float arrivalRadius = AniFormationUtility.ArrivalRadius;

        PlanFormationMovement(
            aniTransform.Position,
            formationCenter,
            formationRotation,
            slotIndex,
            arrivalRadius,
            ref blackboard,
            in fsmContext);
    }

    // =============== 公共 Nav 写入逻辑 ===============

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
        bool hasArrived = distanceSquared <= arrivalRadiusSq;

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, hasArrived);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop,    hasArrived);
        
        if (hasArrived)
        {
            return;
        }

        int currentTick = (int)fsmContext.Tick;
        int nextUpdateTick = Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick);

        bool shouldUpdateNav = (nextUpdateTick == 0) || (currentTick >= nextUpdateTick);
        if (!shouldUpdateNav)
            return;

        Blackboard.SetFloat3(ref blackboard, AniMovementBlackboardKeys.K_NavTargetPosition, desiredPosition);
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavRequestVersion, currentTick);

        int newNextTick = currentTick + AniMovementConfig.NavUpdateIntervalTicks;
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick, newNextTick);
    }
}
