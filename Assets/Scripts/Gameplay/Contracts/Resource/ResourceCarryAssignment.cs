using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay.Contracts
{
/// <summary>
/// 资源搬运任务的目标玩家和 Ani 到位进度
/// </summary>
public struct ResourceCarryAssignment : IComponentData
{
    public Entity PlayerRobotEntity; // 资源最终交付的玩家机器人

    public int AssignedCarrierAniCount; // 已分配的搬运 Ani 数量
    public int ReadyCarrierAniCount;    // 已到达站位槽的 Ani 数量

        public int IsCarryStarted;          // 零表示 Ani 正在就位，非零表示资源已开始移动
}

/// <summary>
/// 分配给单个 Ani 的资源实体和站位槽命令
/// </summary>
public struct AniCarryResourceOrder : IComponentData
{
    [GhostField] public Entity ResourceEntity; // 需要协助搬运的资源实体
    [GhostField] public int SlotIndex;          // 对应资源站位缓冲区索引
}

/// <summary>
    /// 可启用标签，用于搬运期间阻止 Ani 接收其他命令
/// </summary>
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniCommandLockedTag : IComponentData, IEnableableComponent {}

/// <summary>
/// 标识已经进入移动阶段的资源实体
/// </summary>
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct ResourceCarryingTag : IComponentData {}

/// <summary>
/// 请求把资源分配给目标玩家机器人的一次性命令
/// </summary>
public struct ResourcePickupRequest : IComponentData
{
    // 资源最终交付的玩家机器人
    public Entity PlayerRobotEntity;

    public int MaximumCarrierAniCountOverride;
}
}
