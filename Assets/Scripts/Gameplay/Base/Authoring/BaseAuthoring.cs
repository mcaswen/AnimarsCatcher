using AnimarsCatcher.Gameplay.Contracts;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 将场景中的基地配置烘焙为参与阵营、生命值和攻击感知的 Entity 数据
    /// </summary>
    public class BaseAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("Camp")]
        [SerializeField] private CampType _camp;

        [FormerlySerializedAs("IsBigBase")]
        [SerializeField] private bool _isBigBase = true;

        [Header("血量")]
        [FormerlySerializedAs("MaxHealth")]
        [SerializeField] private int _maximumHealth = 1000;

        [Header("用于感知范围的 Collider（建议 BoxCollider）")]
        [FormerlySerializedAs("SenseCollider")]
        [SerializeField] private Collider _senseCollider;

        private sealed class Baker : Unity.Entities.Baker<BaseAuthoring>
        {
            public override void Bake(BaseAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Renderable);

                AddComponent(entity, new Camp { Value = authoring._camp });

                AddComponent(entity, new Health
                {
                    Current = authoring._maximumHealth,
                    Maximum = authoring._maximumHealth
                });

                AddComponent<BaseTag>(entity);

                if (authoring._isBigBase)
                    AddComponent<BigBaseTag>(entity);
                else
                    AddComponent<SmallBaseTag>(entity);

                AddComponent<RangedAttackableTag>(entity);

                var box = authoring._senseCollider as BoxCollider;
                if (!box)
                {
                    var collider = authoring._senseCollider;
                    if (!collider)
                    {
                        Debug.LogWarning($"[BaseAuthoring] {authoring.name} 没有设置 SenseCollider");
                        return;
                    }

                    Bounds bounds = collider.bounds;
                    AddComponent(entity, new BaseWorldAABB
                    {
                        Center = bounds.center,
                        HalfExtents = bounds.extents
                    });
                }
                else
                {
                    Bounds bounds = box.bounds;
                    AddComponent(entity, new BaseWorldAABB
                    {
                        Center = bounds.center,
                        HalfExtents = bounds.extents
                    });
                }
            }
        }
    }
}
