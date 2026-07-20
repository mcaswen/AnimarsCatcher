using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 配置实体的初始生命值并限制最小值为一
    /// </summary>
    [DisallowMultipleComponent]
    public class HealthAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("maxHealth")]
        [SerializeField] private int _maximumHealth = 100;

        private sealed class Baker : Baker<HealthAuthoring>
        {
            public override void Bake(HealthAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                int maximumHealth = Mathf.Max(1, authoring._maximumHealth);

                AddComponent(entity, new Health
                {
                    Current = maximumHealth,
                    Maximum = maximumHealth,
                });
            }
        }
    }
}
