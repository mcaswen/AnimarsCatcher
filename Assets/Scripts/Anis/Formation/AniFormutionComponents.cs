using Unity.Entities;
using Unity.NetCode;

[GhostComponent]
public struct AniFormationMember : IComponentData
{
    [GhostField]
    public Entity leader;

    [GhostField]
    public int slotIndex; 
}

[GhostComponent]
public struct AniFormationJoinRequest : IComponentData
{
    [GhostField]
    public Entity leader;
}

[GhostComponent]
public struct AniFormationLeaveRequest : IComponentData
{
    [GhostField]
    public Entity leader;
}
