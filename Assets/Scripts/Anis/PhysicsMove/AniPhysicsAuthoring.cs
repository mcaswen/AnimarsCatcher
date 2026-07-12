using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Physics;

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
    [Tooltip("参与碰撞的类别 bitmask")]
    public uint BelongsTo   = ~0u;
    
    [Tooltip("碰撞对象类别")]
    public uint CollidesWith = ~0u;

    [Tooltip("碰撞组索引")]
    public int GroupIndex = 0;

    /// <summary>
    /// 将探测参数和初始探测结果写入实体
    /// </summary>
    class Baker : Baker<AniPhysicsAuthoring>
    {
        /// <summary>
        /// 烘焙射线长度、偏移和 Unity Physics 过滤器
        /// </summary>
        /// <param name="authoring">Ani 物理探测创作组件</param>
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
