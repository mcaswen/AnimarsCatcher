using AnimarsCatcher.Gameplay.Contracts;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 将场景中的基地配置烘焙为参与阵营、生命值和攻击感知的实体数据
    /// </summary>
    public class BaseAuthoring : MonoBehaviour
    {
        public CampType Camp;
        public bool IsBigBase = true;

        [Header("血量")]
        public int MaxHealth = 1000;

        [Header("用于感知范围的 Collider（建议 BoxCollider）")]
        public Collider SenseCollider;   // 场景里已经有碰撞体就拖进来
    }

    class BaseAuthoringBaker : Baker<BaseAuthoring>
    {
        public override void Bake(BaseAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Renderable);

            AddComponent(entity, new Camp { Value = authoring.Camp });

            AddComponent(entity, new Health
            {
                current    = authoring.MaxHealth,
                max = authoring.MaxHealth
            });

            AddComponent<BaseTag>(entity);

            if (authoring.IsBigBase)
                AddComponent<BigBaseTag>(entity);
            else
                AddComponent<SmallBaseTag>(entity);

            AddComponent<RangedAttackableTag>(entity);

            var box = authoring.SenseCollider as BoxCollider;
            if (!box)
            {
                var collider = authoring.SenseCollider;
                if (!collider)
                {
                    Debug.LogWarning($"[BaseAuthoring] {authoring.name} 没有设置 SenseCollider");
                    return;
                }

                Bounds bounds = collider.bounds;
                AddComponent(entity, new BaseWorldAABB
                {
                    Center      = bounds.center,
                    HalfExtents = bounds.extents
                });
            }
            else
            {
                Bounds bounds = box.bounds;
                AddComponent(entity, new BaseWorldAABB
                {
                    Center      = bounds.center,
                    HalfExtents = bounds.extents
                });
            }
        }
    }
}
