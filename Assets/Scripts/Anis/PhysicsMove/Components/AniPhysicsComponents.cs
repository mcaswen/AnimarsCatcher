using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

/// <summary>
/// 保存地面和前向障碍射线的最新采样结果
/// </summary>
public struct AniPhysicsProbe : IComponentData
{
    public float3 GroundNormal;
    public float GroundDistance;
    public bool IsGrounded;

    public bool HasObstacleAhead;
    public float3 ObstacleNormal;
    public float ObstacleDistance;
}

/// <summary>
/// 保存 Ani 物理探测长度、起点偏移和碰撞过滤器
/// </summary>
public struct AniPhysicsConfig : IComponentData
{
    public float GroundRayLength;
    public float ForwardRayLength;
    public float3 ProbeOffset;
    public CollisionFilter Filter;
}
