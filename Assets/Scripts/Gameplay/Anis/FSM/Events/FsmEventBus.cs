using Unity.Entities;
using AnimarsCatcher.Core.Fsm;

namespace AnimarsCatcher.Gameplay
{
/// <summary>
/// 定义通过 Tick 版本传播的状态机事件键
/// </summary>
public static class BlackboardEventKeys
{
    public const uint AssignedToPlayerTick = 0xE001u;
    public const uint AssignedToTargetTick = 0xE002u;
    public const uint TargetLostTick = 0xE003u;
}

/// <summary>
/// 通过写入当前 Tick 在实体黑板上发布一次性状态机事件
/// </summary>
public static class FsmEventBus
{
    /// <summary>
    /// 发布实体被分配给玩家的事件版本
    /// </summary>
    /// <param name="blackboard">接收事件的实体黑板</param>
    /// <param name="context">提供当前 Tick 的状态机上下文</param>
    public static void RaiseAssignedToPlayer(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.AssignedToPlayerTick, (int)context.Tick);
    }

    /// <summary>
    /// 发布实体被分配到目标的事件版本
    /// </summary>
    /// <param name="blackboard">接收事件的实体黑板</param>
    /// <param name="context">提供当前 Tick 的状态机上下文</param>
    public static void RaiseAssignedToTarget(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.AssignedToTargetTick, (int)context.Tick);
    }

    /// <summary>
    /// 发布实体失去目标的事件版本
    /// </summary>
    /// <param name="blackboard">接收事件的实体黑板</param>
    /// <param name="context">提供当前 Tick 的状态机上下文</param>
    public static void RaiseTargetLost(ref DynamicBuffer<FsmVar> blackboard, in FsmContext context)
    {
        blackboard.SetInt(BlackboardEventKeys.TargetLostTick, (int)context.Tick);
    }

}
}
