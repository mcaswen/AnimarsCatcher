using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using static Unity.NetCode.ClientServerTickRate.FrameRateMode;

namespace Unity.NetCode
{
    /// <summary>
    /// 追踪时间和 Tick 数量，并持续累加时间直至可以运行一个 Tick
    /// </summary>
    internal unsafe class NetcodeTimeTracker
    {
        internal struct Count
        {
            // 模拟应执行的总步数，忽略批处理影响时与 PredictedSimulationSystemGroup 迭代次数一一对应
            public int TotalSteps;
            // 短步数量，例如总步数为 4 且短步数为 1 时，会先执行 3 个长步再执行 1 个短步
            public int ShortStepCount;
            // 长步包含的基础步数，例如该值为 3 时，长步使用 DeltaTime 乘以 3
            // 短步则少一个基础步，使用 DeltaTime 乘以 2
            public int LengthLongSteps;
        }

        internal int RemainingTicksToRun; // 剩余需要运行的预测循环次数
        private float m_AccumulatedTime;
        private bool m_IsFirstTimeExecuting = true;
        private double m_ElapsedTime;
        private Count m_UpdateCount;
        private ProfilerMarker m_fixedUpdateMarker;
        private readonly PredictedFixedStepSimulationSystemGroup m_PredictedFixedStepSimulationSystemGroup;
        private DoubleRewindableAllocators* m_OldGroupAllocators = null;

        internal NetcodeTimeTracker(ComponentSystemGroup group)
        {
            m_fixedUpdateMarker = new ProfilerMarker("ServerFixedUpdate");
            m_PredictedFixedStepSimulationSystemGroup = group.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();
            var networkTimeQuery = group.World.EntityManager.CreateEntityQuery(typeof(NetworkTime));

            var netTimeEntity = group.World.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkTime>());
            group.World.EntityManager.SetName(netTimeEntity, "NetworkTimeSingleton");
            networkTimeQuery.SetSingleton(new NetworkTime
            {
                ServerTick = new NetworkTick(0),
                ServerTickFraction = 1f,
            });
        }

        internal Count RefreshUpdateCount(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength)
        {
            return UpdateAccumulatorForDeltaTime(deltaTime, fixedTimeStep, maxTimeSteps, maxTimeStepLength, ref m_AccumulatedTime);
        }

        internal Count GetUpdateCountReadonly(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength)
        {
            var accumulatedTime = m_AccumulatedTime;
            return UpdateAccumulatorForDeltaTime(deltaTime, fixedTimeStep, maxTimeSteps, maxTimeStepLength, ref accumulatedTime);
        }

        private static Count UpdateAccumulatorForDeltaTime(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength, ref float accumulatedTime)
        {
            accumulatedTime += deltaTime;
            int updateCount = (int)(accumulatedTime / fixedTimeStep);
            // 示例
            // accumulatedTime = 0.16666666
            // fixedTimeStep = 0.016666666
            // updateCount = 10, maxTimeSteps = 4, maxTimeStepLength = 4
            int shortSteps = 0;
            int length = 1;
            if (updateCount > maxTimeSteps) // 10 > 4
            {
                // 计算所需的批处理长度
                // 加上 maxTimeSteps - 1，使整数除法得到向上取整结果
                length = (updateCount + maxTimeSteps - 1) / maxTimeSteps; // (10 + 4 - 1) / 4 = 13/4 = (int)3.25 = 3
                if (length > maxTimeStepLength) // 3 ! > 4
                {
                    length = maxTimeStepLength;
                }
                else
                {
                    // 计算需要多少长步和短步
                    shortSteps = length * maxTimeSteps - updateCount; // 3 * 4 - 10 = 2
                }
                updateCount = maxTimeSteps; // 4
            }

            var longStepCount = updateCount - shortSteps; // 4 - 2 = 2
            var timeConsumedThisFrame = length * fixedTimeStep * longStepCount + (length - 1) * fixedTimeStep * shortSteps; // 3 * 0.016666666 * 2 + (3 - 1) * 0.016666666 * 2 = 0.1666666666 == accumulatedTime
            accumulatedTime -= timeConsumedThisFrame;
            return new Count
            {
                TotalSteps = updateCount,
                ShortStepCount = shortSteps,
                LengthLongSteps = length
            };
        }

        internal bool ShouldSleep(ClientServerTickRate tickRate)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return tickRate.TargetFrameRateMode != BusyWait;
#else
            return tickRate.TargetFrameRateMode == Sleep;
#endif
        }
        /// <summary>
        /// 每次预测迭代时调用，用于更新对应的网络时间状态
        /// 存在需要运行的模拟时返回 true
        /// </summary>
        internal bool InitializeNetworkTimeForFrame(ComponentSystemGroup group, ClientServerTickRate tickRate, Count updateCount)
        {
            // 初始化本帧预测系统组的全部运行次数
            // DGS 侧覆盖整帧，Host 侧仅覆盖预测组
            // TODO-2.0：考虑让 DGS 也只在预测组中执行，以统一 DGS 与 Host 行为
            m_UpdateCount = updateCount;
            RemainingTicksToRun = m_UpdateCount.TotalSteps;
            m_PredictedFixedStepSimulationSystemGroup.ConfigureTimeStep(tickRate); // TODO-MovePred

            if (ShouldSleep(tickRate))
            {
                AdjustTargetFrameRate(tickRate.SimulationTickRate, tickRate.SimulationFixedTimeStep);
            }

            return RemainingTicksToRun > 0;
        }

        internal void PopTime(ComponentSystemGroup group)
        {
            group.World.PopTime();
            group.World.RestoreGroupAllocator(m_OldGroupAllocators);
            m_fixedUpdateMarker.End();
        }

        internal void PushTime(ComponentSystemGroup group, float dt, NetworkTime networkTime)
        {
            group.World.PushTime(new TimeData(networkTime.ElapsedNetworkTime, dt));
            m_OldGroupAllocators = group.World.CurrentGroupAllocators;
            group.World.SetGroupAllocator(group.RateGroupAllocators);
            m_fixedUpdateMarker.Begin();
        }

        internal void UpdateNetworkTime(ComponentSystemGroup group, ClientServerTickRate tickRate, ref NetworkTime networkTime)
        {
            if (m_IsFirstTimeExecuting)
            {
                m_IsFirstTimeExecuting = false;
                // 保持与 UpdateWorldTimeSystem 相同的行为，使首帧从零开始
                // 此处结果会暂时为负数，之后再钳制到零
                m_ElapsedTime = group.World.Time.ElapsedTime - group.World.Time.DeltaTime;
            }
            if (RemainingTicksToRun == (m_UpdateCount.ShortStepCount))
                --m_UpdateCount.LengthLongSteps;
            var dt = GetDeltaTimeForCurrentTick(tickRate);
            // 检查 Tick 回绕
            var currentServerTick = networkTime.ServerTick;
            currentServerTick.Increment();
            var nextTick = currentServerTick;
            nextTick.Add((uint)(m_UpdateCount.LengthLongSteps - 1));
            networkTime.ServerTick = nextTick;
            networkTime.EffectiveInputLatencyTicks = 0;
            networkTime.InterpolationTick = networkTime.ServerTick;
            networkTime.SimulationStepBatchSize = m_UpdateCount.LengthLongSteps;
            if (RemainingTicksToRun == 1)
                networkTime.Flags &= ~NetworkTimeFlags.IsCatchUpTick;
            else
                networkTime.Flags |= NetworkTimeFlags.IsCatchUpTick;
            m_ElapsedTime += dt;
            // World 启动时，如果首帧 DeltaTime 较大，前几个预测 Tick 的 ElapsedTime 会是负数
            // 这样首帧执行多个批处理 Tick 时，累计时间仍能与 World 的 ElapsedTime 对齐
            networkTime.ElapsedNetworkTime = math.max(m_ElapsedTime, 0);
        }

        private void AdjustTargetFrameRate(int tickRate, float fixedTimeStep)
        {
            //
            // Headless 模式下会围绕实际帧率来回微调 Application.targetFrameRate
            // 目标是始终保留约半帧的累计时间，使上方循环恰好执行一次 Tick
            //
            // 使用 targetFrameRate 是为了让 Unity 能在帧之间休眠
            // 从而降低服务端 CPU 占用
            //
            int rate = tickRate;
            const float aboveHalfRange = 0.75f;
            const float belowHalfRange = 0.25f;
            if (m_AccumulatedTime > aboveHalfRange * fixedTimeStep)
                rate += 2; // 更高帧率意味着更小的 DeltaTime，从而减少剩余累计时间
            else if (m_AccumulatedTime < belowHalfRange * fixedTimeStep)
                rate -= 2; // 更低帧率意味着更大的 DeltaTime，从而增加剩余累计时间

            UnityEngine.Application.targetFrameRate = rate;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float GetDeltaTimeForCurrentTick(in ClientServerTickRate tickRate)
        {
            var dt = tickRate.SimulationFixedTimeStep * m_UpdateCount.LengthLongSteps;
            return dt;
        }
    }
}
