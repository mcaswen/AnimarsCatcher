using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

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

    // Ani 围在资源周围的相对坐标（局部空间），不填则默认只有一个槽位在资源中心
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
