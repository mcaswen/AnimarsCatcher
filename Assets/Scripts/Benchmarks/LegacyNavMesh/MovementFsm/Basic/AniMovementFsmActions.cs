using AnimarsCatcher.Core.Fsm;
using Unity.Burst;
using Unity.Entities;

/// <summary>
/// 定义移动状态进入和退出时对导航黑板执行的原子动作
/// </summary>
[BurstCompile]
public static class AniMovementFsmActions
{
    /// <summary>
    /// 进入空闲状态时停止导航并标记已经到达
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void EnterIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, true);
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavRequestVersion, (int)context.Tick);

        // 空闲状态视为没有未完成的移动目标
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, true);
    }

    /// <summary>
    /// 离开空闲状态时不需要额外清理
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void ExitIdle(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 空操作仍需注册，以保持状态图出入动作结构一致
    }

    /// <summary>
    /// 进入跟随状态时启用导航并发布加入玩家阵型事件
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void EnterFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 目标位置由移动规划系统按玩家和槽位持续更新
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
        
        // 清零更新 Tick，使规划系统可以立即提交首个导航目标
        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        // 跟随目标使用 PlayerEntity，清除寻敌状态遗留的 TargetEntity
        Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.TargetEntity, Entity.Null);

        // 使用当前 Tick 作为事件版本，阵型系统按版本消费
        var playerEntity = Blackboard.GetEntity(ref blackboard, AniMovementBlackboardKeys.PlayerEntity);
        if (playerEntity != Entity.Null)
        {
            Blackboard.SetEntity(ref blackboard, AniMovementBlackboardKeys.FormationLeader, playerEntity);
            Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.FormationJoinEventVersion, (int)context.Tick);
        }

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    /// <summary>
    /// 离开跟随状态时发布阵型离开事件
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void ExitFollow(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.FormationLeaveEventVersion, (int)context.Tick);
    }

    /// <summary>
    /// 进入寻敌状态时启用导航并强制刷新目标位置
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void EnterFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 目标实体位置由移动规划系统写入导航目标
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    /// <summary>
    /// 离开寻敌状态时不需要额外清理
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void ExitFind(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 空操作保留统一的状态图动作入口
    }

    /// <summary>
    /// 进入定点移动状态时启用导航并强制刷新点击目标
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void EnterMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];

        // 点击位置由移动规划系统转换为每个阵型槽位的导航目标
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.NavStop, false);
        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);

        Blackboard.SetInt(ref blackboard, AniMovementBlackboardKeys.NavNextUpdateTick, 0);

        Blackboard.SetBool(ref blackboard, AniMovementBlackboardKeys.MoveArrived, false);
    }

    /// <summary>
    /// 离开定点移动状态时不需要额外清理
    /// </summary>
    /// <param name="entity">执行动作的 Ani 实体</param>
    /// <param name="fsm">实体状态机数据</param>
    /// <param name="context">当前状态机上下文</param>
    [BurstCompile]
    public static void ExitMoveTo(in Entity entity, ref Fsm fsm, in FsmContext context)
    {
        // 空操作保留统一的状态图动作入口
    }
}
