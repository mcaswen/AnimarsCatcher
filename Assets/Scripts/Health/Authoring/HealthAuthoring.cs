using Unity.Entities;
using UnityEngine;

/// <summary>
/// 配置实体的初始生命值并限制最小值为一
/// </summary>
[DisallowMultipleComponent]
public class HealthAuthoring : MonoBehaviour
{
    public int maxHealth = 100;

    /// <summary>
    /// 将生命值配置写入运行时实体
    /// </summary>
    class Baker : Baker<HealthAuthoring>
    {
        /// <summary>
        /// 烘焙当前生命值和最大生命值
        /// </summary>
        /// <param name="authoring">生命值创作组件</param>
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
