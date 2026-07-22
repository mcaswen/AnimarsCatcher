using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// NetCode 包为 <see cref="Unity.Transforms.LocalTransform"/> 组件提供的默认序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "Transform - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct TransformDefaultVariant
    {
        /// <summary>
        /// Position 默认按 1000 的量化单位复制，即每个分量约有 1 毫米精度
        /// 复制的 Position 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// Scale 默认按 1000 的量化单位复制
        /// 复制的 Scale 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;

        /// <summary>
        /// 复制 Rotation 四元数，其浮点数据采用较高精度量化，即每个分量使用 10 位或更多位
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// 仅复制实体 <see cref="Unity.Transforms.LocalTransform.Position"/> 的
    /// <see cref="Unity.Transforms.LocalTransform"/> 序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionOnly - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionOnlyVariant
    {
        /// <summary>
        /// Position 默认按 1000 的量化单位复制，即每个分量约有 1 毫米精度
        /// 复制的 Position 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;
    }
    /// <summary>
    /// 仅复制实体 <see cref="Unity.Transforms.LocalTransform.Rotation"/> 的
    /// <see cref="Unity.Transforms.LocalTransform"/> 序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "RotationOnly - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct RotationOnlyVariant
    {
        /// <summary>
        /// 复制 Rotation 四元数，其浮点数据采用较高精度量化，即每个分量使用 10 位或更多位
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// 复制实体 <see cref="Unity.Transforms.LocalTransform.Position"/> 和
    /// <see cref="Unity.Transforms.LocalTransform.Rotation"/> 属性的序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionAndRotation - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionRotationVariant
    {
        /// <summary>
        /// Position 默认按 1000 的量化单位复制，即每个分量约有 1 毫米精度
        /// 复制的 Position 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// Rotation 默认按 1000 的量化单位复制
        /// 复制的 Rotation 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// 复制实体 <see cref="Unity.Transforms.LocalTransform.Position"/> 和
    /// <see cref="Unity.Transforms.LocalTransform.Scale"/> 属性的序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionScale - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionScaleVariant
    {
        /// <summary>
        /// Position 默认按 1000 的量化单位复制，即每个分量约有 1 毫米精度
        /// 复制的 Position 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// Scale 默认按 1000 的量化单位复制，并同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }
    /// <summary>
    /// 复制实体 <see cref="Unity.Transforms.LocalTransform.Rotation"/> 和
    /// <see cref="Unity.Transforms.LocalTransform.Scale"/> 属性的序列化策略
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "RotationScale - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct RotationScaleVariant
    {
        /// <summary>
        /// Rotation 默认按 1000 的量化单位复制
        /// 复制的 Rotation 同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;

        /// <summary>
        /// Scale 默认按 1000 的量化单位复制，并同时支持插值与外推
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }

    /// <summary>
    /// 当 Transform 组件尚未配置默认 Variant 时，按需设置 NetCode 默认 Variant 的 System
    /// 包默认设置以下 Variant：
    /// - <see cref="Unity.Transforms.LocalTransform"/>
    /// - <see cref="Unity.Transforms.Translation"/>
    /// - <see cref="Unity.Transforms.Rotation"/>
    /// </summary>
    /// <remarks>
    /// <para>若 <see cref="GhostComponentSerializerCollectionData.DefaultVariants"/> 映射中已存在 Transform 组件的默认分配，本 System 不会覆盖它</para>
    /// <para>所有继承自 <see cref="DefaultVariantSystemBase"/> 的 System 优先级都更高，即使它们在本 System 之后创建</para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [UpdateInGroup(typeof(DefaultVariantSystemGroup), OrderLast = true)]
    public sealed partial class TransformDefaultVariantSystem : SystemBase
    {
        protected override void OnCreate()
        {
            var rules = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>().DefaultVariantRules;
            rules.TrySetDefaultVariant(ComponentType.ReadWrite<LocalTransform>(), DefaultVariantSystemBase.Rule.OnlyParents(typeof(TransformDefaultVariant)), this);

            Enabled = false;
        }

        protected override void OnUpdate()
        {
        }
    }
}
