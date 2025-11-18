using Unity.Entities;
using Unity.Mathematics;

public struct AniMoveIntent : IComponentData
{
    public float3 DesiredVelocity; // 世界空间期望速度
}
