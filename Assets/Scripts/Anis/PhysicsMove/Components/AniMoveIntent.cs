using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[GhostComponent]
public struct AniMoveIntent : IComponentData
{
    [GhostField]
    public float3 DesiredVelocity; // 世界空间期望速度
}
