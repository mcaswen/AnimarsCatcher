using Unity.Entities;
using UnityEngine;

/// <summary>
/// 创建资源数量变化事件的共享缓冲区宿主
/// </summary>
public class ResourceEventHubAuthoring : MonoBehaviour
{
    /// <summary>
    /// 烘焙两类资源事件缓冲区和 Hub 标签
    /// </summary>
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
