using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Gameplay;
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
        var commandIdlePtr   = BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.IsCommandIdle);
        var commandFollowPtr = BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.IsCommandFollow);
        var commandFindPtr   = BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.IsCommandFind);
        var commandMovePtr   = BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.IsCommandMoveTo);
        var targetGonePtr= BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.IsTargetGone);
        var arrivedPtr   = BurstCompiler.CompileFunctionPointer<ConditionFunction>(AniMovementFsmConditions.HasMoveArrived);

        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandIdleConditionId,   commandIdlePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandFollowConditionId, commandFollowPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandFindConditionId,   commandFindPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandMoveToConditionId, commandMovePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.TargetGoneConditionId,    targetGonePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.MoveArrivedConditionId,   arrivedPtr);

        // 动作注册
        var enterIdlePtr   = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.EnterIdle);
        var exitIdlePtr    = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.ExitIdle);
        var enterFollowPtr = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.EnterFollow);
        var exitFollowPtr  = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.ExitFollow);
        var enterFindPtr   = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.EnterFind);
        var exitFindPtr    = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.ExitFind);
        var enterMovePtr   = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.EnterMoveTo);
        var exitMovePtr    = BurstCompiler.CompileFunctionPointer<ActionFunction>(AniMovementFsmActions.ExitMoveTo);

        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.EnterIdleActionId,   enterIdlePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.ExitIdleActionId,    exitIdlePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.EnterFollowActionId, enterFollowPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.ExitFollowActionId,  exitFollowPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.EnterFindActionId,   enterFindPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.ExitFindActionId,    exitFindPtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.EnterMoveToActionId, enterMovePtr);
        FsmRegistry.RegisterAction((ActionId)AniMovementFsmIds.ExitMoveToActionId,  exitMovePtr);

        state.Enabled = false; // 注册完毕后关闭系统
    }

    public void OnUpdate(ref SystemState state) {}
}
