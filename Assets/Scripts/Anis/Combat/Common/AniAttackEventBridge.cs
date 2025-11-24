using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 近战攻击动画事件：
/// 只负责告诉 ECS：哪个 Attacker、哪一发 ShotId“应该在此刻结算”
/// 目标信息由 ECS 里的 AniPendingAttack 决定。
/// </summary>
public struct AniAttackHitEvent
{
    public Entity Attacker;
    public uint ShotId;
}

/// <summary>
/// 近战攻击动画事件桥：
/// View 在动画事件里 Enqueue，ECS 的 System 不断 TryDequeue 消费。
/// </summary>
public static class AniAttackEventBridge
{
    private static readonly Queue<AniAttackHitEvent> Events = new Queue<AniAttackHitEvent>();
    private static readonly object LockObject = new object();

    public static void Enqueue(in AniAttackHitEvent eventData)
    {
        lock (LockObject)
        {
            Events.Enqueue(eventData);
        }
    }

    public static bool TryDequeue(out AniAttackHitEvent eventData)
    {
        lock (LockObject)
        {
            if (Events.Count > 0)
            {
                eventData = Events.Dequeue();
                return true;
            }
        }

        eventData = default;
        return false;
    }
}