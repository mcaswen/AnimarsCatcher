using Unity.Entities;

namespace Unity.NetCode
{
    class NetcodeHostPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientServerTickRateQuery;
        private TickRateManagerStrategy m_Runner;
        private NetcodeTimeTracker m_TimeTracker;

        const NetworkTimeFlags k_ServerPredictionFlags = NetworkTimeFlags.IsInPredictionLoop |
            NetworkTimeFlags.IsFirstPredictionTick |
            NetworkTimeFlags.IsFinalPredictionTick |
            NetworkTimeFlags.IsFinalFullPredictionTick |
            NetworkTimeFlags.IsFirstTimeFullyPredictingTick;

        internal NetcodeHostPredictionRateManager(ComponentSystemGroup group, NetcodeTimeTracker timeTracker)
        {
            m_NetworkTimeQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientServerTickRateQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<ClientServerTickRate>());
            m_TimeTracker = timeTracker;
            m_Runner = new RunMultiple() { ShouldRunFirstTime = ShouldRun, ShouldContinueRun = ShouldRun, OnEnterSystemGroup = OnEnterPredictionLoopForFirstTime, OnExitSystemGroup = OnExitPredictionLoop,  OnSubsequentRuns = OnSubsequentLoops};
        }

        bool ShouldRun(ComponentSystemGroup group)
        {
            // 该值由父级 SimulationSystemGroup 初始化
            // 仅适用于 Host，因为预测组会在单次 SimulationSystemGroup 更新中运行多次
            return m_TimeTracker.RemainingTicksToRun > 0;
        }

        void OnEnterPredictionLoopForFirstTime(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            // 为保证预测组运行前当前 Tick 已准确，首次 UpdateNetworkTime 由父级 SimulationSystemGroup 执行
            m_TimeTracker.RemainingTicksToRun--;
            var dt = m_TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            // Host 仅在预测期间压入固定时间，预测外仍保留真实帧 DeltaTime 供插值等客户端系统使用
            m_TimeTracker.PushTime(group, dt, networkTime);

            networkTime.Flags |= k_ServerPredictionFlags;
        }

        void OnSubsequentLoops(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            m_TimeTracker.PopTime(group);
            m_TimeTracker.UpdateNetworkTime(group, tickRate, ref networkTime);
            m_TimeTracker.RemainingTicksToRun--;
            var dt = m_TimeTracker.GetDeltaTimeForCurrentTick(tickRate);
            m_TimeTracker.PushTime(group, dt, networkTime);
        }

        void OnExitPredictionLoop(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;

            m_TimeTracker.PopTime(group);
            // 重置全部预测标志，因为它们在预测循环之外无效
            networkTime.Flags &= ~k_ServerPredictionFlags;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);

            // 外层系统组已经设置了正确的初始时间
            // 预测循环前，帧级系统应看到本帧即将模拟的 Tick
            // 预测循环后，帧级系统应看到本帧刚完成模拟的 Tick，输入目标 Tick 应加一以便累积下一 Tick 的输入
        }
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
    }
}
