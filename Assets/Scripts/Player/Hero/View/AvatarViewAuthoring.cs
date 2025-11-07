using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class AvatarViewAuthoring : MonoBehaviour
{
    public GameObject ViewPrefab;
    
    class Baker : Baker<AvatarViewAuthoring>
    {
        public override void Bake(AvatarViewAuthoring a)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            // 托管对象，只存一个 GameObject 引用
            AddComponentObject(entity, new AvatarViewPrefabReference { ViewPrefab = a.ViewPrefab });
        }
    }
}

public class AvatarViewPrefabReference : IComponentData
{
    public GameObject ViewPrefab;
}

public struct AvatarViewSpawnedTag : IComponentData {}
