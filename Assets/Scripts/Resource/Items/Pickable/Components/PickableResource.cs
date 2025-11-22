using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct PickableResource : IComponentData
{   
    // 资源物品种类
    [GhostField]
    public ResourceItemKind ResourceItemKind;

    // 拾取成功后给玩家增加多少资源
    [GhostField]
    public int TotalResourceAmount;

    // 要求的最少 Ani 数
    [GhostField]
    public int MinimumCarrierAniCount;

    // 允许分配的最大 Ani 数
    [GhostField]
    public int MaximumCarrierAniCount;

    // Ani 就位距离
    [GhostField]
    public float StartCarryDistance;

    // 到达玩家机器人身边的到达半径
    [GhostField]
    public float DeliveryArrivalRadius;

    // 资源往玩家机器人移动的速度
    [GhostField] 
    public float CarryMoveSpeed;
}

// 搬运时 Ani 相对物体的站位槽
public struct PickableResourceCarrierSlot : IBufferElementData
{
    public float3 LocalOffset;
}

// 用来让 Attack 系统区分出“这是能被 Picker 拾取的资源”
[GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
public struct PickableResourceTag : IComponentData {}
