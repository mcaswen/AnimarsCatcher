using Unity.Entities;
using UnityEngine;

/// <summary>
/// 声明每个玩家对应的资源 Ghost 预制体
/// </summary>
public class PlayerResourceGhostAuthoring : MonoBehaviour
{
    class Baker : Baker<PlayerResourceGhostAuthoring>
    {
        public override void Bake(PlayerResourceGhostAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent<PlayerResourceTag>(entity);
            AddComponent<PlayerResourceState>(entity);
        }
    }
}
