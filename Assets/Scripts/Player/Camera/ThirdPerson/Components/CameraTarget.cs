using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

[Serializable]
public struct CameraTarget : IComponentData
{
    [GhostField]
    public Entity TargetEntity;
}
