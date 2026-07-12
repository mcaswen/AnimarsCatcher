using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.CharacterController;

/// <summary>
/// 保存第三人称 KCC 角色的移动物理配置
/// </summary>
[Serializable]
public struct ThirdPersonCharacter : IComponentData
{
    // 接地移动配置
    public float RotationSharpness;
    public float GroundMaxSpeed;
    public float GroundedMovementSharpness;
    // 空中移动配置
    public float AirAcceleration;
    public float AirMaxSpeed;
    public float AirDrag;
    // KCC 接地、台阶和斜坡处理配置
    public float3 Gravity;
    public bool PreventAirAccelerationAgainstUngroundedHits;
    public BasicStepAndSlopeHandlingParameters StepAndSlopeHandling;
}

/// <summary>
/// 保存第三人称角色当前预测帧的移动向量
/// </summary>
[Serializable]
public struct ThirdPersonCharacterControl : IComponentData
{
    public float3 MoveVector;
}
