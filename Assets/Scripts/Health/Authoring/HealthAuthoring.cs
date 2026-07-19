using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置实体的初始生命值并限制最小值为一
    /// </summary>
    [DisallowMultipleComponent]
    public class HealthAuthoring : MonoBehaviour
    {
        public int maxHealth = 100;

        class Baker : Baker<HealthAuthoring>
        {
            public override void Bake(HealthAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                int maxHealth = Mathf.Max(1, authoring.maxHealth);

                AddComponent(entity, new Health
                {
                    current = maxHealth,
                    max     = maxHealth,
                });
            }
        }
    }
}
