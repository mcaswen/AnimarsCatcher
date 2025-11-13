using Unity.Burst;
using Unity.Entities;

public struct BlasterAniRegistryInitialized : IComponentData {}

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
[UpdateBefore(typeof(FsmEvaluateSystem))] // 确保首帧评估前已完成注册
public partial struct BlasterFsmAniBinderSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // 已注册过就直接返回（防止多 World 重复跑该 System ）
        if (SystemAPI.HasSingleton<BlasterAniRegistryInitialized>()) 
            return;

        state.EntityManager.CreateEntity(typeof(BlasterAniRegistryInitialized));

        // 条件注册
        var shouldFollowPtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(BlasterAniFsmConditions.C_ShouldFollow);
        var shouldFindPtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(BlasterAniFsmConditions.C_ShouldFind);
        var shouldShootPtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(BlasterAniFsmConditions.C_ShouldShoot);
        var targetGonePtr = BurstCompiler.CompileFunctionPointer<ConditionFn>(BlasterAniFsmConditions.C_TargetGone);

        FsmRegistry.RegisterCondition((ConditionId)BlasterAniFsmIDs.C_ShouldFollow, shouldFollowPtr);
        FsmRegistry.RegisterCondition((ConditionId)BlasterAniFsmIDs.C_ShouldFind, shouldFindPtr);
        FsmRegistry.RegisterCondition((ConditionId)BlasterAniFsmIDs.C_ShouldShoot, shouldShootPtr);
        FsmRegistry.RegisterCondition((ConditionId)BlasterAniFsmIDs.C_TargetGone, targetGonePtr);

        // 动作注册
        var enterIdlePtr = BurstCompiler.CompileFunctionPointer<ActionFn>(BlasterAniFsmActions.A_EnterIdle);
        var enterFollowPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(BlasterAniFsmActions.A_EnterFollow);
        var exitFollowPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(BlasterAniFsmActions.A_ExitFollow);
        var enterFindPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(BlasterAniFsmActions.A_EnterFind);
        var enterShootPtr = BurstCompiler.CompileFunctionPointer<ActionFn>(BlasterAniFsmActions.A_EnterShoot);

        FsmRegistry.RegisterAction((ActionId)BlasterAniFsmIDs.A_EnterIdle,   enterIdlePtr);
        FsmRegistry.RegisterAction((ActionId)BlasterAniFsmIDs.A_EnterFollow, enterFollowPtr);
        FsmRegistry.RegisterAction((ActionId)BlasterAniFsmIDs.A_ExitFollow,  exitFollowPtr);
        FsmRegistry.RegisterAction((ActionId)BlasterAniFsmIDs.A_EnterFind,   enterFindPtr);
        FsmRegistry.RegisterAction((ActionId)BlasterAniFsmIDs.A_EnterShoot,  enterShootPtr);

        state.Enabled = false;
    }

}