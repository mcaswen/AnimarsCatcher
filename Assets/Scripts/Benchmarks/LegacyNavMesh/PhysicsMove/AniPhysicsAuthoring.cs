using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Physics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 配置 Ani 的地面探测、前向障碍探测和碰撞过滤规则
    /// </summary>
    [DisallowMultipleComponent]
    public class AniPhysicsAuthoring : MonoBehaviour
    {
        [Header("Raycast 设置")]
        public float GroundRayLength  = 2.0f;               // 地面检测长度
        public float ForwardRayLength = 1.5f;               // 前向障碍检测长度
        public Vector3 ProbeOffset = new Vector3(0, 0.5f, 0); // 从角色 pivot 往上多少作为起点

        [Header("碰撞过滤")]
        [Tooltip("探测查询所属类别的位掩码")]
        public uint BelongsTo   = ~0u;

        [Tooltip("探测查询可命中的类别位掩码")]
        public uint CollidesWith = ~0u;

        [Tooltip("有效范围 -32768 至 32767；同组正值强制命中，负值禁止命中")]
        public int GroupIndex = 0;

        class Baker : Baker<AniPhysicsAuthoring>
        {
            public override void Bake(AniPhysicsAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new AniPhysicsProbe
                {
                    GroundNormal     = math.up(),
                    GroundDistance   = 0f,
                    IsGrounded       = false,
                    HasObstacleAhead = false,
                    ObstacleNormal   = float3.zero,
                    ObstacleDistance = 0f
                });

                var filter = new CollisionFilter
                {
                    BelongsTo    = authoring.BelongsTo,
                    CollidesWith = authoring.CollidesWith,
                    GroupIndex   = (short)authoring.GroupIndex
                };

                AddComponent(entity, new AniPhysicsConfig
                {
                    GroundRayLength  = authoring.GroundRayLength,
                    ForwardRayLength = authoring.ForwardRayLength,
                    ProbeOffset      = authoring.ProbeOffset,
                    Filter           = filter
                });
            }
        }
    }
}
