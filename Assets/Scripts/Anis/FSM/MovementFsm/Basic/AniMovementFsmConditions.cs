using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class AniMovementFsmConditions
{
    [BurstCompile]
    public static bool IsCommandIdle(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.Idle;
    }

    [BurstCompile]
    public static bool IsCommandFollow(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.Follow;
    }

    [BurstCompile]
    public static bool IsCommandFind(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);

        if (mode != (int)AniMovementCommandMode.Find)
            return false;

        var target = blackboard.GetEntity(AniMovementBlackboardKeys.TargetEntity);
        return target != Entity.Null;
    }

    [BurstCompile]
    public static bool IsCommandMoveTo(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.MoveTo;
    }

    [BurstCompile]
    public static bool IsTargetGone(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        var target = blackboard.GetEntity(AniMovementBlackboardKeys.TargetEntity);
        return target == Entity.Null;
    }

    [BurstCompile]
    public static bool HasMoveArrived(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetBool(AniMovementBlackboardKeys.MoveArrived);
    }
}
