using Unity.Entities;
using UnityEngine;

[DisallowMultipleComponent]
public class CharacterViewAuthoring : MonoBehaviour
{
    public GameObject ViewPrefab;
    
    class Baker : Baker<CharacterViewAuthoring>
    {
        public override void Bake(CharacterViewAuthoring a)
        {
            Entity entity = GetEntity(TransformUsageFlags.Dynamic);
            // 托管对象，只存一个 GameObject 引用
            AddComponentObject(entity, new CharacterViewPrefab { ViewPrefab = a.ViewPrefab });
        }
    }
}

public class CharacterViewPrefab : IComponentData
{
    public GameObject ViewPrefab;
}

public struct ViewSpawnedTag : IComponentData {}
