using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class BlasterAniFsmConditions
{
    [BurstCompile]
    public static bool C_ShouldFollow(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetInt(BlasterAniBlackFsmBoardKeys.K_CommandMode) == (int)BlasterAniFsmCommandMode.Follow;
    }
    [BurstCompile]
    public static bool C_ShouldFind(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetInt(BlasterAniBlackFsmBoardKeys.K_CommandMode) == (int)BlasterAniFsmCommandMode.Find
               && blackboard.GetEntity(BlasterAniBlackFsmBoardKeys.K_TargetEntity) != Entity.Null
               && !blackboard.GetBool(BlasterAniBlackFsmBoardKeys.K_HasFiringSolution);
    }

    [BurstCompile]
    public static bool C_ShouldShoot(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetInt(BlasterAniBlackFsmBoardKeys.K_CommandMode) == (int)BlasterAniFsmCommandMode.Find
               && blackboard.GetEntity(BlasterAniBlackFsmBoardKeys.K_TargetEntity) != Entity.Null
               && blackboard.GetBool(BlasterAniBlackFsmBoardKeys.K_HasFiringSolution);
    }

    [BurstCompile]
    public static bool C_TargetGone(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetEntity(BlasterAniBlackFsmBoardKeys.K_TargetEntity) == Entity.Null;
    }

}

[BurstCompile]
public static class BlasterAniFsmActions
{
    [BurstCompile]
    public static void A_EnterIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        Blackboard.SetBool(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, (int)context.Tick);

    }

    [BurstCompile]
    public static void A_EnterFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        Blackboard.SetBool(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavStop, false);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, (int)context.Tick);

        Blackboard.SetEntity(ref blackboard, BlasterAniBlackFsmBoardKeys.K_TargetEntity, Entity.Null);

        var playerEntity = Blackboard.GetEntity(ref blackboard, BlasterAniBlackFsmBoardKeys.K_PlayerEntity);
        if (playerEntity != Entity.Null)
            Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_FormationJoinEventVersion, (int)context.Tick);

        // 速度和目标点由外部Planner系统设置

    }
    
    [BurstCompile]
    public static void A_ExitFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_FormationLeaveEventVersion, (int)context.Tick);
    }

    [BurstCompile]
    public static void A_EnterFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        
        //朝目标移动，直到可以开火
        Blackboard.SetBool(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavStop, false);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, (int)context.Tick);

        // 由Planner系统将TargetPosition设置为NavTargetPosition，直到ShouldShoot
    }

    [BurstCompile]
    public static void A_ExitFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    { }

    [BurstCompile]
    public static void A_EnterShoot(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetBool(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, (int)context.Tick);

        //立即开火
        Blackboard.SetInt(ref blackboard, BlasterAniBlackFsmBoardKeys.K_NextFireTick, (int)context.Tick);
    }
}

