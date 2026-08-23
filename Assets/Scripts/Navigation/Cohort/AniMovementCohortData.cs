using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 表示 MovementOrder 从切分到结束的生命周期状态
    /// </summary>
    public enum AniMovementOrderStatus : byte
    {
        Pending,
        Active,
        Completed,
        Failed,
        Superseded
    }

    /// <summary>
    /// 表示一个 Cohort 当前是否仍在等待寻路或推进成员
    /// </summary>
    public enum AniMovementCohortStatus : byte
    {
        AwaitingPath,
        Moving,
        Holding,
        Completed,
        Failed
    }

    /// <summary>
    /// 覆盖默认 Cohort 容量，硬上限会阻止异常配置重新形成大 Squad
    /// </summary>
    public struct AniMovementCohortSettings : IComponentData
    {
        public int PreferredMemberCapacity;
        public int MaximumMemberCapacity;
    }

    /// <summary>
    /// 汇总一份 MovementOrder 的切分、目标区域和最终执行状态
    /// </summary>
    public struct AniMovementOrderState : IComponentData
    {
        public AniMovementOrderStatus Status;
        public int ValidMemberCount;
        public int ActiveCohortCount;

        // 死亡、移除或换令后递增的成员版本
        public uint MemberVersion;

        // 动态目标跨 Cell 后递增的目标版本
        public uint TargetVersion;

        public byte GoalAssignmentPending;

        // 本 Tick 从固定坐标或目标 Entity 解析出的坐标
        public float3 ResolvedTargetPosition;

        // 最近一次完成目标区域分配时使用的原始目标坐标
        public float3 GoalRegionSourcePosition;

        // 最近一次成功分配落点时使用的投影中心
        public float3 GoalRegionCenterPosition;

        // 不依赖 Entity 编号的 Cohort 成员切分摘要
        public ulong CohortPartitionHash;

        // 不依赖 Entity 编号的目标落点分配摘要
        public ulong GoalRegionHash;
    }

    /// <summary>
    /// 保存共享一次寻路请求的有界成员组，不表达任何可见阵型
    /// </summary>
    public struct AniMovementCohort : IComponentData
    {
        public uint CohortId;
        public Entity Order;

        // 冻结订单处理先后关系的服务器序号
        public uint OrderSequence;

        public int OwnerNetworkId;

        // 决定成员能否共享通行上下文的配置键
        public uint AgentProfile;

        // 切分时代表位置所在的起始 Cluster
        public int StartClusterId;

        public int MemberCount;

        // Cohort 内成员变化后递增的版本
        public uint MemberVersion;

        // 当前落点和路径请求对应的订单目标版本
        public uint TargetVersion;

        public float MaximumAgentRadius;
        public float MinimumMaxSpeed;
        public float MinimumMaxAcceleration;

        // 当前成员位置的平均值，用作共享请求起点
        public float3 RepresentativePosition;
    }

    /// <summary>
    /// 保存 Cohort 的动态目标、寻路版本和到达进度
    /// </summary>
    public struct AniMovementCohortPathState : IComponentData
    {
        public AniMovementCohortStatus Status;

        // 目标区域投影到可站立 Cell 后的实际寻路终点
        public float3 GoalRegionCenterPosition;

        // 最近一次实际提交给 Flow 的目标坐标
        public float3 LastSubmittedTargetPosition;

        // 当前允许写回结果的请求版本
        public uint ActiveRequestVersion;

        // 防止同一请求重复累计指标的版本
        public uint CountedRequestVersion;

        // 最近一次请求使用的订单目标版本
        public uint SubmittedTargetVersion;

        // 动态目标连续变化时限制重复请求频率
        public int RepathCooldownTicks;

        // 全体成员连续满足到达条件的 Tick 数
        public int SettledTicks;
        public int FieldRequestCount;
        public int SuccessfulFieldRequestCount;
        public int FailedFieldRequestCount;
        public int CacheHitCount;
    }

    /// <summary>
    /// 保存 Cohort 继承的订单目标语义
    /// </summary>
    public struct AniMovementCohortTarget : IComponentData
    {
        public AniSquadCommandMode Mode;
        public Entity TargetEntity;
        public float TargetStoppingDistance;
    }

    /// <summary>
    /// 保存 Cohort 内按稳定空间顺序排列的成员
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct AniMovementCohortMember : IBufferElementData
    {
        public Entity Ani;
        public int StableId;
    }

    /// <summary>
    /// 标记 Ani 当前所属的自由移动 Cohort
    /// </summary>
    public struct AniMovementCohortMembership : IComponentData
    {
        public Entity Cohort;
        public uint CohortId;
        public int StableId;
        public uint AgentProfile;
    }

    /// <summary>
    /// 保存 Ani 在自然目标区域中独占的稳定落点
    /// </summary>
    public struct AniGoalAssignment : IComponentData
    {
        public int TargetCellIndex;
        public float3 TargetPosition;
        public float ArrivalRadius;
        public float InfluenceRadius;
        public uint TargetVersion;
    }
}
