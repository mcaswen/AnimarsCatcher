namespace AnimarsCatcher.Networking
{
    using System.Collections.Generic;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.NetCode;
    using Unity.CharacterController;

    /// <summary>
    /// 注册 KCC 组件在 Ghost 序列化中的默认变体
    /// </summary>
    public partial class DefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(KinematicCharacterBody), Rule.ForAll(typeof(KinematicCharacterBody_DefaultVariant)));
            defaultVariants.Add(typeof(CharacterInterpolation), Rule.ForAll(typeof(CharacterInterpolation_GhostVariant)));
            defaultVariants.Add(typeof(TrackedTransform), Rule.ForAll(typeof(TrackedTransform_DefaultVariant)));
        }
    }

    /// <summary>
    /// 同步所有网络角色进行预测所需的 KCC 核心状态
    /// </summary>
    [GhostComponentVariation(typeof(KinematicCharacterBody))]
    [GhostComponent()]
    public struct KinematicCharacterBody_DefaultVariant
    {
        // 相对速度和接地状态是所有网络角色进行预测的基础状态
        [GhostField()]
        public float3 RelativeVelocity;
        [GhostField()]
        public bool IsGrounded;

        // 父实体相关字段用于支持角色站在带 TrackedTransform 的移动平台上
        [GhostField()]
        public Entity ParentEntity;
        [GhostField()]
        public float3 ParentLocalAnchorPoint;
        [GhostField()]
        public float3 ParentVelocity;
    }

    /// <summary>
    /// 仅在预测客户端保留 KCC 插值状态
    /// 远端插值 Ghost 已由 NetCode 处理，服务器也不需要表现插值
    /// </summary>
    [GhostComponentVariation(typeof(CharacterInterpolation))]
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    public struct CharacterInterpolation_GhostVariant
    {
    }

    /// <summary>
    /// 同步移动平台当前固定步长姿态
    /// </summary>
    [GhostComponentVariation(typeof(TrackedTransform))]
    [GhostComponent()]
    public struct TrackedTransform_DefaultVariant
    {
        [GhostField()]
        public RigidTransform CurrentFixedRateTransform;
    }
}
