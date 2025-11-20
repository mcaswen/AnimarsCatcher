using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public static class AniMovementFsmConditions
{
    [BurstCompile]
    public static bool C_CommandIdle(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.K_CommandMode);
        return mode == (int)AniMovementCommandMode.Idle;
    }

    [BurstCompile]
    public static bool C_CommandFollow(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.K_CommandMode);
        return mode == (int)AniMovementCommandMode.Follow;
    }

    [BurstCompile]
    public static bool C_CommandFind(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.K_CommandMode);

        if (mode != (int)AniMovementCommandMode.Find)
            return false;

        var target = blackboard.GetEntity(AniMovementBlackboardKeys.K_TargetEntity);
        return target != Entity.Null;
    }

    [BurstCompile]
    public static bool C_CommandMoveTo(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.K_CommandMode);
        return mode == (int)AniMovementCommandMode.MoveTo;
    }

    [BurstCompile]
    public static bool C_TargetGone(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        var target = blackboard.GetEntity(AniMovementBlackboardKeys.K_TargetEntity);
        return target == Entity.Null;
    }

    [BurstCompile]
    public static bool C_MoveArrived(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetBool(AniMovementBlackboardKeys.K_MoveArrived);
    }
}
