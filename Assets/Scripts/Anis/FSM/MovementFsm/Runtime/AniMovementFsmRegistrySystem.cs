using Unity.Burst;
using Unity.Entities;

public struct AniMovementRegistryInitialized : IComponentData {}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[UpdateBefore(typeof(FsmEvaluateSystem))]
public partial struct AniMovementFsmRegistrySystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<AniMovementRegistryInitialized>())
            return;

        state.EntityManager.CreateEntity(typeof(AniMovementRegistryInitialized));

        // 条件注册
        var commandIdlePtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_CommandIdle);
        var commandFollowPtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_CommandFollow);
        var commandFindPtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_CommandFind);
        var commandMovePtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_CommandMoveTo);
        var targetGonePtr= BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_TargetGone);
        var arrivedPtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.C_MoveArrived);

        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_CommandIdle,   commandIdlePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_CommandFollow, commandFollowPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_CommandFind,   commandFindPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_CommandMoveTo, commandMovePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_TargetGone,    targetGonePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIDs.C_MoveArrived,   arrivedPtr);

        // 动作注册
        var enterIdlePtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_EnterIdle);
        var exitIdlePtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_ExitIdle);
        var enterFollowPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_EnterFollow);
        var exitFollowPtr  = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_ExitFollow);
        var enterFindPtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_EnterFind);
        var exitFindPtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_ExitFind);
        var enterMovePtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_EnterMoveTo);
        var exitMovePtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.A_ExitMoveTo);

        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_EnterIdle,   enterIdlePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_ExitIdle,    exitIdlePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_EnterFollow, enterFollowPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_ExitFollow,  exitFollowPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_EnterFind,   enterFindPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_ExitFind,    exitFindPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_EnterMoveTo, enterMovePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIDs.A_ExitMoveTo,  exitMovePtr);

        state.Enabled = false; // 注册完毕后关闭系统
    }

    public void OnUpdate(ref SystemState state) {}
}
