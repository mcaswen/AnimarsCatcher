using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.GraphicsIntegration;

namespace Unity.NetCode
{
    /// <summary>
    /// PhysicsVelocity 的默认序列化 Variant，用于同步 Physics
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsVelocity), nameof(PhysicsVelocity))]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
    public struct PhysicsVelocityDefaultVariant
    {
        /// <summary>
        /// 刚体在世界空间中的线速度，单位为 m/s
        /// </summary>
        [GhostField(Quantization = 1000)] public float3 Linear;
        /// <summary>
        /// 刚体在世界空间中的角速度，单位为弧度/秒
        /// </summary>
        [GhostField(Quantization = 1000)] public float3 Angular;
    }


    /// <summary>
    /// PhysicsGraphicalSmoothing 的默认序列化 Variant，用于在插值客户端禁用平滑
    /// 插值客户端上的 Ghost 由服务器而不是 Physics 控制，因此 Physics 平滑会产生错误结果
    /// </summary>
    [GhostComponentVariation(typeof(PhysicsGraphicalSmoothing), nameof(PhysicsGraphicalSmoothing))]
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct PhysicsGraphicalSmoothingDefaultVariant
    {
    }

    /// <summary>
    /// 为 <see cref="Unity.Physics.PhysicsVelocity"/> 和
    /// <see cref="Unity.Physics.GraphicsIntegration.PhysicsGraphicalSmoothing"/> 注册可选的默认 Variant
    /// </summary>
    /// <remarks>
    /// <para>如果 `PhysicsVelocity` 或 `PhysicsGraphicalSmoothing` 组件已存在于
    /// <see cref="GhostComponentSerializerCollectionData.DefaultVariants"/> 映射中，本系统绝不会覆盖其默认分配</para>
    /// <para>任何派生自 <see cref="DefaultVariantSystemBase"/> 的系统都具有更高优先级，
    /// 即使它们在本系统之后创建也是如此</para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [UpdateInGroup(typeof(DefaultVariantSystemGroup), OrderLast = true)]
    public sealed partial class PhysicsDefaultVariantSystem : SystemBase
    {
        protected override void OnCreate()
        {
            var rules = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>().DefaultVariantRules;
            rules.TrySetDefaultVariant(ComponentType.ReadWrite<PhysicsVelocity>(), DefaultVariantSystemBase.Rule.OnlyParents(typeof(PhysicsVelocityDefaultVariant)), this);
            rules.TrySetDefaultVariant(ComponentType.ReadWrite<PhysicsGraphicalSmoothing>(), DefaultVariantSystemBase.Rule.OnlyParents(typeof(PhysicsGraphicalSmoothingDefaultVariant)), this);
            Enabled = false;
        }

        protected override void OnUpdate()
        {
        }
    }
}
