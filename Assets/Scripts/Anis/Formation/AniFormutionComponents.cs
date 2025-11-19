using Unity.Entities;

public struct AniFormationLeader : IComponentData
{
    public int columnCount;
    public float horizontalSpacing;
    public float backwardSpacing;
    public float arrivalRadius;
}

// 顺序即槽位索引
public struct AniFormationRoster : IBufferElementData
{
    public Entity member;
}

public struct AniFormationMember : IComponentData
{
    public Entity leader;
    public int slotIndex; 
}

public struct AniFormationJoinRequest : IComponentData
{
    public Entity leader;
}

public struct AniFormationLeaveRequest : IComponentData
{
    public Entity leader;
}
