using System;
using System.Runtime.CompilerServices;
using Unity.Entities;
using UnityEngine.Serialization;

[assembly: InternalsVisibleTo("Unity.NetCode.Physics.Hybrid")]

namespace Unity.NetCode
{
    /// <summary>
    /// 配置 <see cref="PredictedFixedStepSimulationSystemGroup"/> 内部的
    /// <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> 应如何以及何时运行
    /// </summary>
    public enum PhysicGroupRunMode
    {
        /// <summary>
        /// 服务器和客户端的默认选项
        /// <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> 需要存在同时带有 <see cref="PredictedGhost"/>
        /// 和 <see cref="Unity.Physics.PhysicsVelocity"/> 组件的 Entity 才会运行
        /// 在服务器上，如果没有 Entity 匹配该查询但延迟补偿已启用，Physics Group 仍会运行
        /// </summary>
        /// <remarks>
        /// 注意，在客户端使用此默认设置时，如果不存在 Predicted Ghost，预测循环不会运行，Physics 模拟的任何部分也不会运行
        /// 如需改变此行为，请将 <see cref="ClientTickRate.PredictionLoopUpdateMode"/> 设为 <see cref="PredictionLoopUpdateMode.AlwaysRun"/>
        /// <br/>
        /// 如果没有匹配的 Entity 但启用了延迟补偿，Physics 循环仅在 <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> 为 true 时运行
        /// <br/>
        /// 如果所有 Predicted Ghost Entity 都已销毁且未启用延迟补偿，Collision World 信息将逐渐陈旧
        /// 它仍包含最近一次计算的 Broadphase Tree，但系统不再计算新的 Broadphase Tree
        /// 因此旧 Broadphase Tree 中存储的 Entity 引用以及关联 Collider Blob 的引用都可能已经失效
        /// </remarks>
        LagCompensationEnabledOrKinematicGhosts,
        /// <summary>
        /// 适用于服务器和客户端的更宽松选项
        /// <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> 需要存在带有 <see cref="Unity.Physics.PhysicsVelocity"/>
        /// 或 <see cref="Unity.Physics.Collider"/> 组件的 Entity 才会运行
        /// 如果没有 Entity 匹配该查询但延迟补偿已启用，Physics Group 仍会更新
        /// </summary>
        /// <remarks>
        /// 注意，在客户端使用此设置时，如果不存在 Physics Ghost，预测循环不会运行，Physics 模拟的任何部分也不会运行
        /// 如需改变此行为，请将 <see cref="ClientTickRate.PredictionLoopUpdateMode"/> 设为 <see cref="PredictionLoopUpdateMode.AlwaysRun"/>
        /// <br/>
        /// 如果没有匹配的 Entity 但启用了延迟补偿，Physics 循环仅在 <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> 为 true 时运行
        /// <br/>
        /// 如果所有 Physics Entity 都已销毁且未启用延迟补偿，Collision World 信息将逐渐陈旧
        /// 它仍包含最近一次计算的 Broadphase Tree，但系统不再计算新的 Broadphase Tree
        /// 因此旧 Broadphase Tree 中存储的 Entity 引用以及关联 Collider Blob 的引用都可能已经失效
        /// </remarks>
        LagCompensationEnabledOrAnyPhysicsEntities,
        /// <summary>
        /// 即使不存在 Physics Entity、Predicted Ghost Entity 且未启用延迟补偿，也允许 Physics Group 运行
        /// 如果不存在 Physics Entity，Physics 循环仅在 <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> 为 true 时运行
        /// </summary>
        AlwaysRun,
    }
    /// <summary>
    /// 用于配置 <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/> 是否在预测循环中运行的 Singleton Component
    /// </summary>
    internal struct PhysicsGroupConfig : IComponentData
    {
        /// <summary>
        /// 表示即使 World 中不存在 Predicted Ghost，Physics Group 是否仍应运行
        /// 默认设置为 <see cref="PhysicGroupRunMode.RequirePredictedGhostsOrLagCompensation"/>
        /// </summary>
        public PhysicGroupRunMode PhysicsRunMode;
    }
}
