using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Transforms;

public struct AniSpawnPointTag : IComponentData {}
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

            // 用 LocalTransform 来保存位置旋转
            AddComponent(entity, LocalTransform.FromPositionRotationScale(
                authoring.transform.position,
                authoring.transform.rotation,
                1f
            ));
        }
    }
}
