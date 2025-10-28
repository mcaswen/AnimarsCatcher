using Unity.Burst;
using Unity.Entities;

public static class BlasterAniFns
{
    [BurstCompile]
    public static bool C_OnAssignedToPlayer(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        return blackboard.GetInt(BlackBoardEventKeys.K_AssignedToPlayerTick, -1) == (int)context.Tick;
    }

    [BurstCompile]
    public static bool C_OnAssignedToTarget(ref Fsm fsm, ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        return blackboard.GetInt(BlackBoardEventKeys.K_AssignedToTargetTick, -1) == (int)context.Tick;
    }

    [BurstCompile]
    public static bool C_LostTarget(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {
        return bb.GetInt(BlackBoardEventKeys.K_TargetLostTick, -1) == (int)context.Tick;
    }

    [BurstCompile]
    public static void A_EnterPatrol(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {
        fsm.TimeInState = 0f;
    }

    [BurstCompile]
    public static void A_ExitPatrol(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context) { }

    [BurstCompile]
    public static void A_EnterChase(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {
        fsm.TimeInState = 0f;
    }

    [BurstCompile]
    public static void A_ExitChase(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context) { }

    [BurstCompile]
    public static void A_EnterAttack(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {

    }
}

