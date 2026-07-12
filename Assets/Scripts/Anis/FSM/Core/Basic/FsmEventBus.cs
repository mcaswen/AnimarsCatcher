using Unity.Entities;

public static class BlackboardEventKeys
{
    public const uint AssignedToPlayerTick = 0xE001u;
    public const uint AssignedToTargetTick = 0xE002u;
    public const uint TargetLostTick = 0xE003u;
}

public static class FsmEventBus
{
    public static void RaiseAssignedToPlayer(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.AssignedToPlayerTick, (int)context.Tick);
    }

    public static void RaiseAssignedToTarget(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.AssignedToTargetTick, (int)context.Tick);
    }

    public static void RaiseTargetLost(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.TargetLostTick, (int)context.Tick);
    }

}
