using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在场景或预制体上配置实体的初始阵营
    /// </summary>
    [DisallowMultipleComponent]
    public class CampAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("initialCamp")]
        [SerializeField] private CampType _initialCamp = CampType.Neutral;

        private sealed class Baker : Baker<CampAuthoring>
        {
            public override void Bake(CampAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new Camp
                {
                    Value = authoring._initialCamp
                });
            }
        }
    }
}
