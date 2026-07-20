using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端在近战动画命中帧发送的攻击确认消息
    /// </summary>
    public struct MeleeHitRpc : IRpcCommand
    {
        public int  AttackerGhostId;
        public uint ShotId;
    }
}
