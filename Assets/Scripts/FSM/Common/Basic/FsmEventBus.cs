using Unity.Entities;

public static class BlackBoardEventKeys
{
    public const uint K_AssignedToPlayerTick = 0xE001u;
    public const uint K_AssignedToTargetTick = 0xE002u;
    public const uint K_TargetLostTick = 0xE003u;
}

public static class FsmEventBus
{
    public static void Raise_AssignedToPlayer(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackBoardEventKeys.K_AssignedToPlayerTick, (int)context.Tick);
    }

    public static void Raise_AssignedToTarget(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackBoardEventKeys.K_AssignedToTargetTick, (int)context.Tick);
    }

    public static void Raise_TargetLost(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackBoardEventKeys.K_TargetLostTick, (int)context.Tick);
    }

}