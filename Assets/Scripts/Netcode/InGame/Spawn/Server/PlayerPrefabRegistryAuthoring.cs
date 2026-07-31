namespace AnimarsCatcher.Networking
{
    using UnityEngine;
    using UnityEngine.Serialization;
    using UnityEngine.Scripting.APIUpdating;

    /// <summary>
    /// 配置服务器创建玩家所需的角色和相机 Ghost Prefab
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Networking", "AnimarsCatcher.Networking", "PlayerPrefabRegistry")]
    public class PlayerPrefabRegistryAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("CharacterPrefab")]
        [SerializeField] private GameObject _characterPrefab;

        [FormerlySerializedAs("CameraPrefab")]
        [SerializeField] private GameObject _cameraPrefab;

        private sealed class Baker : Unity.Entities.Baker<PlayerPrefabRegistryAuthoring>
        {
            public override void Bake(PlayerPrefabRegistryAuthoring authoring)
            {
                var registryEntity = GetEntity(Unity.Entities.TransformUsageFlags.None);

                var characterEntity = GetEntity(
                    authoring._characterPrefab,
                    Unity.Entities.TransformUsageFlags.Dynamic);

                var cameraEntity = GetEntity(
                    authoring._cameraPrefab,
                    Unity.Entities.TransformUsageFlags.Dynamic);

            // 两个引用挂在同一注册实体上，供服务器生成系统以单例方式读取
                AddComponent(registryEntity, new CharacterGhostPrefabReference { Value = characterEntity });
                AddComponent(registryEntity, new CameraGhostPrefabReference { Value = cameraEntity });
            }
        }
    }
}
