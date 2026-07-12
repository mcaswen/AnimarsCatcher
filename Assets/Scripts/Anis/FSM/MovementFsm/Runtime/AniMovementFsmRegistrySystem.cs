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
        var commandIdlePtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.IsCommandIdle);
        var commandFollowPtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.IsCommandFollow);
        var commandFindPtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.IsCommandFind);
        var commandMovePtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.IsCommandMoveTo);
        var targetGonePtr= BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.IsTargetGone);
        var arrivedPtr   = BurstCompiler.CompileFunctionPointer<ConditionFn>(AniMovementFsmConditions.HasMoveArrived);

        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandIdleConditionId,   commandIdlePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandFollowConditionId, commandFollowPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandFindConditionId,   commandFindPtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.CommandMoveToConditionId, commandMovePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.TargetGoneConditionId,    targetGonePtr);
        FsmRegistry.RegisterCondition((ConditionId)AniMovementFsmIds.MoveArrivedConditionId,   arrivedPtr);

        // 动作注册
        var enterIdlePtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.EnterIdle);
        var exitIdlePtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.ExitIdle);
        var enterFollowPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.EnterFollow);
        var exitFollowPtr  = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.ExitFollow);
        var enterFindPtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.EnterFind);
        var exitFindPtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.ExitFind);
        var enterMovePtr   = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.EnterMoveTo);
        var exitMovePtr    = BurstCompiler.CompileFunctionPointer<ActionFn>(AniMovementFsmActions.ExitMoveTo);

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
