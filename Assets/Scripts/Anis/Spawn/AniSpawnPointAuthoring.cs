using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 标记可供服务器按阵营查找的 Ani 出生点
    /// </summary>
    public struct AniSpawnPointTag : IComponentData {}

    /// <summary>
    /// 在场景中配置指定阵营的 Ani 出生位置和朝向
    /// </summary>
    public class AniSpawnPointAuthoring : MonoBehaviour
    {
        public CampType campType = CampType.Alpha;

        class Baker : Baker<AniSpawnPointAuthoring>
        {
            public override void Bake(AniSpawnPointAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent<AniSpawnPointTag>(entity);
                AddComponent(entity, new Camp { Value = authoring.campType });

                // 保存场景变换，服务器生成 Ghost 时直接复用
                AddComponent(entity, LocalTransform.FromPositionRotationScale(
                    authoring.transform.position,
                    authoring.transform.rotation,
                    1f
                ));
            }
        }
    }
}
