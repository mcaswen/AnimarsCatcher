using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

public struct RangedHitRpc : IRpcCommand
{
    public int AttackerGhostId;
    public int TargetGhostId;   // -1 表示没打到实体，只打了地
    public float3 HitPosition;
    public float3 HitNormal;
    public uint   ShotId;
}
