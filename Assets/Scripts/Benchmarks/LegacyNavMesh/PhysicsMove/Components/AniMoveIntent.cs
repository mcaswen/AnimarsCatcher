using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// 保存导航系统输出的世界空间期望速度
/// </summary>
[GhostComponent]
public struct AniMoveIntent : IComponentData
{
    [GhostField]
    public float3 DesiredVelocity; // 世界空间期望速度
}
