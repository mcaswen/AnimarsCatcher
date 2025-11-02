using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class BlasterAniConditions
{
    [BurstCompile]
    public static bool C_ShouldFollow(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsFollowing)
            && !blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsFinding);

    [BurstCompile]
    public static bool C_ShouldFind(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsFinding)
           && blackboard.GetEntity(BlasterAniBlackBoardKeys.K_TargetEntity) != Entity.Null
           && !blackboard.GetBool(BlasterAniBlackBoardKeys.K_CanFire);

    [BurstCompile]
    public static bool C_ShouldShoot(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetBool(BlasterAniBlackBoardKeys.K_IsFinding)
           && blackboard.GetEntity(BlasterAniBlackBoardKeys.K_TargetEntity) != Entity.Null
           && blackboard.GetBool(BlasterAniBlackBoardKeys.K_CanFire);

    [BurstCompile]
    public static bool C_TargetGone(ref DynamicBuffer<FsmVar> blackboard)
        => blackboard.GetEntity(BlasterAniBlackBoardKeys.K_TargetEntity) == Entity.Null;

}

[BurstCompile]
public static class BlasterAniActions
{
    [BurstCompile]
    public static void A_EnterIdle(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;

        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestVersion, (int)context.Tick);

        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationPlayRequestId, 1); // idle
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestVersion, (int)context.Tick);
    }

    [BurstCompile]
    public static void A_EnterFollow(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;

        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, false);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestVersion, (int)context.Tick);

        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationPlayRequestId, 2); // run
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestVersion, (int)context.Tick);

        Blackboard.SetEntity(ref blackboard, BlasterAniBlackBoardKeys.K_TargetEntity, Entity.Null);

        // 速度和目标点由外部Planner系统设置

    }
    
    [BurstCompile]
    public static void A_ExitFollow(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_IsFollowing, false);
    }

    [BurstCompile]
    public static void A_EnterFind(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        //朝目标移动，直到可以开火
        fsm.TimeInState = 0f;

        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, false);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestVersion, (int)context.Tick);

        // 由Planner系统将TargetPosition设置为NavTargetPosition，直到ShouldShoot
    }

    [BurstCompile]
    public static void A_ExitFind(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_IsFinding, false);
    }

    [BurstCompile]
    public static void A_EnterShoot(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        fsm.TimeInState = 0f;

        Blackboard.SetBool(ref blackboard, BlasterAniBlackBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NavRequestVersion, (int)context.Tick);

        // 动画
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationPlayRequestId, 3); // shoot
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_AnimationRequestVersion, (int)context.Tick);

        //立即开火
        Blackboard.SetInt(ref blackboard, BlasterAniBlackBoardKeys.K_NextFireTick, (int)context.Tick);
    }
}

