using Unity.Entities;
using UnityEngine;

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
