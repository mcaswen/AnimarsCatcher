using Unity.Entities;
using Unity.Mathematics;

public static class AniMovementFsmIDs
{
    public const ushort kStateOffset = 0;
    public const ushort kCondOffset  = 64;
    public const ushort kActOffset   = 128;

    // StateId，0 预留给通用 S0，这里从 1 开始
    public readonly static ushort S_Idle   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kStateOffset + 1);
    public readonly static ushort S_Follow = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kStateOffset + 2);
    public readonly static ushort S_Find   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kStateOffset + 3);
    public readonly static ushort S_MoveTo = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kStateOffset + 4);

    // ConditionId
    public readonly static ushort C_CommandIdle   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 1); // mode == Idle
    public readonly static ushort C_CommandFollow = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 2); // mode == Follow
    public readonly static ushort C_CommandFind   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 3); // mode == Find && target != null
    public readonly static ushort C_CommandMoveTo = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 4); // mode == MoveTo
    public readonly static ushort C_TargetGone    = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 5); // target == null
    public readonly static ushort C_MoveArrived   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kCondOffset + 6); // K_MoveArrived == true

    // ActionId
    public readonly static ushort A_EnterIdle   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 1);
    public readonly static ushort A_ExitIdle    = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 2);
    public readonly static ushort A_EnterFollow = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 3);
    public readonly static ushort A_ExitFollow  = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 4);
    public readonly static ushort A_EnterFind   = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 5);
    public readonly static ushort A_ExitFind    = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 6);
    public readonly static ushort A_EnterMoveTo = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 7);
    public readonly static ushort A_ExitMoveTo  = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, kActOffset + 8);
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
    public const uint K_CommandMode = 0x0001u;  // int 外部系统控制，驱动状态切换

    // 目标实体
    public const uint K_TargetEntity = 0x0002u;  // Entity（Find 模式时的目标，可以是敌人/资源）
    public const uint K_PlayerEntity = 0x0003u;  // Entity（跟随的机器人主角）

    // MoveTo 静止点
    public const uint K_MoveToPosition = 0x0004u; // float3，MoveTo 目标点

    // 导航请求
    public const uint K_NavRequestVersion = 0x0101u;  // int 为去抖，版本号变化时才下发 SetDestination
    public const uint K_NavTargetPosition = 0x0102u;  // float3
    public const uint K_NavStop = 0x0103u;  // bool
    public const uint K_NavNextUpdateTick   = 0x0104u;  // int 下一次允许更新 NavRequest 的 Tick

    // 到达检测
    public const uint K_MoveArrived = 0x0204u;  // bool

    // 阵列相关
    public const uint K_FormationJoinEventVersion  = 0x0401u; // int，每次请求加入阵列事件版本号加一，外部消费
    public const uint K_FormationLeaveEventVersion = 0x0402u; // int，每次请求离开阵列事件版本号加一，外部消费
    public const uint K_FormationLeader = 0x0403u; // Entity，通常 = PlayerEntity
}
