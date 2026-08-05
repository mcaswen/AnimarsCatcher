using AnimarsCatcher.Gameplay;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 定义移动状态、条件和动作在全局 FSM 标识符空间中的位置
    /// </summary>
    public static class AniMovementFsmIds
    {
        public const ushort StateOffset = 0;
        public const ushort ConditionOffset = 64;
        public const ushort ActionOffset = 128;

        // 状态标识符从一开始，零值保留给通用 None
        public static readonly ushort IdleStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 1);
        public static readonly ushort FollowStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 2);
        public static readonly ushort FindStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 3);
        public static readonly ushort MoveToStateId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, StateOffset + 4);

        // 状态迁移条件标识符
        public static readonly ushort CommandIdleConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 1);
        public static readonly ushort CommandFollowConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 2);
        public static readonly ushort CommandFindConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 3);
        public static readonly ushort CommandMoveToConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 4);
        public static readonly ushort TargetGoneConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 5);
        public static readonly ushort MoveArrivedConditionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ConditionOffset + 6);

        // 状态进入和退出动作标识符
        public static readonly ushort EnterIdleActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 1);
        public static readonly ushort ExitIdleActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 2);
        public static readonly ushort EnterFollowActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 3);
        public static readonly ushort ExitFollowActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 4);
        public static readonly ushort EnterFindActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 5);
        public static readonly ushort ExitFindActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 6);
        public static readonly ushort EnterMoveToActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 7);
        public static readonly ushort ExitMoveToActionId = FsmIdSpace.Of(FsmIdSpace.AniMovementBase, ActionOffset + 8);
    }

    /// <summary>
    /// 表示外部系统要求移动状态机执行的高层命令
    /// </summary>
    public enum AniMovementCommandMode : int
    {
        Idle   = 0,
        Follow = 1,
        Find   = 2,
        MoveTo = 3,
    }

    /// <summary>
    /// 定义移动、导航和阵型系统共享的黑板键
    /// </summary>
    public static class AniMovementBlackboardKeys
    {
        // 外部系统写入的命令模式，驱动状态切换
        public const uint CommandMode = 0x0001u;

        // Find 模式使用的敌人或资源目标
        public const uint TargetEntity = 0x0002u;

        // Follow 模式使用的玩家主角
        public const uint PlayerEntity = 0x0003u;

        // MoveTo 命令的世界空间目标点
        public const uint MoveToPosition = 0x0004u;

        // 版本变化时才向 NavMesh 下发新目标
        public const uint NavRequestVersion = 0x0101u;

        // 导航目标的世界空间坐标
        public const uint NavTargetPosition = 0x0102u;

        // 表示导航是否应停止
        public const uint NavStop = 0x0103u;

        // 下一次允许更新导航请求的 Tick
        public const uint NavNextUpdateTick = 0x0104u;

        // 表示当前移动目标是否已经到达
        public const uint MoveArrived = 0x0204u;

        // 加入阵型请求的消费版本
        public const uint FormationJoinEventVersion = 0x0401u;

        // 离开阵型请求的消费版本
        public const uint FormationLeaveEventVersion = 0x0402u;

        // 当前阵型队长，通常与 PlayerEntity 相同
        public const uint FormationLeader = 0x0403u;

        // 定点移动时冻结的阵型世界锚点
        public const uint MoveFormationTargetPoint = 0x0404u;

        // 定点移动时冻结的阵型世界前向
        public const uint MoveFormationForward = 0x0405u;
    }
}
