using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using System.Runtime.InteropServices;
using Unity.VisualScripting;

public static class BlasterAniGraphBlobBuilder
{
    public static void AllocateBuilderBase(out BlobBuilder builder, ref FsmGraph graph)
    {
        builder = new BlobBuilder(Allocator.Temp);
        graph = ref builder.ConstructRoot<FsmGraph>();
        var states = builder.Allocate(ref graph.States, BlasterAniIDs.S_Idle + 1);
    }

    public static void BuildIdleState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Idle].State = (StateId)BlasterAniIDs.S_Idle;
        var transition0 = builder.Allocate(ref states[BlasterAniIDs.S_Idle].Transitions, 1);

        // Idle状态仅有一种转换，Idle -> Follow
        transition0[0] = new FsmTransition
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
        var transition1 = builder.Allocate(ref states[BlasterAniIDs.S_Follow].Transitions, 3);

        // Follow状态有两种转换，Follow -> Find， Follow -> Idle， Follow -> Shoot
        transition1[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Find,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFind,
            OnExit = 0,
        };

        // transition1[1] = new FsmTransition
        // {
        //     To = (StateId)BlasterAniIDs.S_Idle,
        //     Condition = (ConditionId)BlasterAniIDs.C_StopShooting,
        //     OnEnter = (ActionId)BlasterAniIDs.A_EnterIdle,
        //     OnExit = 0,
        // };

        transition1[1] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Shoot,
            Condition = (ConditionId)BlasterAniIDs.C_CanShoot,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterShoot,
            OnExit = 0,
        };
    }

    public static void BuildFindState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Find].State = (StateId)BlasterAniIDs.S_Find;
        var transition2 = builder.Allocate(ref states[BlasterAniIDs.S_Find].Transitions, 3);

        // Find状态有两种转换，Find -> Follow， Find -> Shoot
        transition2[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };

        // transition2[1] = new FsmTransition
        // {
        //     To = (StateId)BlasterAniIDs.S_Idle,
        //     Condition = (ConditionId)BlasterAniIDs.C_StopShooting,
        //     OnEnter = (ActionId)BlasterAniIDs.A_EnterIdle,
        //     OnExit = 0,
        // };

        transition2[1] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Shoot,
            Condition = (ConditionId)BlasterAniIDs.C_CanShoot,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterShoot,
            OnExit = 0,
        };
    }
    
    public static void BuildShootState(ref BlobBuilder builder, ref FsmGraph graph, ref BlobBuilderArray<FsmStateNode> states)
    {
        states[BlasterAniIDs.S_Find].State = (StateId)BlasterAniIDs.S_Find;
        var transition3 = builder.Allocate(ref states[BlasterAniIDs.S_Find].Transitions, 3);

        // Shoot状态有两种转换，Shoot -> Follow， Shoot -> Find
        transition3[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Follow,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFollow,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFollow,
            OnExit = 0,
        };
        
        transition3[0] = new FsmTransition
        {
            To = (StateId)BlasterAniIDs.S_Find,
            Condition = (ConditionId)BlasterAniIDs.C_ShouldFind,
            OnEnter = (ActionId)BlasterAniIDs.A_EnterFind,
            OnExit = 0,
        };
    }


}
