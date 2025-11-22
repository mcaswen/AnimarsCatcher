using Unity.Entities;
using Unity.NetCode;

[GhostComponent]
public struct AniAttributes : IComponentData
{
    [GhostField]
    public float MoveSpeed;

    public float AttackInterval;

    public int AttackDamage;

    public float AttackRange;
    public AniAttackMode AttackMode;

    [GhostField]
    public int OwnerPlayerId;
}

public enum AniAttackMode : byte
{
    Melee  = 0,
    Ranged = 1,
}