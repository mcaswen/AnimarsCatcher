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
        public AniType AniType;

        [Tooltip("单位米/秒，需大于等于 0")]
        [FormerlySerializedAs("MoveSpeed")]
        public float MovementSpeed;

        [Tooltip("单位秒，需大于等于 0")]
        public float AttackInterval;

        [Tooltip("需大于等于 0")]
        public int AttackDamage;

        [Tooltip("单位米，同时影响索敌和远程站位")]
        public float AttackRange;

        public AniAttackMode AttackMode;
    }

    /// <summary>
    /// 将 Ani 配置转换为运行时组件和可启用标签
    /// </summary>
    public class AniAttributesBaker : Baker<AniAttributesAuthoring>
    {
        /// <summary>
        /// 烘焙通用属性、类型标签和可攻击能力
        /// </summary>
        /// <param name="authoring">Ani 属性创作组件</param>
        public override void Bake(AniAttributesAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new AniAttributes
            {
                MovementSpeed = authoring.MovementSpeed,
                AttackInterval = authoring.AttackInterval,
                AttackDamage = authoring.AttackDamage,
                AttackRange = authoring.AttackRange,
                AttackMode = authoring.AttackMode,
            });

            // 可启用标签预先烘焙后只切换状态，避免运行时增删结构组件
            AddComponent<AniSelectedTag>(entity);
            SetComponentEnabled<AniSelectedTag>(entity, false);

            AddComponent<AniCommandLockedTag>(entity);
            SetComponentEnabled<AniCommandLockedTag>(entity, false);

            // 类型标签驱动不同攻击表现和移动站位策略
            if (authoring.AniType == AniType.Picker)
            {
                AddComponent<PickerAniTag>(entity);
            }
            else if (authoring.AniType == AniType.Blaster)
            {
                AddComponent<BlasterAniTag>(entity);
            }

            AddComponent<MeleeAttackableTag>(entity);
            AddComponent<RangedAttackableTag>(entity);
        }
    }
}
