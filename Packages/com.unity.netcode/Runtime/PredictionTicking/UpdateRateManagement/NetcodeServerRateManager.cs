using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 服务端 World 的主要更新速率管理器，根据 Tick 率和时间累加逻辑决定模拟系统组是否运行
    /// Host 侧的 Simulation Group 按帧率运行，Prediction Group 按 Tick 率运行
    /// DGS 侧的 Simulation Group 和 Prediction Group 都按相同 Tick 率运行，后者仅透传更新
    /// </summary>
    public class NetcodeServerRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientSeverTickRateQuery;

        private RunMultiple m_Runner;
        internal NetcodeTimeTracker TimeTracker;
        ComponentSystemGroup m_Group;

        internal NetcodeServerRateManager(ComponentSystemGroup group)
        {
            m_Group = group;

            // 创建单例查询
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientSeverTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());

            m_Runner = new RunMultiple() { ShouldRunFirstTime = ShouldEnterSystemGroupFirstTime, ShouldContinueRun = ShouldContinueRun, OnEnterSystemGroup = OnEnterServerFrame, OnSubsequentRuns = OnSubsequentRuns, OnExitSystemGroup = OnExitServerFrame};
            TimeTracker = new NetcodeTimeTracker(group);
        }

        bool ShouldEnterSystemGroupFirstTime(ComponentSystemGroup group)
        {
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            var updateCountThisFrame = TimeTracker.RefreshUpdateCount(group.World.Time.DeltaTime, tickRate.SimulationFixedTimeStep, tickRate.MaxSimulationStepsPerFrame, tickRate.MaxSimulationStepBatchSize);

            networkTime.NumPredictedTicksExpected = updateCountThisFrame.TotalSteps;
            var shouldRun = TimeTracker.InitializeNetworkTimeForFrame(group, tickRate, updateCountThisFrame);
            return shouldRun;
        }

        bool ShouldContinueRun(ComponentSystemGroup group)
        {
            return TimeTracker.RemainingTicksToRun > 0;
        }

        void OnEnterServerFrame(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            // 首次运行时不需要弹出时间上下文
            TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            TimeTracker.RemainingTicksToRun--;
            var dt = TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnSubsequentRuns(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientSeverTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            TimeTracker.PopTime(group);

            TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            // TODO-2.0：考虑通过破坏性变更改用帧时间，Sleep 模式下差异很小，BusyWait 模式下则应使用真实帧时间而非 Tick DeltaTime
            TimeTracker.RemainingTicksToRun--;
            var dt = TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnExitServerFrame(ComponentSystemGroup group)
        {
            // 在服务端组中压入和弹出时间上下文，以保持与旧版服务端逻辑一致
            TimeTracker.PopTime(group);
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.NumPredictedTicksExpected = 0;
        }

        /// <summary>
        /// 仅供内部使用，在系统组边界调用，用于判断是否进入或退出系统组
        /// </summary>
        /// <param name="group">目标系统组</param>
        /// <returns>系统组是否应更新</returns>
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);
        }

        /// <summary>
        /// 仅供内部使用
        /// </summary>
        /// <exception cref="NotImplementedException">NotImplementedException</exception>
        public float Timestep
        {
            get
            {
                throw new System.NotImplementedException();
            }
            set
            {
                throw new System.NotImplementedException();
            }
        }

        /// <summary>
        /// <para>
        /// 重要：此方法已过时，应改用 <see cref="NetworkTime.IsOffFrame"/>，该方法将在后续版本中移除
        /// </para>
        /// <para>
        /// 用于判断服务端 <see cref="SimulationSystemGroup"/> 本帧是否更新，仅在 <see cref="ClientServerTickRate.TargetFrameRateMode"/> 为 <see cref="ClientServerTickRate.FrameRateMode.BusyWait"/> 时有效
        /// 当 Host 使用 BusyWait 模式时，可在服务端不执行 Tick 的帧中安排客户端操作
        /// 例如 Tick 率为 60 Hz、帧率为 120 Hz 时，客户端托管的服务端每个 Tick 会运行两帧
        /// 因而每两帧中有一帧负载较低，可用于执行额外操作
        /// 可通过服务端的速率管理器访问此方法
        /// </para>
        /// </summary>
        /// <example>
        /// [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        /// [UpdateInGroup(typeof(InitializationSystemGroup))]
        /// public partial class DoExtraWorkSystem : SystemBase
        /// {
        ///     protected override void OnUpdate()
        ///     {
        ///         var serverRateManager = ClientServerBootstrap.ServerWorld.GetExistingSystemManaged&lt;SimulationSystemGroup&gt;().RateManager as NetcodeServerRateManager;
        ///         if (!serverRateManager.WillUpdate())
        ///             DoExtraWork(); // 已知本帧负载较低，可以执行额外工作
        ///     }
        /// }
        /// </example>
        /// <returns>服务端模拟系统组本帧是否更新</returns>
        [Obsolete("Prefer using NetworkTime.IsOffFrame")]
        public bool WillUpdate()
        {
            return WillUpdateInternal();
        }

        /// <summary>
        /// 内部使用的非过时版本，即使移除上方的过时方法也应保留
        /// </summary>
        /// <returns></returns>
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
    }
}
