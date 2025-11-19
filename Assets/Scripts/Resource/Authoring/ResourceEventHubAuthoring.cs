using Unity.Entities;
using UnityEngine;

public class ResourceEventHubAuthoring : MonoBehaviour
{
    class Baker : Baker<ResourceEventHubAuthoring>
    {
        public override void Bake(ResourceEventHubAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddBuffer<FoodAmountChangedEvent>(entity);
            AddBuffer<CrystalAmountChangedEvent>(entity);
            AddComponent<ResourceEventHubTag>(entity);
        }
    }
}

public struct ResourceEventHubTag : IComponentData { }
