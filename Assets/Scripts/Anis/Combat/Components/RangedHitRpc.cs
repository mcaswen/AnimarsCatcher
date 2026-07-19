using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端上报远程攻击动画射线产生的候选命中
    /// </summary>
    public struct RangedHitRpc : IRpcCommand
    {
        public int AttackerGhostId;
        public int TargetGhostId;   // 负值表示没有命中网络实体
        public float3 HitPosition;
        public float3 HitNormal;
        public uint   ShotId;
    }
}
