using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 描述远程视图射线产生的候选命中结果
    /// 最终伤害仍由服务器根据攻击快照和目标规则结算
    /// </summary>
    public struct AniHitResultData
    {
        public Entity Attacker;
        public Entity HitTarget;
        public float3 HitPosition;
        public float3 HitNormal;
        public int Damage;
        public AniAttackMode AttackMode;
        public uint   ShotId;
    }

    /// <summary>
    /// 在线程安全队列中桥接 Blaster 视图射线和 ECS 客户端系统
    /// </summary>
    public static class AniHitBridge
    {
        private static Queue<AniHitResultData> Hits = new Queue<AniHitResultData>();
        private static object LockObject = new object();

        /// <summary>
        /// 将一次远程射线结果加入待发送队列
        /// </summary>
        /// <param name="hitData">视图计算出的候选命中数据</param>
        public static void Enqueue(in AniHitResultData hitData)
        {
            lock (LockObject)
            {
                Hits.Enqueue(hitData);
            }
        }

        /// <summary>
        /// 尝试按进入顺序取出下一次远程命中结果
        /// </summary>
        /// <param name="hitData">成功时返回候选命中数据</param>
        /// <returns>队列中存在结果时返回真</returns>
        public static bool TryDequeue(out AniHitResultData hitData)
        {
            lock (LockObject)
            {
                if (Hits.Count > 0)
                {
                    hitData = Hits.Dequeue();
                    return true;
                }
            }

            hitData = default;
            return false;
        }
    }
}
