using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 预测组的速率管理器，父级模拟组负责控制 Tick 率，因此该管理器主要负责透传更新并正确设置 NetworkTime 标志
    /// </summary>
    class NetcodeServerPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private TickRateManagerStrategy m_Runner;

        const NetworkTimeFlags k_ServerPredictionFlags = NetworkTimeFlags.IsInPredictionLoop |
            NetworkTimeFlags.IsFirstPredictionTick |
            NetworkTimeFlags.IsFinalPredictionTick |
            NetworkTimeFlags.IsFinalFullPredictionTick |
            NetworkTimeFlags.IsFirstTimeFullyPredictingTick;

        internal NetcodeServerPredictionRateManager(ComponentSystemGroup group)
        {
            m_NetworkTimeQuery = group.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_Runner = new RunOnce() {ShouldRun = (_)=>true, OnEnterSystemGroup = OnEnterPredictionLoopForFirstTime, OnExitSystemGroup = OnExitPredictionLoop};
        }

        void OnEnterPredictionLoopForFirstTime(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            networkTime.Flags |= k_ServerPredictionFlags;
        }

        void OnExitPredictionLoop(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            // 重置全部预测标志，因为它们在预测循环之外无效
            networkTime.Flags &= ~k_ServerPredictionFlags;
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            return m_Runner.Update(group);
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
