using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 远程 / 激光命中结果：
/// 视图层已经算出了命中目标、命中位置、法线和伤害。
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
/// 远程命中结果桥：
/// Blaster 的 View 在 Raycast 后把命中结果塞进来，
/// ECS 统一在 ApplyHitSystem 里消费并改 Health。
/// </summary>
public static class AniHitBridge
{
    private static Queue<AniHitResultData> Hits = new Queue<AniHitResultData>();
    private static object LockObject = new object();

    public static void Enqueue(in AniHitResultData hitData)
    {
        lock (LockObject)
        {
            Hits.Enqueue(hitData);
        }
    }

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