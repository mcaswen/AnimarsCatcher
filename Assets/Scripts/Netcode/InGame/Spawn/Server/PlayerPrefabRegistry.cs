namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 配置服务器创建玩家所需的角色和相机 Ghost Prefab
    /// </summary>
    public class PlayerPrefabRegistry : MonoBehaviour
    {
        public GameObject CharacterPrefab;
        public GameObject CameraPrefab;
    }

    /// <summary>
    /// 保存服务器角色 Ghost Prefab 的单例引用
    /// </summary>
    public struct CharacterGhostPrefab : IComponentData
    {
        /// <summary>
        /// 角色 Ghost Prefab 实体
        /// </summary>
        public Entity Value;
    }

    /// <summary>
    /// 保存玩家相机 Ghost Prefab 的单例引用
    /// </summary>
    public struct CameraGhostPrefab : IComponentData
    {
        /// <summary>
        /// 玩家相机 Ghost Prefab 实体
        /// </summary>
        public Entity Value;
    }

    /// <summary>
    /// 负责将玩家 Prefab 注册表烘焙为实体引用
    /// </summary>
    public class PlayerPrefabRegistryBaker : Baker<PlayerPrefabRegistry>
    {
        public override void Bake(PlayerPrefabRegistry authoring)
        {
            var registryEntity = GetEntity(TransformUsageFlags.None);

            var characterEntity = GetEntity(authoring.CharacterPrefab, TransformUsageFlags.Dynamic);

            var cameraEntity = GetEntity(authoring.CameraPrefab, TransformUsageFlags.Dynamic);

            // 两个引用挂在同一注册实体上，供服务器生成系统以单例方式读取
            AddComponent(registryEntity, new CharacterGhostPrefab { Value = characterEntity });
            AddComponent(registryEntity, new CameraGhostPrefab { Value = cameraEntity });
        }
    }
}
