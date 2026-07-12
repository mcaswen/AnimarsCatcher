using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode;

/// <summary>
/// 保存环绕相机配置以及跨帧运行状态
/// </summary>
[Serializable]
public struct OrbitCamera : IComponentData
{
    // 旋转配置
    public float RotationSpeed;
    public float MaxVerticalAngle;
    public float MinVerticalAngle;
    public bool RotateWithCharacterParent;

    // 距离与缩放配置
    public float MinDistance;
    public float MaxDistance;
    public float DistanceMovementSpeed;
    public float DistanceMovementSharpness;

    // 遮挡检测与平滑配置
    public float ObstructionRadius;
    public float ObstructionInnerSmoothingSharpness;
    public float ObstructionOuterSmoothingSharpness;
    public bool PreventFixedUpdateJitter;

    // 跨帧状态
    public float TargetDistance;
    public float SmoothedTargetDistance;
    public float ObstructedDistance;
    public float PitchAngle;
    public float3 PlanarForward;
}

/// <summary>
/// 保存玩家本帧对环绕相机的控制输入
/// </summary>
[Serializable]
public struct OrbitCameraControl : IComponentData
{
    public Entity FollowedCharacterEntity;
    public float2 LookDegreesDelta;
    public float ZoomDelta;
}

/// <summary>
/// 记录遮挡检测需要忽略的实体
/// </summary>
[Serializable]
public struct OrbitCameraIgnoredEntityBufferElement : IBufferElementData
{
    public Entity Entity;
}
