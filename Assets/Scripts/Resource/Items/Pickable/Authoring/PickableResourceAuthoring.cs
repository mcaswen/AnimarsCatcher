using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 配置可由 Picker Ani 协作搬运的资源实体
/// </summary>
[DisallowMultipleComponent]
public class PickableResourceAuthoring : MonoBehaviour
{
    public ResourceItemKind ResourceItemKind;

    public int TotalResourceAmount = 10;

    public int MinimumCarrierAniCount = 1;
    public int MaximumCarrierAniCount = 3;

    public float StartCarryDistance = 1f;
    public float DeliveryArrivalRadius = 3f;

    public float CarryMoveSpeed = 3.0f;

    // 搬运 Ani 相对资源中心的局部站位列表 为空时使用中心槽位
    public Vector3[] CarrierSlotLocalOffsets;

    class Baker : Baker<PickableResourceAuthoring>
    {
        public override void Bake(PickableResourceAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new PickableResource
            {
                ResourceItemKind       = authoring.ResourceItemKind,
                TotalResourceAmount    = authoring.TotalResourceAmount,
                MinimumCarrierAniCount = authoring.MinimumCarrierAniCount,
                MaximumCarrierAniCount = authoring.MaximumCarrierAniCount,
                StartCarryDistance     = authoring.StartCarryDistance,
                DeliveryArrivalRadius  = authoring.DeliveryArrivalRadius,
                CarryMoveSpeed         = authoring.CarryMoveSpeed,
            });

            DynamicBuffer<PickableResourceCarrierSlot> slotBuffer =
                AddBuffer<PickableResourceCarrierSlot>(entity);

            // 自定义槽位按配置顺序写入 该顺序同时作为分配索引
            if (authoring.CarrierSlotLocalOffsets != null &&
                authoring.CarrierSlotLocalOffsets.Length > 0)
            {
                foreach (Vector3 offset in authoring.CarrierSlotLocalOffsets)
                {
                    slotBuffer.Add(new PickableResourceCarrierSlot
                    {
                        LocalOffset = (float3)offset
                    });
                }
            }
            else
            {
                // 默认一个槽位在资源中心
                slotBuffer.Add(new PickableResourceCarrierSlot
                {
                    LocalOffset = float3.zero
                });
            }

            AddComponent<PickableResourceTag>(entity);
            AddComponent<ResourceItemTag>(entity);

        }
    }
}
