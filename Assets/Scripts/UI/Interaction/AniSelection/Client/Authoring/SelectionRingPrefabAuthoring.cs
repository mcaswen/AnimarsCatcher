using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class SelectionRingPrefabAuthoring : MonoBehaviour
{
    public GameObject RingPrefab;
    public float YOffset = 0.02f;

    class Baker : Baker<SelectionRingPrefabAuthoring>
    {
        public override void Bake(SelectionRingPrefabAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var ringPrefabEntity = GetEntity(authoring.RingPrefab, TransformUsageFlags.Renderable);
            AddComponent(entity, new SelectionRingPrefabConfig
            {
                Prefab = ringPrefabEntity,
                YOffset = authoring.YOffset
            });
        }
    }
}

public struct SelectionRingPrefabConfig : IComponentData
{
    public Entity Prefab;
    public float YOffset;
}

public struct SelectionRingRef : IComponentData
{
    public Entity RingEntity;
}
