using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.HealthBars
{
    /// <summary>
    /// 配置 Entity 对应的血条 GameObject 预制体和世界偏移
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "AnimarsCatcher.Presentation.HealthUI", "AnimarsCatcher.Presentation", "HealthBarViewAuthoring")]
    public class HealthBarViewAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("healthBarPrefab")]
        [SerializeField] private GameObject _healthBarPrefab;

        [FormerlySerializedAs("worldOffset")]
        [SerializeField] private Vector3 _worldOffset = new Vector3(0f, 2f, 0f);

        private sealed class Baker : Unity.Entities.Baker<HealthBarViewAuthoring>
        {
            public override void Bake(HealthBarViewAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponentObject(entity, new HealthBarViewConfig
                {
                    HealthBarPrefab = authoring._healthBarPrefab,
                    WorldOffset = authoring._worldOffset
                });
            }
        }
    }
}
