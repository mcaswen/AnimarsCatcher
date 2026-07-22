using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode
{
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst=true)]
    [BurstCompile]
    internal partial struct GhostPredictionDisableSimulateSystem : ISystem
    {
        ComponentTypeHandle<Simulate> m_SimulateHandle;
        ComponentTypeHandle<PredictedGhost> m_PredictedHandle;
        ComponentTypeHandle<GhostChildEntity> m_GhostChildEntityHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        EntityQuery m_PredictedQuery;
        EntityQuery m_NetworkTimeSingleton;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_SimulateHandle = state.GetComponentTypeHandle<Simulate>();
            m_PredictedHandle = state.GetComponentTypeHandle<PredictedGhost>(true);
            m_GhostChildEntityHandle = state.GetComponentTypeHandle<GhostChildEntity>(true);
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<Simulate>()
                .WithAll<GhostInstance, PredictedGhost>()
#pragma warning disable NETC0001
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
#pragma warning restore NETC0001
            m_PredictedQuery = state.GetEntityQuery(builder);
            m_NetworkTimeSingleton = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        [BurstCompile]
        struct TogglePredictedJob : IJobChunk
        {
            public ComponentTypeHandle<Simulate> simulateHandle;
            [ReadOnly] public ComponentTypeHandle<PredictedGhost> predictedHandle;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntity> ghostChildEntityHandle;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupHandle;
            public EntityStorageInfoLookup storageInfoFromEntity;
            public NetworkTick tick;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);

                var predicted = chunk.GetNativeArray(ref predictedHandle);
                var enabledMask = chunk.GetEnabledMask(ref simulateHandle);
                if (chunk.Has(ref linkedEntityGroupHandle))
                {
                    var linkedEntityGroupArray = chunk.GetBufferAccessor(ref linkedEntityGroupHandle);

                    for(int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; i++)
                    {
                        var shouldPredict = predicted[i].ShouldPredict(tick);
                        var isPredicting = enabledMask.GetBit(i);
                        enabledMask[i] = shouldPredict;
                        if (isPredicting != shouldPredict)
                        {
                            var linkedEntityGroup = linkedEntityGroupArray[i];
                            for (int child = 1; child < linkedEntityGroup.Length; ++child)
                            {
                                var storageInfo = storageInfoFromEntity[linkedEntityGroup[child].Value];
                                if (storageInfo.Chunk.Has(ref ghostChildEntityHandle) && storageInfo.Chunk.Has(ref simulateHandle))
                                    storageInfo.Chunk.SetComponentEnabled(ref simulateHandle, storageInfo.IndexInChunk, shouldPredict);
                            }
                        }
                    }
                }
                else
                {
                    for(int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; i++)
                        enabledMask[i] = predicted[i].ShouldPredict(tick);
                }
            }
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = m_NetworkTimeSingleton.GetSingleton<NetworkTime>();
            var tick = networkTime.ServerTick;
            m_SimulateHandle.Update(ref state);
            m_PredictedHandle.Update(ref state);
            m_GhostChildEntityHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);
            var predictedJob = new TogglePredictedJob
            {
                simulateHandle = m_SimulateHandle,
                predictedHandle = m_PredictedHandle,
                ghostChildEntityHandle = m_GhostChildEntityHandle,
                linkedEntityGroupHandle = m_LinkedEntityGroupHandle,
                storageInfoFromEntity = state.GetEntityStorageInfoLookup(),
                tick = tick
            };
            state.Dependency = predictedJob.ScheduleParallel(m_PredictedQuery, state.Dependency);
        }
    }

    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast=true)]
    [BurstCompile]
    internal partial struct GhostPredictionEnableSimulateSystem : ISystem
    {
        ComponentTypeHandle<Simulate> m_SimulateHandle;
        private EntityQuery m_GhostQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_SimulateHandle = state.GetComponentTypeHandle<Simulate>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithDisabled<Simulate>()
                .WithAny<GhostInstance, GhostChildEntity>();
            m_GhostQuery = state.GetEntityQuery(builder);
        }
        [BurstCompile]
        struct EnableAllPredictedGhostSimulate : IJobChunk
        {
            public ComponentTypeHandle<Simulate> simulateHandle;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var enabledMask = chunk.GetEnabledMask(ref simulateHandle);
                for(int i=0;i<chunk.Count;++i)
                    enabledMask[i] = true;
            }
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var netTime = SystemAPI.GetSingleton<NetworkTime>();
            if (netTime.IsFinalPredictionTick)
            {
                m_SimulateHandle.Update(ref state);
                state.Dependency = new EnableAllPredictedGhostSimulate()
                {
                    simulateHandle = m_SimulateHandle,
                }.ScheduleParallel(m_GhostQuery, state.Dependency);
            }
        }
    }


    /// <summary>
    /// <para>所有修改 Predicted Ghost 且大致具有确定性的 Gameplay System 的父 Group
    /// 此 System Group 会在客户端和服务器 World 中按照固定时间步长运行，
    /// 时间步长由 <see cref="ClientServerTickRate.SimulationTickRate"/> 指定
    /// 关于此 Group 与 PredictedFixedStepSimulationSystemGroup 的差异，
    /// 请参阅 <see cref="PredictedFixedStepSimulationSystemGroup"/> 文档</para>
    /// <para>在服务器上，此 Group 与 <see cref="SimulationSystemGroup"/> 同步运行，因此每个 Tick 只更新一次
    /// 换言之，SimulationSystemGroup 以固定时间步长运行且每帧只运行一次，此系统也继承这些特性
    /// 在客户端上，此 Group 通过让客户端模拟领先服务器来实现客户端预测逻辑</para>
    /// <para><b>重要：由于客户端会预测服务器未来的状态，每当客户端收到新 Snapshot 时，
    /// 此 Group 中的所有系统都可能在一个模拟帧内更新多次，相关频率参见
    /// <see cref="ClientServerTickRate.NetworkTickRate"/> 和 <see cref="ClientServerTickRate.SimulationTickRate"/>
    /// 此过程称为回滚与重模拟</b></para>
    /// <para>Ping 越高，预测 Group 的重模拟 Tick 也越频繁
    /// 例如在其他条件相近时，Ping 为 200ms 的客户端可能需要重模拟的帧数约为 100ms 连接的两倍
    /// 预测并重模拟的帧数很容易达到两位数，因此此 Group 中的系统必须非常高效，且可能消耗大量 CPU
    /// <i>可以使用预测 Group 批处理缓解该问题，参见 <see cref="ClientTickRate.MaxPredictionStepBatchSizeRepeatedTick"/></i></para>
    /// <para>此 Group 包含所有预测模拟，即客户端与服务器执行相同逻辑的模拟
    /// 在服务器上，全部预测逻辑都作为权威游戏状态处理，并且只模拟一次</para>
    /// <para>注意：此 SystemGroup 会有意添加到非 NetCode World 中，以支持单机测试</para>
    /// </summary>
    /// <remarks>由于此 Group 中的子系统更新非常频繁，在客户端上每帧可能运行多次，
    /// 在服务器上则会处理所有 Predicted Ghost，因此它通常是两端构建中开销最大的 Group
    /// 需要特别关注在此 Group 中运行的系统以控制性能
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst=true)]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public partial class PredictedSimulationSystemGroup : ComponentSystemGroup
    {}

    /// <summary>
    /// <para>Ghost 预测内部的固定更新 Group，相当于预测场景中的 <see cref="FixedStepSimulationSystemGroup"/>
    /// 该固定更新 Group 可以采用比其他预测逻辑更高的更新频率，并且不会执行部分 Tick</para>
    /// <para>注意：此 SystemGroup 会有意添加到非 NetCode World 中，以支持单机测试</para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class PredictedFixedStepSimulationSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// 返回控制此 Group 更新逻辑的 NetcodePredictionFixedRateManager 实例
        /// </summary>
        internal NetcodePredictionFixedRateManager InternalRateManager => m_InternalRateManager;
        /// <summary>
        /// 设置此 Group 使用的时间步长，单位为秒，默认值为 1/60 秒
        /// </summary>
        public float Timestep
        {
            get
            {
                return m_InternalRateManager.Timestep;
            }
            [Obsolete("The PredictedFixedStepSimulationSystemGroup.TimeStep setter has been deprecated and will be removed (RemovedAfter Entities 1.0)." +
                "Please use the ClientServerTickRate.PredictedFixedStepSimulationTickRatio to set the desired rate for this group. " +
                "Any TimeStep value set using the RateManager directly will be overwritten with the setting provided in the ClientServerTickRate", false)]
            set
            {
                m_InternalRateManager.Timestep = value;
            }
        }
        /// <summary>
        /// 将当前时间步长设置为此 Group 相对模拟或预测循环的运行频率比例
        /// 默认值为 1，即此 Group 与 <see cref="PredictedSimulationSystemGroup"/> 采用相同固定频率运行
        /// </summary>
        /// <param name="tickRate">模拟使用的 ClientServerTickRate</param>
        internal void ConfigureTimeStep(in ClientServerTickRate tickRate)
        {
            if(m_InternalRateManager == null)
                return;
            tickRate.Validate();
            var fixedTimeStep = tickRate.PredictedFixedStepSimulationTimeStep;
#if UNITY_EDITOR || NETCODE_DEBUG
            if (m_InternalRateManager.DeprecatedTimeStep != 0f)
            {
                var timestep = m_InternalRateManager.Timestep;
                if (math.distance(timestep, fixedTimeStep) > 1e-4f)
                {
                    UnityEngine.Debug.LogWarning($"The PredictedFixedStepSimulationSystemGroup.TimeStep is {timestep}ms ({math.ceil(1f/timestep)}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {fixedTimeStep}ms ({math.ceil(1f/fixedTimeStep)}FPS).\n" +
                                                 "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                 "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                }
            }
#endif
            m_InternalRateManager.SetTimeStep(tickRate.PredictedFixedStepSimulationTimeStep, tickRate.PredictedFixedStepSimulationTickRatio);
        }

        NetcodePredictionFixedRateManager m_InternalRateManager;
        private ComponentSystemBase m_BeginFixedStepSimulationEntityCommandBufferSystem;
        private ComponentSystemBase m_EndFixedStepSimulationEntityCommandBufferSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            SetRateManagerCreateAllocator(null);
            m_InternalRateManager = new NetcodePredictionFixedRateManager(this);
            m_BeginFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystemManaged<BeginFixedStepSimulationEntityCommandBufferSystem>();
            m_EndFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystemManaged<EndFixedStepSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            while (m_InternalRateManager.ShouldGroupUpdate(this))
            {
                m_BeginFixedStepSimulationEntityCommandBufferSystem.Update();
                base.OnUpdate();
                m_EndFixedStepSimulationEntityCommandBufferSystem.Update();
            }
        }
    }
}
