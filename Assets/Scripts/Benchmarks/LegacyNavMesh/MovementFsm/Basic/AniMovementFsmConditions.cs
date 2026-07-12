using Unity.Burst;
using Unity.Entities;

/// <summary>
/// 定义移动状态图可由 Burst 调用的黑板条件函数
/// </summary>
[BurstCompile]
public static class AniMovementFsmConditions
{
    /// <summary>
    /// 判断外部命令是否要求进入空闲状态
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>命令模式为空闲时返回真</returns>
    [BurstCompile]
    public static bool IsCommandIdle(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.Idle;
    }

    /// <summary>
    /// 判断外部命令是否要求进入跟随状态
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>命令模式为跟随时返回真</returns>
    [BurstCompile]
    public static bool IsCommandFollow(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.Follow;
    }

    /// <summary>
    /// 判断外部命令是否要求寻敌且目标实体有效
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>命令模式为寻敌且目标非空时返回真</returns>
    [BurstCompile]
    public static bool IsCommandFind(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);

        if (mode != (int)AniMovementCommandMode.Find)
            return false;

        var target = blackboard.GetEntity(AniMovementBlackboardKeys.TargetEntity);
        return target != Entity.Null;
    }

    /// <summary>
    /// 判断外部命令是否要求移动到指定位置
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>命令模式为定点移动时返回真</returns>
    [BurstCompile]
    public static bool IsCommandMoveTo(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        int mode = blackboard.GetInt(AniMovementBlackboardKeys.CommandMode);
        return mode == (int)AniMovementCommandMode.MoveTo;
    }

    /// <summary>
    /// 判断寻敌目标引用是否已经清空
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>目标实体为空时返回真</returns>
    [BurstCompile]
    public static bool IsTargetGone(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        var target = blackboard.GetEntity(AniMovementBlackboardKeys.TargetEntity);
        return target == Entity.Null;
    }

    /// <summary>
    /// 判断移动规划系统是否已将实体标记为到达
    /// </summary>
    /// <param name="entity">正在评估的 Ani 实体</param>
    /// <param name="context">当前状态机上下文</param>
    /// <returns>到达标记为真时返回真</returns>
    [BurstCompile]
    public static bool HasMoveArrived(in Entity entity, in FsmContext context)
    {
        var blackboard = context.BlackboardLookup[entity];
        return blackboard.GetBool(AniMovementBlackboardKeys.MoveArrived);
    }
}
