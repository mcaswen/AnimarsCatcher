using Unity.Entities;
using Unity.NetCode;

public enum AniAttackTargetKind : byte
{
    None        = 0,
    EnemyAni    = 1,
    Resource    = 2,
    EnemyBase   = 3,
}

public struct AniPendingAttack : IComponentData
{
    public Entity Target;

    public AniAttackTargetKind Kind;

    public uint ShotId;
}

public struct AniAttackTarget : IComponentData
{
    public Entity Target;

    public AniAttackTargetKind Kind;
}

public struct AniAttackState : IComponentData
{
    public float CooldownRemaining;
}

[GhostComponent]
public struct AniAttackFireRequest : IComponentData
{
    [GhostField]
    public uint ShotId;
}
