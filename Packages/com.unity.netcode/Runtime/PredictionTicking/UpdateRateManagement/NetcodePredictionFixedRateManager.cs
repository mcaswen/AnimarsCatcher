using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    unsafe class NetcodePredictionFixedRateManager
    {
        public float Timestep
        {
            get => m_TimeStep;
            set
            {
                m_TimeStep = value;
#if UNITY_EDITOR || NETCODE_DEBUG
                m_DeprecatedTimeStep = value;
#endif
            }
        }

        int m_RemainingUpdates;
        float m_TimeStep;
        int m_StepRatio;
        double m_ElapsedTime;
        private EntityQuery networkTimeQuery;
        // 用于追踪对 TimeStep setter 的无效调用
#if UNITY_EDITOR || NETCODE_DEBUG
        float m_DeprecatedTimeStep;
        public float DeprecatedTimeStep
        {
            get=> m_DeprecatedTimeStep;
            set => m_DeprecatedTimeStep = value;
        }

#endif
        DoubleRewindableAllocators* m_OldGroupAllocators = null;

        public int RemainingUpdates => m_RemainingUpdates;

        public NetcodePredictionFixedRateManager(ComponentSystemGroup group)
        {
            SetTimeStep(0f, 0);
            networkTimeQuery = group.EntityManager.CreateEntityQuery(typeof(NetworkTime));
        }

        public void SetTimeStep(float timeStep, int ratio)
        {
            m_TimeStep = timeStep;
            m_StepRatio = ratio;
#if UNITY_EDITOR || NETCODE_DEBUG
            m_DeprecatedTimeStep = 0f;
#endif
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            // 值大于零说明当前是循环中的第二次或后续调用
            if (m_RemainingUpdates > 0)
            {
                group.World.PopTime();
                group.World.RestoreGroupAllocator(m_OldGroupAllocators);
                --m_RemainingUpdates;
            }
            else if(m_TimeStep > 0f)
            {
                var networkTime = networkTimeQuery.GetSingleton<NetworkTime>();
                // stepRatio 为 1:1 时不针对部分 Tick 运行，因为 ClientSimulationSystemGroup 已保证误差处于 Tick 率的 5% 以内
                // 此时再做取整没有实际意义
                // stepRatio 大于一时，物理循环频率更高，部分 Tick 可能实际产生一个或多个物理步
                if (!networkTime.IsPartialTick || m_StepRatio > 1)
                {
                    m_RemainingUpdates = (int) (group.World.Time.DeltaTime / m_TimeStep);
                    m_ElapsedTime = group.World.Time.ElapsedTime;
                    // 当固定循环频率高于模拟频率时，客户端允许物理系统为部分 Tick 运行
                    // 这会增加客户端的物理步进开销，例如 stepRatio 为 2 且物理频率为 120 Hz 时
                    // 客户端平均每个 Tick 会执行 3 次而不是 2 次物理模拟
                    // 部分 Tick 执行 1 次，此时帧 DeltaTime 大于物理 DeltaTime
                    // 完整 Tick 执行 2 次
                    //
                    // 这种 120 Hz 物理更新方式本身正确，但在需要降低开销时可能并不符合预期
                    // 后续可能需要提供配置项，让用户决定是否启用此行为
                    if (networkTime.IsPartialTick)
                    {
                        m_ElapsedTime -= group.World.Time.DeltaTime;
                        m_ElapsedTime += m_RemainingUpdates * m_TimeStep;
                    }
                }
            }
            if (m_RemainingUpdates == 0)
                return false;
            group.World.PushTime(new TimeData(
                elapsedTime: m_ElapsedTime - (m_RemainingUpdates-1)*m_TimeStep,
                deltaTime: m_TimeStep));
            m_OldGroupAllocators = group.World.CurrentGroupAllocators;
            group.World.SetGroupAllocator(group.RateGroupAllocators);
            return true;
        }
    }
}
