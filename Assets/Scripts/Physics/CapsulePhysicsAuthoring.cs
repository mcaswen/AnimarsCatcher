using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public struct CapsulePhysicsInfo : IComponentData
{
    public float3 Center;  // 本地偏移
    public float  Radius;
    public float  Height;
}

[DisallowMultipleComponent]
public class CapsulePhysicsAuthoring : MonoBehaviour
{
    public CapsuleCollider SourceCollider;

    class Baker : Baker<CapsulePhysicsAuthoring>
    {
        public override void Bake(CapsulePhysicsAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            var capsule = authoring.SourceCollider != null
                ? authoring.SourceCollider
                : authoring.GetComponent<CapsuleCollider>();

            if (!capsule)
            {
                Debug.LogWarning($"[CharacterCapsuleAuthoring] {authoring.name} 上没找到 CapsuleCollider");
                return;
            }

            float radius = capsule.radius;
            float height = Mathf.Max(capsule.height, radius * 2f);

            AddComponent(entity, new CapsulePhysicsInfo
            {
                Center = capsule.center,
                Radius = radius,
                Height = height
            });
        }
    }
}
