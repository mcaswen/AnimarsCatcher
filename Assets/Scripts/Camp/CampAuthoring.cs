using Unity.Entities;
using UnityEngine;

/// <summary>
/// 在场景或预制体上配置实体的初始阵营
/// </summary>
[DisallowMultipleComponent]
public class CampAuthoring : MonoBehaviour
{
    public CampType initialCamp = CampType.Neutral;

    class Baker : Baker<CampAuthoring>
    {
        public override void Bake(CampAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new Camp
            {
                Value = authoring.initialCamp
            });
        }
    }
}
