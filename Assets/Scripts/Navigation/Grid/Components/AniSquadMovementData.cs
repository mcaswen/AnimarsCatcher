using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 表示 Squad 当前的基础移动状态
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
    /// 保存服务器专用 Squad 的稳定身份和成员聚合参数
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
    /// 保存订单解析、重规划和到达判定状态
    /// </summary>
    public struct AniSquadPathState : IComponentData
    {
        public AniSquadMovementStatus Status;
        public float3 ResolvedTargetPosition;
        public float3 LastSubmittedTargetPosition;
        public uint SubmittedOrderSequence;
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
    /// 保存不绑定具体成员的 Squad 锚点状态
    /// </summary>
    public struct AniSquadAnchor : IComponentData
    {
        public float3 Position;
        public float3 Velocity;
        public float3 Forward;
        public int CurrentCellIndex;
    }

    /// <summary>
    /// 保存基础阵型布局和槽位版本
    /// </summary>
    public struct AniSquadFormationState : IComponentData
    {
        public AniSquadFormationKind Kind;
        public int ColumnCount;
        public uint MemberVersion;
        public uint LayoutVersion;
        public uint AssignmentVersion;
    }

    /// <summary>
    /// 保存 Squad 成员和稳定排序键
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct AniSquadMember : IBufferElementData
    {
        public Entity Ani;
        public int StableId;
        public int SlotIndex;
    }

    /// <summary>
    /// 保存阵型局部空间中的一个槽位
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct AniFormationSlot : IBufferElementData
    {
        public int SlotIndex;
        public float3 LocalOffset;
    }

    /// <summary>
    /// 保存 Ani 到 Squad 的服务器归属和当前槽位
    /// </summary>
    public struct AniSquadMembership : IComponentData
    {
        public Entity Squad;
        public uint SquadId;
        public int SlotIndex;
    }

    /// <summary>
    /// 保存阶段四开阔地移动使用的成员参数
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
    /// 保存阵型系统为成员计算的世界空间槽位目标
    /// </summary>
    public struct AniSlotTarget : IComponentData
    {
        public float3 Position;
    }

    /// <summary>
    /// 保存受速度和加速度约束后的成员期望速度
    /// </summary>
    public struct AniPreferredVelocity : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// 保存唯一移动提交系统写回的成员结果
    /// </summary>
    public struct AniMovementResult : IComponentData
    {
        public float3 AppliedVelocity;
        public float DistanceToSlot;
        public uint CommitCount;
    }

    /// <summary>
    /// 标识 Grid 群体移动 Benchmark 创建的 Ani
    /// </summary>
    public struct NavigationGridMovementBenchmarkAni : IComponentData
    {
        public int AgentIndex;
    }
}
