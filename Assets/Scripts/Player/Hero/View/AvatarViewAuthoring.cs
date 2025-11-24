using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class AvatarViewAuthoring : MonoBehaviour
{
    [SerializeField] private GameObject ViewPrefab;
    [SerializeField] private AvatarViewType avatarViewType;
    
    class Baker : Baker<AvatarViewAuthoring>
    {
        public override void Bake(AvatarViewAuthoring authoring)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            // 托管对象，只存一个 GameObject 引用
            AddComponentObject(entity, new AvatarViewPrefabReference 
            { 
                ViewPrefab = authoring.ViewPrefab, 
                ViewType = authoring.avatarViewType 
            });
        }
    }
}

public class AvatarViewPrefabReference : IComponentData
{
    public GameObject ViewPrefab;
    public AvatarViewType ViewType;
}

public enum AvatarViewType
{
    None = 0,
    Robot = 1,
    BlasterAni = 2,
    PickerAni = 3,
    Resource = 4,
}


public struct AvatarViewSpawnedTag : IComponentData {}
