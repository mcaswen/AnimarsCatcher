using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class AniMovementFsmActions
{
    [BurstCompile]
    public static void A_EnterIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop, true);
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavRequestVersion, (int)context.Tick);

        // 清理到达标记
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, true);
    }

    [BurstCompile]
    public static void A_ExitIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }

    [BurstCompile]
    public static void A_EnterFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 开启导航，由 FollowPlanner 设置 NavTargetPosition
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);
        
        // 不直接写版本更新，而是立即刷新一次UpdateTick
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick, 0);

        // Follow 时不再有 TargetEntity 概念，交给 FollowPlanner 追 Player
        Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.K_TargetEntity, Entity.Null);

        // 阵列加入事件，由 Formation 系统消费
        var playerEntity = Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.K_PlayerEntity);
        if (playerEntity != Entity.Null)
        {
            Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.K_FormationLeader, playerEntity);
            Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_FormationJoinEventVersion, (int)context.Tick);
        }

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);
    }

    [BurstCompile]
    public static void A_ExitFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_FormationLeaveEventVersion, (int)context.Tick);
    }

    [BurstCompile]
    public static void A_EnterFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 朝 TargetEntity 移动，Planner 会把 TargetPosition 写到 NavTargetPosition
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);
    }

    [BurstCompile]
    public static void A_ExitFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }

    [BurstCompile]
    public static void A_EnterMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // MoveTo 按 K_MoveToPosition 走，由 Planner 负责写 NavTarget
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.K_NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.K_MoveArrived, false);
    }

    [BurstCompile]
    public static void A_ExitMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }
}
