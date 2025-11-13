using Unity.Entities;

public struct FormationLeader : IComponentData
{
    public int columnCount;
    public float horizontalSpacing;
    public float backwardSpacing;
    public float arrivalRadius;
}

// 顺序即槽位索引
public struct FormationRoster : IBufferElementData
{
    public Entity member;
}

public struct FormationMember : IComponentData
{
    public Entity leader;
    public int slotIndex; 
}

public struct FormationJoinRequest : IComponentData
{
    public Entity leader;
}

public struct FormationLeaveRequest : IComponentData
{
    public Entity leader;
}
