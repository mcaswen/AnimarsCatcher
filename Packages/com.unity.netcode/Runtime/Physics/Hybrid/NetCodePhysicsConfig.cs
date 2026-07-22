using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于启用预测物理的自动 World 切换（<see cref="PredictedPhysicsNonGhostWorld"/>）和延迟补偿（<see cref="EnableLagCompensation"/>），并调整相关设置
    /// 转换时，只要启用了其中任一功能，就会向 Scene 或 SubScene 添加一个单例实体
    /// 系统会根据这些设置自动为该实体添加 <see cref="PredictedPhysicsNonGhostWorld"/> 和 <see cref="EnableLagCompensation"/> 组件
    /// </summary>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.NetCodePhysicsConfig)]
    public sealed class NetCodePhysicsConfig : MonoBehaviour
    {
        /// <summary>
        /// 配置 PhysicsSystemGroup 在 <see cref="PredictedFixedStepSimulationSystemGroup"/> 内的更新方式
        /// 默认使用 <see cref="PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts"/> 以保留原有行为
        /// 通常更合理的配置是 <see cref="PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities"/> 或 <see cref="PhysicGroupRunMode.AlwaysRun"/>
        /// </summary>
        /// <remarks>
        /// 客户端的物理系统只有在预测循环运行时才能更新
        /// 要使此配置生效，必须将 PredictedSimulationSystemGroup 配置为始终更新
        /// 即把 <see cref="ClientTickRate.PredictionLoopUpdateMode"/> 设置为 <see cref="PredictionLoopUpdateMode.AlwaysRun"/>
        /// </remarks>
        [Tooltip("Configure how the PhysicsSystemGroup should update inside the <b>PredictedFixedStepSimulationSystemGroup</b>.\nBy default, this option is set to <b>PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts</b> (preserve the original behavior).\nHowever, in general, a more correct settings would be to either use <b>PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities</b>, or <b>PhysicGroupRunMode.AlwaysRun</b>.\n\n<b>For the client, in particular, because physics can update only if the prediction loop runs, in order to have this settings be used, <color=yellow>it is necessary to configure the PredictedSimulationSystemGroup to always update (by using the ClientTickRate.PredictionLoopUpdateMode property and set that to PredictionLoopUpdateMode.AlwaysRun</color>).</b>")]
        public PhysicGroupRunMode PhysicGroupRunMode;
        /// <summary>
        /// 设为 true 后启用 LagCompensation 系统，服务端和客户端会开始在 PhysicsWorldHistory Buffer 中记录物理 World 状态
        /// 可通过 ServerHistorySize 和 ClientHistorySize 属性进一步配置 Buffer 大小
        /// </summary>
        [Tooltip("Enable/Disable the LagCompensation system. Server and Client will start recording the physics world state in the PhysicsWorldHistory buffer")]
        public bool EnableLagCompensation;
        /// <inheritdoc cref="LagCompensationConfig.ServerHistorySize"/>
        [Tooltip("The number of physics world states that are backed up on the server. This cannot be more than the maximum capacity (32), and must be a power of two.\n\nLeaving it at zero will give you the default value (16).")]
        public int ServerHistorySize;
        /// <inheritdoc cref="LagCompensationConfig.ClientHistorySize"/>
        [Tooltip("The number of physics world states that are backed up on the client. This cannot be more than the maximum capacity (32), and must be a power of two.\n\nThe default value is 1, but setting it to 0 will disable recording the physics history on the client, reducing CPU and memory consumption.")]
        public int ClientHistorySize = 1;

        /// <summary>
        /// 使用预测物理时，客户端主物理 World 中的所有动态物理对象都必须是 Ghost
        /// 设置该值后，默认物理 World 中所有非 Ghost 对象都会被移入另一个 World
        /// </summary>
        [Tooltip("The physics world index to use for all dynamic physics objects which are not ghosts.")]
        public uint ClientNonGhostWorldIndex = 0;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyDynamicColliders"/>
        [Tooltip("Denotes whether or not Netcode will deep copy dynamic colliders into the Lag Compensation CollisionWorld ring buffer used for Lag Compensation.\n\nRecommendation & Default: True.\n\nEnable this if you get exceptions when querying since-destroyed entities.")]
        public bool DeepCopyDynamicColliders = true;

        /// <inheritdoc cref="LagCompensationConfig.DeepCopyStaticColliders"/>
        [Tooltip("Denotes whether or not Netcode will deep copy static colliders into the Lag Compensation CollisionWorld ring buffer used for Lag Compensation.\n\nEnable if you need perfectly accurate lag compensation query results with static colliders, which is typically only necessary if they occasionally change.\n\nRecommendation & Default: False.\n\nInstead: Run two queries - one against static geometry - then another against the dynamic entities in the historic buffer.")]
        public bool DeepCopyStaticColliders;
    }

    class NetCodePhysicsConfigBaker : Baker<NetCodePhysicsConfig>
    {
        public override void Bake(NetCodePhysicsConfig authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            if (authoring.EnableLagCompensation)
            {
                AddComponent(entity, new LagCompensationConfig
                {
                    ServerHistorySize = authoring.ServerHistorySize,
                    ClientHistorySize = authoring.ClientHistorySize,
                    DeepCopyStaticColliders = authoring.DeepCopyStaticColliders,
                    DeepCopyDynamicColliders = authoring.DeepCopyDynamicColliders,
                });
            }
            AddComponent(entity, new PhysicsGroupConfig()
            {
                PhysicsRunMode = authoring.PhysicGroupRunMode
            });
            if (authoring.ClientNonGhostWorldIndex != 0)
                AddComponent(entity, new PredictedPhysicsNonGhostWorld{Value = authoring.ClientNonGhostWorldIndex});
        }
    }
}
