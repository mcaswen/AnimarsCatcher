using Unity.Entities;
using Unity.NetCode;

// [GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct ResourceCarryAssignment : IComponentData
{
    public Entity PlayerRobotEntity; // 要送给哪个玩家机器人

    public int AssignedCarrierAniCount; // 目前分配了多少 Ani
    public int ReadyCarrierAniCount;    // 到位了多少 Ani

    public int IsCarryStarted;          // 0: Ani 正在就位；1: 已经开始往玩家走
}

// [GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct AniCarryResourceOrder : IComponentData
{
    [GhostField] public Entity ResourceEntity; // 要搬运的资源实体
    [GhostField] public int SlotIndex;          // 站位槽索引
}

// 用 IEnableableComponent 来开关
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
[GhostEnabledBit]
public struct AniCommandLockedTag : IComponentData, IEnableableComponent {}

// 标记 资源 正在被搬运
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct ResourceCarryingTag : IComponentData {}

// 请求搬运某个资源
public struct ResourcePickupRequest : IComponentData
{
    // 资源要送给哪个玩家机器人
    public Entity PlayerRobotEntity;

    public int MaximumCarrierAniCountOverride;
}