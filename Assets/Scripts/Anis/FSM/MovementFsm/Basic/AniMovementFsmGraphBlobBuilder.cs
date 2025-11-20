using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Runtime.InteropServices;

public static class AniMovementFsmGraphBlobBuilder
{
    private static readonly int MAX_CAPACITY = 1024;

    public static void AllocateBuilderBase(out BlobBuilder builder, out BlobBuilderArray<FsmStateNode> states)
    {
        builder = new BlobBuilder(Allocator.Temp);
        ref var graph = ref builder.ConstructRoot<FsmGraph>();
        states = builder.Allocate(ref graph.States, MAX_CAPACITY);
    }

    public static void BuildIdleState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIDs.S_Idle].State = (StateId)AniMovementFsmIDs.S_Idle;
        var transitions = builder.Allocate(ref states[AniMovementFsmIDs.S_Idle].Transitions, 3); // 共 3 条边

        // Idle -> Follow
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Follow,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFollow,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFollow,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitIdle,
        };

        // Idle -> Find
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Find,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFind,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFind,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitIdle,
        };

        // Idle -> MoveTo
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_MoveTo,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandMoveTo,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterMoveTo,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitIdle,
        };
    }

    public static void BuildFollowState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIDs.S_Follow].State = (StateId)AniMovementFsmIDs.S_Follow;
        var transitions = builder.Allocate(ref states[AniMovementFsmIDs.S_Follow].Transitions, 2); // 共 2 条边

        // Follow -> Find
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Find,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFind,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFind,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitFollow,
        };

        // Follow -> MoveTo
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_MoveTo,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandMoveTo,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterMoveTo,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitFollow,
        };
    }

    public static void BuildFindState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIDs.S_Find].State = (StateId)AniMovementFsmIDs.S_Find;
        var transitions = builder.Allocate(ref states[AniMovementFsmIDs.S_Find].Transitions, 3); // 共 3 条边

        // Find -> Follow （CommandFollow）
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Follow,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFollow,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFollow,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitFind,
        };

        // Find -> MoveTo （CommandMoveTo）
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_MoveTo,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandMoveTo,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterMoveTo,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitFind,
        };

        // Find -> Idle （目标消失）
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Idle,
            Condition = (ConditionId)AniMovementFsmIDs.C_TargetGone,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterIdle,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitFind,
        };
    }

    public static void BuildMoveToState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIDs.S_MoveTo].State = (StateId)AniMovementFsmIDs.S_MoveTo;
        var transitions = builder.Allocate(ref states[AniMovementFsmIDs.S_MoveTo].Transitions, 3); // 共 3 条边

        // MoveTo -> Idle （到达或者命令改为 Idle）
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Idle,
            Condition = (ConditionId)AniMovementFsmIDs.C_MoveArrived,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterIdle,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitMoveTo,
        };

        // MoveTo -> Follow
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Follow,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFollow,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFollow,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitMoveTo,
        };

        // MoveTo -> Find
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIDs.S_Find,
            Condition = (ConditionId)AniMovementFsmIDs.C_CommandFind,
            OnEnter   = (ActionId)AniMovementFsmIDs.A_EnterFind,
            OnExit    = (ActionId)AniMovementFsmIDs.A_ExitMoveTo,
        };
    }
}
