using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 描述近战动画事件确认的攻击者和攻击序号
    /// 目标仍由服务器保存的 AniPendingAttack 决定
    /// </summary>
    public struct AniAttackHitEvent
    {
        public Entity Attacker;
        public uint ShotId;
    }

    /// <summary>
    /// 通过线程安全队列把 MonoBehaviour 动画事件传给 ECS 客户端系统
    /// </summary>
    public static class AniAttackEventBridge
    {
        private static readonly Queue<AniAttackHitEvent> _events = new Queue<AniAttackHitEvent>();
        private static readonly object _lockObject = new object();

        /// <summary>
        /// 将一次近战动画命中时机加入待发送队列
        /// </summary>
        /// <param name="eventData">攻击者和攻击序号</param>
        public static void Enqueue(in AniAttackHitEvent eventData)
        {
            lock (_lockObject)
            {
                _events.Enqueue(eventData);
            }
        }

        /// <summary>
        /// 尝试按进入顺序取出下一次近战动画命中事件
        /// </summary>
        /// <param name="eventData">成功时返回待发送事件</param>
        /// <returns>队列中存在事件时返回真</returns>
        public static bool TryDequeue(out AniAttackHitEvent eventData)
        {
            lock (_lockObject)
            {
                if (_events.Count > 0)
                {
                    eventData = _events.Dequeue();
                    return true;
                }
            }

            eventData = default;
            return false;
        }
    }
}
