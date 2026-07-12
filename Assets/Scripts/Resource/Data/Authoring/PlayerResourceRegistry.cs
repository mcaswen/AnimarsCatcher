using Unity.Entities;
using UnityEngine;

/// <summary>
/// 向服务端注册玩家资源 Ghost 预制体
/// </summary>
public class PlayerResourceRegistry : MonoBehaviour
{
    public GameObject PlayerResourceGhostPrefab;

    /// <summary>
    /// 将 GameObject 预制体引用转换为实体引用
    /// </summary>
    class Baker : Baker<PlayerResourceRegistry>
    {
        public override void Bake(PlayerResourceRegistry authoring)
        {
            var holderEntity = GetEntity(TransformUsageFlags.None);
            var prefabEntity = GetEntity(authoring.PlayerResourceGhostPrefab, TransformUsageFlags.Dynamic);

            AddComponent(holderEntity, new PlayerResourceGhostPrefab
            {
                Value = prefabEntity
            });
        }
    }
}

/// <summary>
/// 保存玩家资源 Ghost 预制体实体
/// </summary>
public struct PlayerResourceGhostPrefab : IComponentData
{
    public Entity Value;
}
