using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 在服务器把移动命令、阵型槽位和攻击距离转换为导航黑板目标
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FsmApplyTransitionSystem))]
[UpdateAfter(typeof(AniFormationManagementSystem))]
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
                Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.CommandMode);

            bool hasFormationMember = SystemAPI.HasComponent<AniFormationMember>(entity);
            Entity leaderEntity = Entity.Null;
            int slotIndex  = 0;

            if (hasFormationMember)
            {
                var member = SystemAPI.GetComponent<AniFormationMember>(entity);
                leaderEntity = member.leader; // 队长是拥有并控制该 Ani 的玩家实体
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
                        Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.TargetEntity);

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

    // 空闲模式停止导航并保持到达状态
    private static void HandleIdle(ref DynamicBuffer<FsmVar> blackboard)
    {
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop,     true);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, true);
    }

    /// <summary>
    /// 根据阵型中心、朝向和槽位计算 Ani 的世界目标并写入导航黑板
    /// 槽位先在局部空间布局，再旋转到阵型世界空间
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

    // 根据队长到目标的水平向量推导阵型前向
    private static quaternion ComputeFormationRotationFromLeaderToTarget(
        float3 leaderPosition,
        float3 targetPosition,
        in quaternion leaderFallbackRotation)
    {
        float3 dir = targetPosition - leaderPosition;
        dir.y = 0f;

        if (math.lengthsq(dir) < 0.0001f)
        {
            // 队长与目标几乎重合时沿用队长朝向，避免零向量旋转
            return leaderFallbackRotation;
        }

        float3 forward = math.normalize(dir);
        return quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
    }

    // 跟随模式以玩家位置为锚点并按 Ani 类型向后偏移
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

        // 从玩家脚下沿反前向偏移，保持 Ani 位于玩家后方
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

    // 寻敌模式面向敌人并在攻击距离内保持阵型
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

        // 从敌人位置沿反前向偏移，避免远程单位贴近目标
        float3 targetPoint = targetPos;

        float backOffset = 0f;
        if (isBlaster)
        {
            backOffset = aniAttributes.AttackRange * AniFormationUtility.BlasterFindBackFactor;
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

    // 定点移动使用命令接收时冻结的阵型锚点和前向
    private static void HandleMoveTo(
    in LocalTransform aniTransform,
    in LocalTransform leaderTransform, // 缓存前向无效时作为稳定兜底朝向
    int slotIndex,
    bool isPicker,
    bool isBlaster,
    in AniAttributes aniAttributes,
    ref DynamicBuffer<FsmVar> blackboard,
    in FsmContext fsmContext)
    {
        // 使用首次点击时缓存的阵型锚点，避免成员各自计算产生偏差
        float3 targetPoint = Blackboard.GetFloat3(ref blackboard,
            AniMovementBlackboardKeys.MoveFormationTargetPoint);

        float3 forward = Blackboard.GetFloat3(ref blackboard,
            AniMovementBlackboardKeys.MoveFormationForward);

        // 前向未初始化时根据队长和点击点恢复稳定方向
        if (math.lengthsq(forward) < 0.0001f)
        {
            float3 leaderPos = leaderTransform.Position;
            float3 fallbackTarget = Blackboard.GetFloat3(ref blackboard,
                AniMovementBlackboardKeys.MoveToPosition);

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

    // 统一写入到达状态、导航目标和请求版本
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

        // 到达状态同时控制 FSM 迁移和导航停止
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, hasArrived);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop,    hasArrived);

        // 已到达时不再产生新的寻路请求
        if (hasArrived)
            return;

        // 每个 Tick 更新请求版本，使移动目标变化能立即传给导航系统

        Blackboard.SetFloat3(
            ref blackboard,
            AniMovementBlackboardKeys.NavTargetPosition,
            desiredPosition);

        int currentTick = (int)fsmContext.Tick;
        Blackboard.SetInt(
            ref blackboard,
            AniMovementBlackboardKeys.NavRequestVersion,
            currentTick);

        // 当前策略不做间隔限流，因此保持下一次允许更新 Tick 为零
        Blackboard.SetInt(
            ref blackboard,
            AniMovementBlackboardKeys.NavNextUpdateTick,
            0);
    }
}
