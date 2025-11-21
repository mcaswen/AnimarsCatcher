using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

public struct AniPhysicsProbe : IComponentData
{
    public float3 GroundNormal;
    public float GroundDistance;
    public bool IsGrounded;

    public bool HasObstacleAhead;
    public float3 ObstacleNormal;
    public float ObstacleDistance;
}

public struct AniPhysicsConfig : IComponentData
{
    public float GroundRayLength;
    public float ForwardRayLength;
    public float3 ProbeOffset;
    public CollisionFilter Filter;
}