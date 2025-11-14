using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

[GhostComponent]
public struct SimpleCharacter : IComponentData
{
    public float MoveSpeed;

    public float RotationSharpness;

    public float ColliderHeight;

    public float ColliderRadius;
}

[GhostComponent]
public struct SimpleCharacterControl : IComponentData
{

    public float3 MoveVector; 
}