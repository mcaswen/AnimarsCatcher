using Unity.Entities;
using Unity.NetCode;

[GhostComponent]
public struct GlobalGameResourceState : IComponentData
{
    [GhostField] public int MatchTimeSeconds;
}

public struct GlobalGameResourceTag : IComponentData { }

