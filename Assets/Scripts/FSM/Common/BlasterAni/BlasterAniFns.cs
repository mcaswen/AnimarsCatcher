using Unity.Burst;
using Unity.Entities;

public static class BlasterAniFns
{
    [BurstCompile]
    public static bool C_OnAssignedToPlayer(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {
        return bb.GetInt(BlackBoardEventKeys.K_AssignedToPlayerTick, -1) == (int)context.Tick;
    }

    [BurstCompile]
    public static bool C_OnAssignedToTarget(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmContext context)
    {
        return bb.GetInt(BlackBoardEventKeys.K_AssignedToTargetTick, -1) == (int)context.Tick;
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
        fsm.TimeInState = 0f;
        float reset = bb.GetFloat(SoldierBB.K_RecoveryReset, 0.9f);
        bb.SetFloatSorted(SoldierBB.K_Recovery, reset);
        // 真实出手/命中等仍由“攻击系统”根据请求位完成
    }

    [BurstCompile]
    public static void A_ExitAttack(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmEnv env) { }

    [BurstCompile]
    public static void A_TickRecovery(ref Fsm fsm, ref DynamicBuffer<FsmVar> bb, in FsmEnv env)
    {
        float r = bb.GetFloat(SoldierBB.K_Recovery);
        if (r > 0f) {
            r -= env.DeltaTime;
            if (r < 0f) r = 0f;
            bb.SetFloatSorted(SoldierBB.K_Recovery, r);
        }
    }
}

