using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Runtime.InteropServices;

public static class AniMovementFsmGraphBlobBuilder
{
    private const int MaxCapacity = 1024;

    public static void AllocateBuilderBase(out BlobBuilder builder, out BlobBuilderArray<FsmStateNode> states)
    {
        builder = new BlobBuilder(Allocator.Temp);
        ref var graph = ref builder.ConstructRoot<FsmGraph>();
        states = builder.Allocate(ref graph.States, MaxCapacity);
    }

    public static void BuildIdleState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIds.IdleStateId].State = (StateId)AniMovementFsmIds.IdleStateId;
        var transitions = builder.Allocate(ref states[AniMovementFsmIds.IdleStateId].Transitions, 3); // 共 3 条边

        // Idle -> Follow
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FollowStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
        };

        // Idle -> Find
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FindStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
        };

        // Idle -> MoveTo
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.MoveToStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitIdleActionId,
        };
    }

    public static void BuildFollowState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIds.FollowStateId].State = (StateId)AniMovementFsmIds.FollowStateId;
        var transitions = builder.Allocate(ref states[AniMovementFsmIds.FollowStateId].Transitions, 2); // 共 2 条边

        // Follow -> Find
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FindStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitFollowActionId,
        };

        // Follow -> MoveTo
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.MoveToStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitFollowActionId,
        };
    }

    public static void BuildFindState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIds.FindStateId].State = (StateId)AniMovementFsmIds.FindStateId;
        var transitions = builder.Allocate(ref states[AniMovementFsmIds.FindStateId].Transitions, 3); // 共 3 条边

        // Find -> Follow （CommandFollow）
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FollowStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
        };

        // Find -> MoveTo （CommandMoveTo）
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.MoveToStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandMoveToConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterMoveToActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
        };

        // Find -> Idle （目标消失）
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.IdleStateId,
            Condition = (ConditionId)AniMovementFsmIds.TargetGoneConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterIdleActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitFindActionId,
        };
    }

    public static void BuildMoveToState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[AniMovementFsmIds.MoveToStateId].State = (StateId)AniMovementFsmIds.MoveToStateId;
        var transitions = builder.Allocate(ref states[AniMovementFsmIds.MoveToStateId].Transitions, 3); // 共 3 条边

        // MoveTo -> Idle （到达或者命令改为 Idle）
        transitions[0] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.IdleStateId,
            Condition = (ConditionId)AniMovementFsmIds.MoveArrivedConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterIdleActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
        };

        // MoveTo -> Follow
        transitions[1] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FollowStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFollowConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFollowActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
        };

        // MoveTo -> Find
        transitions[2] = new FsmTransition
        {
            To        = (StateId)AniMovementFsmIds.FindStateId,
            Condition = (ConditionId)AniMovementFsmIds.CommandFindConditionId,
            OnEnter   = (ActionId)AniMovementFsmIds.EnterFindActionId,
            OnExit    = (ActionId)AniMovementFsmIds.ExitMoveToActionId,
        };
    }
}
