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
        // 相对 Entity 原点的本地偏移
        public float3 Center;
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

                // 配置 ColliderSource 后可以复用子对象 Collider，未配置时读取当前对象上的组件
                var capsule = authoring._sourceCollider != null
                    ? authoring._sourceCollider
                    : authoring.GetComponent<CapsuleCollider>();

                if (!capsule)
                {
                    Debug.LogWarning($"[CapsuleColliderGeometryAuthoring] {authoring.name} 上没找到 CapsuleCollider");
                    return;
                }

            // 修正无效的 Inspector 输入，确保运行时始终得到可用的胶囊体尺寸
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
