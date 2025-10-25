using Unity.Entities;
using Unity.Mathematics;
using System;

[Serializable]
public struct FixedCamera : IComponentData
{
    public float Distance;     // 相机距离

    public float PitchDeg;     // 俯仰角

    public float YawDeg;       // 偏航角

    public float Height;       // 相机本体额外抬高

    public float Damping;      // 位置阻尼

    public float LookUpBias;   // 观察点额外抬高
}

[Serializable]
public struct FixedCameraControl : IComponentData
{
    public Entity FollowedCharacterEntity;
}

[Serializable]
public struct FixedCameraSmoothState : IComponentData
{
    public float3 Velocity;
}