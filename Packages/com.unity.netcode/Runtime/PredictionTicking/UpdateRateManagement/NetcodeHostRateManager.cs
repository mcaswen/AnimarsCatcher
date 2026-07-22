using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.NetCode
{
    /// <summary>
    /// 基本行为与 <see cref="NetcodeServerRateManager"/> 相同，但还需在非预测帧中额外设置 <see cref="NetworkTime.ServerTick"/>
    /// 非预测帧会累积下一 Tick 的输入，因此应将 <see cref="NetworkTime.InputTargetTick"/> 设置为当前 Tick 加一
    /// ServerTick 应保持不变，用于标识当前状态所属的 Tick
    /// </summary>
    /// 示例
    /// | Tick 10 |       |       |          |       |       | Tick 11          |           |           |
    /// | 帧      | 帧    | 帧    | 帧       | 帧    | 帧    | 帧               | 帧        |           |
    /// |         |       |       | 输入 11  |       |       | 消费输入 11      |           |           |
    /// |         |       |       |          |       |       | 插值 10.2        | 插值 10.4 | 插值 10.6 |
    class NetcodeHostRateManager : IRateManager
    {
        EntityQuery m_NetworkTimeQuery;
        EntityQuery m_ClientSeverTickRateQuery;
        RunOnce m_Runner;
        internal NetcodeTimeTracker TimeTracker;
        ComponentSystemGroup m_Group;

        internal NetcodeHostRateManager(ComponentSystemGroup group)
        {
            m_Group = group;
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(typeof(NetworkTime));

            m_Runner = new RunOnce() { ShouldRun = (_)=>true, OnEnterSystemGroup = OnEnterSimulationGroup, OnExitSystemGroup = OnExitSimulationGroup};
            TimeTracker = new NetcodeTimeTracker(group);
        }

        void OnEnterSimulationGroup(ComponentSystemGroup group)
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            if (tickRate.TargetFrameRateMode == ClientServerTickRate.FrameRateMode.Sleep)
            {
                Debug.LogError($"{nameof(tickRate.TargetFrameRateMode)} set to {nameof(ClientServerTickRate.FrameRateMode.Sleep)} is invalid on a single world host and will be ignored");
                // TODO-Release：应针对移动设备等依赖电池供电的平台处理此模式
            }

            // 此处只做预检查，避免调用其他方法时执行当前不需要的初始化步骤
            // 即使本帧不实际执行 Tick，也必须更新累计时间
            var updateCountThisFrame = TimeTracker.RefreshUpdateCount(group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize);
            networkTime.NumPredictedTicksExpected = updateCountThisFrame.TotalSteps;
            if (updateCountThisFrame.TotalSteps > 0)
            {
                // 本帧将执行 Tick，因此先为预测组预计算，使整帧都使用当前网络时间上下文
                var shouldRunTick = TimeTracker.InitializeNetworkTimeForFrame(group, tickRate, updateCountThisFrame);
                Assert.IsTrue(shouldRunTick, "sanity check failed! we're assuming we are running a tick here");

                TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            }
        }

        void OnExitSimulationGroup(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.NumPredictedTicksExpected = 0;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            // 当前帧需要网络 Tick 上下文
            // 本帧可能不会执行任何 Tick
            return m_Runner.Update(group);
        }

        internal bool WillUpdateInternal()
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            if (TimeTracker.ShouldSleep(tickRate))
            {
                Debug.LogWarning($"Testing if will update when {nameof(ClientServerTickRate.TargetFrameRateMode)} is set to {nameof(ClientServerTickRate.FrameRateMode.Sleep)}. This will always return true.");
            }

            return TimeTracker.GetUpdateCountReadonly(m_Group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize).TotalSteps > 0;
        }

        public float Timestep {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }
    }
}
