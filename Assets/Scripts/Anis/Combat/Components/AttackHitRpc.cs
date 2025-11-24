using Unity.Entities;
using Unity.NetCode;

public struct AttackHitRpc : IRpcCommand
{
    public int  AttackerGhostId;
    public uint ShotId;
}
