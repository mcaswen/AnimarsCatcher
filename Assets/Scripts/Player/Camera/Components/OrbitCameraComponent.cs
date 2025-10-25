using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode;

[Serializable]
public struct OrbitCameraComponent : IComponentData
{
    // Rotation
    public float RotationSpeed;
    public float MaxVAngle;
    public float MinVAngle;
    public bool RotateWithCharacterParent;

    // Distance
    public float MinDistance;
    public float MaxDistance;
    public float DistanceMovementSpeed;
    public float DistanceMovementSharpness;

    // Obstruction
    public float ObstructionRadius;
    public float ObstructionInnerSmoothingSharpness;
    public float ObstructionOuterSmoothingSharpness;
    public bool PreventFixedUpdateJitter;

    // State
    public float TargetDistance;
    public float SmoothedTargetDistance;
    public float ObstructedDistance;
    public float PitchAngle;
    public float3 PlanarForward;
}

[Serializable]
public struct OrbitCameraControl : IComponentData
{
    public Entity FollowedCharacterEntity;
    public float2 LookDegreesDelta;
    public float ZoomDelta;
}

[Serializable]
public struct OrbitCameraIgnoredEntityBufferElement : IBufferElementData
{
    public Entity Entity;
}