using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 客户端在近战动画命中帧发送的攻击确认消息
/// </summary>
public struct AttackHitRpc : IRpcCommand
{
    public int  AttackerGhostId;
    public uint ShotId;
}
