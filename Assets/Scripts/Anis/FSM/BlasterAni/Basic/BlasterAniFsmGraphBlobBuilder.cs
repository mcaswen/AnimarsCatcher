using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.VisualScripting;

public static class BlasterAniFsmGraphBlobBuilder
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
        states[BlasterAniFsmIDs.S_Idle].State = (StateId)BlasterAniFsmIDs.S_Idle;
        var transitions = builder.Allocate(ref states[BlasterAniFsmIDs.S_Idle].Transitions, 1);

        // Idle状态仅有一种转换，Idle -> Follow，作为初始状态不再会被转移回来
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Follow,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFollow,
            OnExit = 0,
        };
    }

    public static void BuildFollowState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniFsmIDs.S_Follow].State = (StateId)BlasterAniFsmIDs.S_Follow;
        var transitions = builder.Allocate(ref states[BlasterAniFsmIDs.S_Follow].Transitions, 1);

        // Follow状态仅有一种转换，Follow -> Find(ShouldFollow，外部系统)
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Find,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFind,
            OnExit = (ActionId)BlasterAniFsmIDs.A_ExitFollow,
        };
    }

    public static void BuildFindState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniFsmIDs.S_Find].State = (StateId)BlasterAniFsmIDs.S_Find;
        var transitions = builder.Allocate(ref states[BlasterAniFsmIDs.S_Find].Transitions, 3);

        // Find状态有三种转换，Find -> Follow(TargetGone), Find -> Shoot(ShouldShoot，外部系统)， Find -> Follow(ShouldFollow，外部系统)
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Follow,
            Condition = (ConditionId)BlasterAniFsmIDs.C_TargetGone,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFollow,
            OnExit = 0,
        };

        transitions[1] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Shoot,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldShoot,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterShoot,
            OnExit = 0,
        };

        transitions[2] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Follow,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFollow,
            OnExit = 0,
        };
    }
    
    public static void BuildShootState(ref BlobBuilder builder, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniFsmIDs.S_Shoot].State = (StateId)BlasterAniFsmIDs.S_Shoot;
        var transitions = builder.Allocate(ref states[BlasterAniFsmIDs.S_Shoot].Transitions, 3);

        // Shoot状态有三种转换，Shoot -> Follow(TargetGone) Shoot -> Follow(ShouldFollow，外部系统)， Shoot -> Find(ShouldFind，外部系统)
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Follow,
            Condition = (ConditionId)BlasterAniFsmIDs.C_TargetGone,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFollow,
            OnExit = 0,
        };

        transitions[1] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Find,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFind,
            OnExit = 0,
        };

        transitions[2] = new FsmTransition
        {
            To = (StateId)BlasterAniFsmIDs.S_Follow,
            Condition = (ConditionId)BlasterAniFsmIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniFsmIDs.A_EnterFollow,
            OnExit = 0,
        };
    }
}
