using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class BlasterAniConditions
{
    [BurstCompile]
    public static bool C_ShouldFollow(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsFollow);

    [BurstCompile]
    public static bool C_ShouldFind(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsShoot)
           && blackboard.GetBool(BlasterAniBlackBoardKeys.K_TargetValid)
           && !blackboard.GetBool(BlasterAniBlackBoardKeys.K_CanShoot);

    [BurstCompile]
    public static bool C_CanShoot(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsShoot)
           && blackboard.GetBool(BlasterAniBlackBoardKeys.K_TargetValid)
           && blackboard.GetBool(BlasterAniBlackBoardKeys.K_CanShoot);

    [BurstCompile]
    public static bool C_TargetGone(ref DynamicBuffer<FsmVar> blackboard)
        => !blackboard.GetBool(BlasterAniBlackBoardKeys.K_TargetValid);

    [BurstCompile]
    public static bool C_StopShooting(ref DynamicBuffer<FsmVar> blackboard)
        => !blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsShoot);
}

[BurstCompile]
public static class BlasterAniActions
{
    [BurstCompile]
    public static void A_EnterIdle(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestTick, (int)context.Tick);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestId, 1); // idle
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestTick, (int)context.Tick);

    }

    [BurstCompile]
    public static void A_EnterFollow(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, false);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestId, 2); // run
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestTick, (int)context.Tick);

        // 速度和目标点由外部Planner系统设置

    }

    [BurstCompile]
    public static void A_EnterFind(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, true);

        // 由Planner系统将TargetPosition设置为NavTargetPosition，直到CanShoot
    }

    [BurstCompile]
    public static void A_EnterShoot(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestTick, (int)context.Tick);


        // 朝向/动画
        Blackboard.SetFloat3(ref blackboard, BlasterAniBlackBoardKeys.K_lookAtTargetPosition, blackboard.GetFloat3(BlasterAniBlackBoardKeys.K_TargetPosition));
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestId, 3); // shoot
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestTick, (int)context.Tick);

        //立即开火
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_ShootPauseTick, (int)context.Tick);
        Blackboard.SetFloat(ref blackboard, BlasterAniBlackBoardKeys.K_ShootCooldown, blackboard.GetFloat(BlasterAniBlackBoardKeys.K_ShootCooldownReset));

    }
}

