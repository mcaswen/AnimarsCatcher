using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 队伍执行移动指令时所处的阶段
    /// </summary>
    public enum AniSquadMovementStatus : byte
    {
        AwaitingPath,
        Moving,
        Holding,
        Completed,
        Failed
    }

    /// <summary>
    /// 服务器上的队伍信息，以及从全体成员汇总出的移动能力
    /// </summary>
    public struct AniSquad : IComponentData
    {
        public uint SquadId;
        public int OwnerNetworkId;
        public uint MemberVersion;
        public float MaximumAgentRadius;
        public float MinimumMaxSpeed;
        public float MinimumMaxAcceleration;
    }

    /// <summary>
    /// 记录队伍的寻路请求、动态目标更新和到达进度
    /// </summary>
    public struct AniSquadPathState : IComponentData
    {
        public AniSquadMovementStatus Status;
        public float3 ResolvedTargetPosition;
        public float3 LastSubmittedTargetPosition;
        public uint SubmittedCommandSequence;
        public uint ActiveRequestVersion;
        public uint CountedRequestVersion;
        public int RepathCooldownTicks;
        public int SettledTicks;
        public int FieldRequestCount;
        public int SuccessfulFieldRequestCount;
        public int FailedFieldRequestCount;
        public int CacheHitCount;
    }

    /// <summary>
    /// 队伍的虚拟中心点；阵型和整体移动都以它为参照
    /// </summary>
    public struct AniSquadAnchor : IComponentData
    {
        public float3 Position;
        public float3 Velocity;
        public float3 Forward;
        public int CurrentCellIndex;
    }

    /// <summary>
    /// 记录当前阵型宽度，以及布局和槽位分配是否需要更新
    /// </summary>
    public struct AniSquadFormationState : IComponentData
    {
        public AniSquadFormationKind Kind;
        public int ConfiguredColumnCount;
        public int ColumnCount;
        public int DesiredColumnCount;
        public byte NarrowPath;
        public float ForwardClearance;
        public uint ClearanceVersion;
        public int WidthStableTicks;
        public uint MemberVersion;
        public uint LayoutVersion;
        public uint AssignmentVersion;
    }

    /// <summary>
    /// 队伍中的一名成员及其固定排序编号和阵型槽位
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct AniSquadMember : IBufferElementData
    {
        public Entity Ani;
        public int StableId;
        public int SlotIndex;
        public AniSquadRole Role;
    }

    /// <summary>
    /// 阵型中的一个相对位置，并注明该位置更适合哪类成员
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct AniFormationSlot : IBufferElementData
    {
        public int SlotIndex;
        public float3 LocalOffset;
        public AniSquadRole PreferredRole;
    }

    /// <summary>
    /// 标记一名 Ani 当前属于哪个队伍、占用哪个阵型槽位
    /// </summary>
    public struct AniSquadMembership : IComponentData
    {
        public Entity Squad;
        public uint SquadId;
        public int SlotIndex;
    }

    /// <summary>
    /// 一名 Ani 在队伍移动中使用的速度、体型和转向参数
    /// </summary>
    public struct AniMovementConfig : IComponentData
    {
        public float MaxSpeed;
        public float MaxAcceleration;
        public float AgentRadius;
        public float ArrivalRadius;
        public float RotationSpeedRadians;
    }

    /// <summary>
    /// 阵型系统为成员计算出的世界坐标目标点
    /// </summary>
    public struct AniSlotTarget : IComponentData
    {
        public float3 Position;
    }

    /// <summary>
    /// 成员在下一次移动提交前希望达到的速度
    /// </summary>
    public struct AniPreferredVelocity : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// 记录成员本帧实际采用的速度、槽位误差和提交次数
    /// </summary>
    public struct AniMovementResult : IComponentData
    {
        public float3 AppliedVelocity;
        public float DistanceToSlot;
        public uint CommitCount;
    }
}
