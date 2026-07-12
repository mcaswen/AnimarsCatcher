using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 表示攻击目标采用的规则和结算类别
/// </summary>
public enum AniAttackTargetKind : byte
{
    None        = 0,
    EnemyAni    = 1,
    Resource    = 2,
    EnemyBase   = 3,
}

/// <summary>
/// 保存服务器开火时冻结的目标快照和唯一攻击序号
/// </summary>
public struct AniPendingAttack : IComponentData
{
    public Entity Target;

    public AniAttackTargetKind Kind;

    public uint ShotId;
}

/// <summary>
/// 保存感知系统为 Ani 选出的当前攻击目标
/// </summary>
public struct AniAttackTarget : IComponentData
{
    public Entity Target;

    public AniAttackTargetKind Kind;
}

/// <summary>
/// 保存距离下一次允许开火的剩余冷却时间
/// </summary>
public struct AniAttackState : IComponentData
{
    public float CooldownRemaining;
}

/// <summary>
/// 通过 ShotId 通知视图触发一次新的攻击动画
/// </summary>
[GhostComponent]
public struct AniAttackFireRequest : IComponentData
{
    [GhostField]
    public uint ShotId;
}
