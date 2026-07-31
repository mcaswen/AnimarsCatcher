using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 区分 Ani 预制体需要附加的专属能力标签
    /// </summary>
    public enum AniType
    {
        Picker,
        Blaster,
    }

    /// <summary>
    /// 配置 Ani 的移动和攻击基础参数
    /// </summary>
    public class AniAttributesAuthoring : MonoBehaviour
    {
        [FormerlySerializedAs("AniType")]
        [SerializeField] private AniType _aniType;

        [Tooltip("单位米/秒，需大于等于 0")]
        [FormerlySerializedAs("MoveSpeed")]
        [FormerlySerializedAs("MovementSpeed")]
        [SerializeField] private float _movementSpeed;

        [Tooltip("单位秒，需大于等于 0")]
        [FormerlySerializedAs("AttackInterval")]
        [SerializeField] private float _attackInterval;

        [Tooltip("需大于等于 0")]
        [FormerlySerializedAs("AttackDamage")]
        [SerializeField] private int _attackDamage;

        [Tooltip("单位米，同时影响索敌和远程站位")]
        [FormerlySerializedAs("AttackRange")]
        [SerializeField] private float _attackRange;

        [FormerlySerializedAs("AttackMode")]
        [SerializeField] private AniAttackMode _attackMode;

        private sealed class Baker : Unity.Entities.Baker<AniAttributesAuthoring>
        {
            public override void Bake(AniAttributesAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new AniAttributes
                {
                    MovementSpeed = authoring._movementSpeed,
                    AttackInterval = authoring._attackInterval,
                    AttackDamage = authoring._attackDamage,
                    AttackRange = authoring._attackRange,
                    AttackMode = authoring._attackMode,
                });

            // 可启用标签预先烘焙后只切换状态，避免运行时增删结构组件
                AddComponent<AniSelectedTag>(entity);
                SetComponentEnabled<AniSelectedTag>(entity, false);

                AddComponent<AniCommandLockedTag>(entity);
                SetComponentEnabled<AniCommandLockedTag>(entity, false);

                // 类型标签驱动不同攻击表现和移动站位策略
                if (authoring._aniType == AniType.Picker)
                {
                    AddComponent<PickerAniTag>(entity);
                }
                else if (authoring._aniType == AniType.Blaster)
                {
                    AddComponent<BlasterAniTag>(entity);
                }

                AddComponent<MeleeAttackableTag>(entity);
                AddComponent<RangedAttackableTag>(entity);
            }
        }
    }
}
