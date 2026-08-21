using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 向服务端注册玩家资源 Ghost 预制体
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "PlayerResourceRegistry")]
    public class PlayerResourcePrefabRegistryAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("PlayerResourceGhostPrefab")]
        [FormerlySerializedAs("PlayerResourceGhostPrefabReference")]
        [SerializeField] private GameObject _playerResourceGhostPrefab;

        private sealed class Baker : Baker<PlayerResourcePrefabRegistryAuthoring>
        {
            public override void Bake(PlayerResourcePrefabRegistryAuthoring authoring)
            {
                var holderEntity = GetEntity(TransformUsageFlags.None);
                var prefabEntity = GetEntity(authoring._playerResourceGhostPrefab, TransformUsageFlags.Dynamic);

                AddComponent(holderEntity, new PlayerResourceGhostPrefabReference
                {
                    Value = prefabEntity
                });
            }
        }
    }

    /// <summary>
    /// 保存玩家资源 Ghost 预制体 Entity
    /// </summary>
    public struct PlayerResourceGhostPrefabReference : IComponentData
    {
        public Entity Value;
    }
}
