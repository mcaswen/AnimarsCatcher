using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[GhostComponent]
public struct ThirdPersonPlayerControl : IComponentData
{
    [GhostField]
    public Entity ControlledCharacter;
    
    [GhostField]
    public Entity ControlledCamera;
}
