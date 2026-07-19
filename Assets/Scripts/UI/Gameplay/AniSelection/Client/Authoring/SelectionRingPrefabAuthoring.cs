using Unity.Entities;
using UnityEngine;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 配置客户端选中光圈预制体和垂直偏移
    /// </summary>
    [DisallowMultipleComponent]
    public class SelectionRingPrefabAuthoring : MonoBehaviour
    {
        public GameObject RingPrefab;
        public float YOffset = 0.02f;

        class Baker : Unity.Entities.Baker<SelectionRingPrefabAuthoring>
        {
            public override void Bake(SelectionRingPrefabAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var ringPrefabEntity = GetEntity(authoring.RingPrefab, TransformUsageFlags.Renderable);
                AddComponent(entity, new SelectionRingPrefabConfig
                {
                    Prefab = ringPrefabEntity,
                    YOffset = authoring.YOffset
                });
            }
        }
    }

    /// <summary>
    /// 客户端选中光圈实体预制体配置
    /// </summary>
    public struct SelectionRingPrefabConfig : IComponentData
    {
        public Entity Prefab;
        public float YOffset;
    }

    /// <summary>
    /// Ani 实体当前关联的选中光圈引用
    /// </summary>
    public struct SelectionRingReference : IComponentData
    {
        public Entity RingEntity;
    }
}
