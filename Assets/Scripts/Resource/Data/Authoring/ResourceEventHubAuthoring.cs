using Unity.Entities;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 创建资源数量变化事件的共享缓冲区宿主
    /// </summary>
    public class ResourceEventHubAuthoring : MonoBehaviour
    {
        class Baker : Baker<ResourceEventHubAuthoring>
        {
            public override void Bake(ResourceEventHubAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);

                AddBuffer<FoodAmountChangedEvent>(entity);
                AddBuffer<CrystalAmountChangedEvent>(entity);
                AddComponent<ResourceEventHubTag>(entity);
            }
        }
    }

    /// <summary>
    /// 标识资源事件缓冲区宿主实体
    /// </summary>
    public struct ResourceEventHubTag : IComponentData { }
}
