using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 可搬运资源的价值、人数门槛和移动参数
    /// </summary>
    [GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
    public struct PickableResource : IComponentData
    {
        // 交付后计入玩家的资源类别
        [GhostField]
        public ResourceItemKind ResourceItemKind;

        // 成功交付后增加的资源总量
        [GhostField]
        public int TotalResourceAmount;

        // 启动搬运所需的最少 Ani 数量
        [GhostField]
        public int MinimumCarrierAniCount;

        // 单次任务允许分配的最大 Ani 数量
        [GhostField]
        public int MaximumCarrierAniCount;

        // Ani 判定到达站位槽的距离
        [GhostField]
        public float StartCarryDistance;

        // 资源判定到达玩家机器人的半径
        [GhostField]
        public float DeliveryArrivalRadius;

        // 资源进入搬运阶段后的移动速度
        [GhostField]
        public float CarryMoveSpeed;
    }

    /// <summary>
    /// 搬运 Ani 相对资源实体的局部站位槽
    /// </summary>
    public struct PickableResourceCarrierSlot : IBufferElementData
    {
        public float3 LocalOffset;
    }

    /// <summary>
    /// 标识可被 Picker 交互系统选中的资源实体
    /// </summary>
    [GhostComponent(SendTypeOptimization = GhostSendType.AllClients)]
    public struct PickableResourceTag : IComponentData {}
}
