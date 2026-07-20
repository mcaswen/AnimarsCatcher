using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Physics.Authoring
{
    /// <summary>
    /// 运行时胶囊体碰撞尺寸数据
    /// </summary>
    public struct CapsuleColliderGeometry : IComponentData
    {
        public float3 Center;  // 相对实体原点的本地偏移
        public float  Radius;
        public float  Height;
    }

    /// <summary>
    /// 从场景 CapsuleCollider 烘焙 ECS 碰撞尺寸
    /// </summary>
    [DisallowMultipleComponent]
    [MovedFrom(true, "AnimarsCatcher.Physics.Authoring", "AnimarsCatcher.Physics.Authoring", "CapsulePhysicsAuthoring")]
    public class CapsuleColliderGeometryAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("SourceCollider")]
        [SerializeField] private CapsuleCollider _sourceCollider;

        private sealed class Baker : Baker<CapsuleColliderGeometryAuthoring>
        {
            public override void Bake(CapsuleColliderGeometryAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                var capsule = authoring._sourceCollider != null
                    ? authoring._sourceCollider
                    : authoring.GetComponent<CapsuleCollider>();

                if (!capsule)
                {
                    Debug.LogWarning($"[CapsuleColliderGeometryAuthoring] {authoring.name} 上没找到 CapsuleCollider");
                    return;
                }

                // Unity 胶囊体要求高度不小于直径
                float radius = capsule.radius;
                float height = Mathf.Max(capsule.height, radius * 2f);

                AddComponent(entity, new CapsuleColliderGeometry
                {
                    Center = capsule.center,
                    Radius = radius,
                    Height = height
                });
            }
        }
    }
}
