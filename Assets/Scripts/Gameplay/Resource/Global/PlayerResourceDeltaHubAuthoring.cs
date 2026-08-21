using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 创建集中保存资源数量变化事件的共享缓冲区
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Gameplay", "AnimarsCatcher.Gameplay", "ResourceEventHubAuthoring")]
    public class PlayerResourceDeltaHubAuthoring : MonoBehaviour
    {
        private sealed class Baker : Baker<PlayerResourceDeltaHubAuthoring>
        {
            public override void Bake(PlayerResourceDeltaHubAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddBuffer<FoodResourceDeltaEvent>(entity);
                AddBuffer<CrystalResourceDeltaEvent>(entity);
                AddComponent<PlayerResourceDeltaHubTag>(entity);
            }
        }
    }

    /// <summary>
    /// 标识承载资源事件缓冲区的 Entity
    /// </summary>
    public struct PlayerResourceDeltaHubTag : IComponentData { }
}
