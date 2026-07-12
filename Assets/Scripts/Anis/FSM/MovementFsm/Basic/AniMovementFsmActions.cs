using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class AniMovementFsmActions
{
    [BurstCompile]
    public static void EnterIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, true);
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavRequestVersion, (int)context.Tick);

        // 清理到达标记
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, true);
    }

    [BurstCompile]
    public static void ExitIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }

    [BurstCompile]
    public static void EnterFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 开启导航，由 FollowPlanner 设置 NavTargetPosition
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
        
        // 不直接写版本更新，而是立即刷新一次UpdateTick
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        // Follow 时不再有 TargetEntity 概念，交给 FollowPlanner 追 Player
        Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.TargetEntity, Entity.Null);

        // 阵列加入事件，由 Formation 系统消费
        var playerEntity = Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.PlayerEntity);
        if (playerEntity != Entity.Null)
        {
            Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.FormationLeader, playerEntity);
            Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.FormationJoinEventVersion, (int)context.Tick);
        }

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    [BurstCompile]
    public static void ExitFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.FormationLeaveEventVersion, (int)context.Tick);
    }

    [BurstCompile]
    public static void EnterFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 朝 TargetEntity 移动，Planner 会把 TargetPosition 写到 NavTargetPosition
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    [BurstCompile]
    public static void ExitFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }

    [BurstCompile]
    public static void EnterMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // MoveTo 按 K_MoveToPosition 走，由 Planner 负责写 NavTarget
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    [BurstCompile]
    public static void ExitMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 预留接口
    }
}
