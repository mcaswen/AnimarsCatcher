using Unity.Entities;
using Unity.Mathematics;
using System;
using Unity.NetCode;

[Serializable]
[GhostComponent]
public struct FixedCamera : IComponentData
{
    [GhostField]
    public float Distance;     // 相机距离

    [GhostField]
    public float PitchDeg;     // 俯仰角

    [GhostField]
    public float YawDeg;       // 偏航角

    [GhostField]
    public float Height;       // 相机本体额外抬高

    [GhostField]
    public float Damping;      // 位置阻尼

    [GhostField]
    public float LookUpBias;   // 观察点额外抬高

    // 网络snap
    [GhostField]
    public float SnapDistance;   // 位置 snap 距离阈值
    
    [GhostField]
    public float SnapAngleDeg;   // 旋转 snap 角度阈值

}

[Serializable]
public struct FixedCameraControl : IComponentData
{
    [GhostField]
    public Entity FollowedCharacterEntity;
}

[Serializable]
public struct FixedCameraSmoothState : IComponentData
{
    [GhostField]
    public float3 Velocity;
}