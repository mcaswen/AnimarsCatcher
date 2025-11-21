using Unity.Entities;
using Unity.Mathematics;

public enum MovementTargetKind : byte
{
    None = 0,
    Ground = 1,
    Player = 2,   
    Ani = 3,
    Resource = 4,   // 点到资源
}

public struct MovementClickRequest : IComponentData
{
    public int Version;        
    public float2 ScreenPosition;
}

public struct MovementClickResult : IComponentData
{
    public int Version;
    public MovementTargetKind TargetKind;
    public Entity TargetEntity;
    public float3 TargetWorldPosition;
}

// 用来防止对同一 Result 重复下命令
public struct MovementClickProcessedVersion : IComponentData
{
    public int Version;
}
