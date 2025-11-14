using Unity.Entities;
using Unity.Mathematics;
using UnityEditor.EditorTools;
using UnityEngine;

public class AniAttributesAuthoring : MonoBehaviour
{
    [Tooltip("最大生命值")]
    public int MaxHealth;

    [Tooltip("移动速度")]
    public float MoveSpeed;

    [Tooltip("攻击间隔")]
    public float AttackInterval;

    [Tooltip("攻击伤害")]
    public float AttackDamage;

    [Tooltip("攻击范围")]
    public float AttackRange;
}

public class AniAttributesBaker : Baker<AniAttributesAuthoring>
{
    public override void Bake(AniAttributesAuthoring authoring)
    {
        var entity = GetEntity(TransformUsageFlags.None);
        AddComponent(entity, new AniAttributes
        {
            MaxHealth = authoring.MaxHealth,
            CurrentHealth = authoring.MaxHealth,
            MoveSpeed = authoring.MoveSpeed,
            AttackInterval = authoring.AttackInterval,
            AttackDamage = authoring.AttackDamage,
            AttackRange = authoring.AttackRange,
            IsSelected = false,
        });
    }
}