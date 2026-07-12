using Unity.Entities;
using UnityEngine;

/// <summary>
/// 声明每个玩家对应的资源 Ghost 预制体
/// </summary>
public class PlayerResourceGhostAuthoring : MonoBehaviour
{
    /// <summary>
    /// 为资源 Ghost 添加状态和识别标签
    /// </summary>
    class Baker : Baker<PlayerResourceGhostAuthoring>
    {
        /// <summary>
        /// 烘焙玩家资源组件
        /// </summary>
        public override void Bake(PlayerResourceGhostAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent<PlayerResourceTag>(entity);
            AddComponent<PlayerResourceState>(entity);
        }
    }
}
