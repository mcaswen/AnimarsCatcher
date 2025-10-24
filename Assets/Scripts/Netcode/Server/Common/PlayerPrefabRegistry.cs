using Unity.Entities;
using UnityEngine;

public class CharacterPrefabRegistry : MonoBehaviour
{
    public GameObject CharacterPrefab;
}

public struct CharacterGhostPrefab : IComponentData
{
    public Entity Value;
}

public class CharacterPrefabRegistryBaker : Baker<CharacterPrefabRegistry>
{
    public override void Bake(CharacterPrefabRegistry authoring)
    {
        var e = GetEntity(TransformUsageFlags.None);

        var characterEntity = GetEntity(authoring.CharacterPrefab, TransformUsageFlags.Dynamic);

        // 注册为单例，用于创建玩家实例
        AddComponent(e, new CharacterGhostPrefab { Value = characterEntity });
    }
}
