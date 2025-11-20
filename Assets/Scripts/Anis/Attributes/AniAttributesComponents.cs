using Unity.Entities;
using Unity.NetCode;

[GhostComponent]
public struct AniAttributes : IComponentData
{
    [GhostField]
    public int MaxHealth;

    [GhostField]
    public float MoveSpeed;

    public float AttackInterval;

    public float AttackDamage;

    public float AttackRange;

    [GhostField]
    public int OwnerPlayerId;
}
public enum PickerAniTargetType
{
    Resource = 0,
    Enemy = 1,
}

public struct PickerAniAttributes : IComponentData
{
    public float CarrySpeed;
    public PickerAniTargetType TargetType;
}