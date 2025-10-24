using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode;

[GhostComponent]
public struct OrbitCameraComponent : IComponentData
{
    // Rotation
    [GhostField]
    public float RotationSpeed;
    [GhostField]
    public float MaxVAngle;
    [GhostField]
    public float MinVAngle;
    [GhostField]
    public bool RotateWithCharacterParent;

    // Distance
    [GhostField]
    public float MinDistance;
    [GhostField]
    public float MaxDistance;
    [GhostField]
    public float DistanceMovementSpeed;
    [GhostField]
    public float DistanceMovementSharpness;

    // Obstruction
    [GhostField]
    public float ObstructionRadius;
    [GhostField]
    public float ObstructionInnerSmoothingSharpness;
    [GhostField]
    public float ObstructionOuterSmoothingSharpness;
    [GhostField]
    public bool PreventFixedUpdateJitter;

    // State
    [GhostField]
    public float TargetDistance;
    [GhostField]
    public float SmoothedTargetDistance;
    [GhostField]
    public float ObstructedDistance;
    [GhostField]
    public float PitchAngle;
    [GhostField]
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