using Unity.Entities;
using UnityEngine;

public class PlayerPrefabRegistry : MonoBehaviour
{
    public GameObject CharacterPrefab;
    public GameObject CameraPrefab;
}

public struct CharacterGhostPrefab : IComponentData
{
    public Entity Value;
}

public struct CameraGhostPrefab : IComponentData
{
    public Entity Value;
}

public class CharacterPrefabRegistryBaker : Baker<PlayerPrefabRegistry>
{
    public override void Bake(PlayerPrefabRegistry authoring)
    {
        var registryEntity = GetEntity(TransformUsageFlags.None);

        var characterEntity = GetEntity(authoring.CharacterPrefab, TransformUsageFlags.Dynamic);

        var cameraEntity = GetEntity(authoring.CameraPrefab, TransformUsageFlags.Dynamic);

        // 注册为单例，用于创建玩家实例
        AddComponent(registryEntity, new CharacterGhostPrefab { Value = characterEntity });
        AddComponent(registryEntity, new CameraGhostPrefab { Value = cameraEntity });
    }
}
