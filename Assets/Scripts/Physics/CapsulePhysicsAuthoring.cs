using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Physics.Authoring
{
    /// <summary>
    /// 运行时胶囊体碰撞尺寸数据
    /// </summary>
    public struct CapsulePhysicsInfo : IComponentData
    {
        public float3 Center;  // 相对实体原点的本地偏移
        public float  Radius;
        public float  Height;
    }

    /// <summary>
    /// 从场景 CapsuleCollider 烘焙 ECS 碰撞尺寸
    /// </summary>
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

                // Unity 胶囊体要求高度不小于直径
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
}
