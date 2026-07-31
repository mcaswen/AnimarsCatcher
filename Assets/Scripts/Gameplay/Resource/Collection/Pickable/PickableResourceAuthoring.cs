using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置可由 Picker Ani 协作搬运的资源实体
    /// </summary>
    [DisallowMultipleComponent]
    public class PickableResourceAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("ResourceItemKind")]
        [SerializeField] private ResourceItemKind _resourceKind;

        [FormerlySerializedAs("TotalResourceAmount")]
        [SerializeField] private int _totalResourceAmount = 10;

        [FormerlySerializedAs("MinimumCarrierAniCount")]
        [SerializeField] private int _minimumCarrierAniCount = 1;

        [FormerlySerializedAs("MaximumCarrierAniCount")]
        [SerializeField] private int _maximumCarrierAniCount = 3;

        [FormerlySerializedAs("StartCarryDistance")]
        [SerializeField] private float _startCarryDistance = 1f;

        [FormerlySerializedAs("DeliveryArrivalRadius")]
        [SerializeField] private float _deliveryArrivalRadius = 3f;

        [FormerlySerializedAs("CarryMoveSpeed")]
        [SerializeField] private float _carryMoveSpeed = 3.0f;

        // 搬运 Ani 相对资源中心的局部站位列表，为空时使用中心槽位
        [FormerlySerializedAs("CarrierSlotLocalOffsets")]
        [SerializeField] private Vector3[] _carrierSlotLocalOffsets;

        private sealed class Baker : Baker<PickableResourceAuthoring>
        {
            public override void Bake(PickableResourceAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new PickableResource
                {
                    ResourceItemKind       = authoring._resourceKind,
                    TotalResourceAmount    = authoring._totalResourceAmount,
                    MinimumCarrierAniCount = authoring._minimumCarrierAniCount,
                    MaximumCarrierAniCount = authoring._maximumCarrierAniCount,
                    StartCarryDistance     = authoring._startCarryDistance,
                    DeliveryArrivalRadius  = authoring._deliveryArrivalRadius,
                    CarryMoveSpeed         = authoring._carryMoveSpeed,
                });

                DynamicBuffer<PickableResourceCarrierSlot> slotBuffer =
                    AddBuffer<PickableResourceCarrierSlot>(entity);

            // 自定义槽位按配置顺序写入，该顺序同时作为分配索引
                if (authoring._carrierSlotLocalOffsets != null &&
                    authoring._carrierSlotLocalOffsets.Length > 0)
                {
                    foreach (Vector3 offset in authoring._carrierSlotLocalOffsets)
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
}
