using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.VisualScripting;

public static class BlasterAniGraphBlobBuilder
{
    public static void AllocateBuilderBase(out BlobBuilder builder, ref FsmGraph graph, out BlobBuilderArray<FsmStateNode> states)
    {
        builder = new BlobBuilder(Allocator.Temp);
        graph = ref builder.ConstructRoot<FsmGraph>();
        states = builder.Allocate(ref graph.States, BlasterAniIDs.kCondOffset - 1);
    }

    public static void BuildIdleState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Idle].State = (StateId)BlasterAniIDs.S_Idle;
        var transitions = builder.Allocate(ref states[BlasterAniIDs.S_Idle].Transitions, 1);

        // Idle状态仅有一种转换，Idle -> Follow，作为初始状态不再会被转移回来
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };
    }

    public static void BuildFollowState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Follow].State = (StateId)BlasterAniIDs.S_Follow;
        var transitions = builder.Allocate(ref states[BlasterAniIDs.S_Follow].Transitions, 1);

        // Follow状态仅有一种转换，Follow -> Find
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Find,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFind,
            OnExit = 0,
        };
    }

    public static void BuildFindState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Find].State = (StateId)BlasterAniIDs.S_Find;
        var transitions = builder.Allocate(ref states[BlasterAniIDs.S_Find].Transitions, 3);

        // Find状态有三种转换，Find -> Follow(TargetGone), Find -> Follow(ShouldFollow，外部系统)， Find -> Shoot
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_TargetGone,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };

        transitions[1] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Shoot,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldShoot,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterShoot,
            OnExit = 0,
        };

        transitions[2] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };
    }
    
    public static void BuildShootState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Shoot].State = (StateId)BlasterAniIDs.S_Shoot;
        var transitions = builder.Allocate(ref states[BlasterAniIDs.S_Shoot].Transitions, 3);

        // Shoot状态有三种转换，Shoot -> Follow(TargetGone) Shoot -> Follow(ShouldFollow，外部系统)， Shoot -> Find(ShouldFind，外部系统)
        transitions[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_TargetGone,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };

        transitions[1] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Find,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFind,
            OnExit = 0,
        };

        transitions[2] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };
    }
}
