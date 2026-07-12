using Unity.Entities;
using Unity.Mathematics;

public static class AniMovementFsmIds
{
    public const ushort StateOffset = 0;
    public const ushort ConditionOffset = 64;
    public const ushort ActionOffset = 128;

    // StateId，0 预留给通用 S0，这里从 1 开始
    public static readonly ushort IdleStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 1);
    public static readonly ushort FollowStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 2);
    public static readonly ushort FindStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 3);
    public static readonly ushort MoveToStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 4);

    // ConditionId
    public static readonly ushort CommandIdleConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 1); // mode == Idle
    public static readonly ushort CommandFollowConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 2); // mode == Follow
    public static readonly ushort CommandFindConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 3); // mode == Find && target != null
    public static readonly ushort CommandMoveToConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 4); // mode == MoveTo
    public static readonly ushort TargetGoneConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 5); // target == null
    public static readonly ushort MoveArrivedConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 6); // MoveArrived == true

    // ActionId
    public static readonly ushort EnterIdleActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 1);
    public static readonly ushort ExitIdleActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 2);
    public static readonly ushort EnterFollowActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 3);
    public static readonly ushort ExitFollowActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 4);
    public static readonly ushort EnterFindActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 5);
    public static readonly ushort ExitFindActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 6);
    public static readonly ushort EnterMoveToActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 7);
    public static readonly ushort ExitMoveToActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 8);
}

public enum AniMovementCommandMode : int
{
    Idle   = 0,
    Follow = 1,
    Find   = 2,
    MoveTo = 3,
}

// 移动相关 黑板 Key
public static class AniMovementBlackboardKeys
{
    // 外部输入命令
    public const uint CommandMode = 0x0001u;  // int 外部系统控制，驱动状态切换

    // 目标实体
    public const uint TargetEntity = 0x0002u;  // Entity（Find 模式时的目标，可以是敌人/资源）
    public const uint PlayerEntity = 0x0003u;  // Entity（跟随的机器人主角）

    // MoveTo 静止点
    public const uint MoveToPosition = 0x0004u; // float3，MoveTo 目标点

    // 导航请求
    public const uint NavRequestVersion = 0x0101u;  // int 为去抖，版本号变化时才下发 SetDestination
    public const uint NavTargetPosition = 0x0102u;  // float3
    public const uint NavStop = 0x0103u;  // bool
    public const uint NavNextUpdateTick = 0x0104u;  // int 下一次允许更新 NavRequest 的 Tick

    // 到达检测
    public const uint MoveArrived = 0x0204u;  // bool

    // 阵列相关
    public const uint FormationJoinEventVersion = 0x0401u; // int，每次请求加入阵列事件版本号加一，外部消费
    public const uint FormationLeaveEventVersion = 0x0402u; // int，每次请求离开阵列事件版本号加一，外部消费
    public const uint FormationLeader = 0x0403u; // Entity，通常 = PlayerEntity

    // 静止阵列用缓存
    public const uint MoveFormationTargetPoint = 0x0404u; // float3，阵列移动目标点
    public const uint MoveFormationForward = 0x0405u; // float3，阵列移动朝向
}
