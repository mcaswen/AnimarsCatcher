using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 配置客户端选中光圈预制体和垂直偏移
    /// </summary>
    [DisallowMultipleComponent]
    public class SelectionRingPrefabAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("RingPrefab")]
        [SerializeField] private GameObject _ringPrefab;
        [FormerlySerializedAs("YOffset")]
        [SerializeField] private float _yOffset = 0.02f;

        private sealed class Baker : Unity.Entities.Baker<SelectionRingPrefabAuthoring>
        {
            public override void Bake(SelectionRingPrefabAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var ringPrefabEntity = GetEntity(authoring._ringPrefab, TransformUsageFlags.Renderable);
                AddComponent(entity, new SelectionRingPrefabConfig
                {
                    Prefab = ringPrefabEntity,
                    YOffset = authoring._yOffset
                });
            }
        }
    }

    /// <summary>
    /// 客户端选中光圈 Entity 预制体配置
    /// </summary>
    public struct SelectionRingPrefabConfig : IComponentData
    {
        public Entity Prefab;
        public float YOffset;
    }

    /// <summary>
    /// Ani Entity 当前关联的选中光圈引用
    /// </summary>
    public struct SelectionRingReference : IComponentData
    {
        public Entity RingEntity;
    }
}
