using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    unsafe class NetcodeClientPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientServerTickRateQuery;
        private EntityQuery m_ClientTickRateQuery;

        private EntityQuery m_AppliedPredictedTicksQuery;
        private EntityQuery m_UniqueInputTicksQuery;

        private EntityQuery m_GhostQuery;

        private NetworkTick m_LastFullPredictionTick;

        private int m_TickIdx;
        private NetworkTick m_TargetTick;
        private NetworkTime m_CurrentTime;
        private float m_FixedTimeStep;
        private double m_ElapsedTime;

        private NativeArray<NetworkTick> m_AppliedPredictedTickArray;
        private int m_NumAppliedPredictedTicks;

        private uint m_MaxBatchSize;
        private uint m_MaxBatchSizeFirstTimeTick;
        private DoubleRewindableAllocators* m_OldGroupAllocators = null;

        public struct TickComparer : IComparer<NetworkTick>
        {
            public TickComparer(NetworkTick target)
            {
                m_TargetTick = target;
            }
            NetworkTick m_TargetTick;
            public int Compare(NetworkTick x, NetworkTick y)
            {
                var ageX = m_TargetTick.TicksSince(x);
                var ageY = m_TargetTick.TicksSince(y);
                // 按 Tick 年龄降序排列，使 Tick 值升序且最旧的 Tick 位于最前
                return ageY - ageX;
            }
        }

        internal NetcodeClientPredictionRateManager(ComponentSystemGroup group)
        {
            // 创建单例查询
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientServerTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            m_ClientTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientTickRate>());

            m_AppliedPredictedTicksQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostPredictionGroupTickState>());
            m_UniqueInputTicksQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<UniqueInputTickMap>());

            var builder = new EntityQueryDesc
            {
                All = new[]{ComponentType.ReadWrite<Simulate>()},
                Any = new []{ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostChildEntity>()},
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            };
            m_GhostQuery = group.World.EntityManager.CreateEntityQuery(builder);
        }
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            if (m_TickIdx == 0)
            {
                networkTime.PredictedTickIndex = 0;
                networkTime.NumPredictedTicksExpected = 0;
                m_CurrentTime = networkTime;
                m_ClientTickRateQuery.TryGetSingleton<ClientTickRate>(out var clientTickRate);

                m_AppliedPredictedTicksQuery.CompleteDependency();
                m_UniqueInputTicksQuery.CompleteDependency();

                var appliedPredictedTicks = m_AppliedPredictedTicksQuery.GetSingletonRW<GhostPredictionGroupTickState>().ValueRW.AppliedPredictedTicks;
                var uniqueInputTicks = m_UniqueInputTicksQuery.GetSingletonRW<UniqueInputTickMap>().ValueRW.TickMap;


                // 连接尚未进入游戏且还未收到任何 Snapshot，因此当前没有可预测的内容
                // 此时仍在等待首个 Snapshot
                if (!m_CurrentTime.ServerTick.IsValid)
                    return false;

                // 没有预测 Ghost 时，不需要续算或回滚
                if(appliedPredictedTicks.IsEmpty)
                {
                    uniqueInputTicks.Clear();
                    appliedPredictedTicks.Clear();
                    // 预测模式要求存在 Ghost 时提前退出，因为此模式下 AppliedPredictedTicks 不应为空
                    if (clientTickRate.PredictionLoopUpdateMode == PredictionLoopUpdateMode.RequirePredictedGhost)
                    {
                        m_LastFullPredictionTick = NetworkTick.Invalid;
                        return false;
                    }
                }

                m_TargetTick = m_CurrentTime.ServerTick;
                m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();
                m_FixedTimeStep = clientServerTickRate.SimulationFixedTimeStep;
                m_ElapsedTime = group.World.Time.ElapsedTime;
                if (networkTime.IsPartialTick)
                {
                    m_TargetTick.Decrement();
                    m_ElapsedTime -= m_FixedTimeStep * networkTime.ServerTickFraction;
                }
                // 必须模拟最后一个完整 Tick，因为历史备份会在该 Tick 应用
                appliedPredictedTicks.TryAdd(m_TargetTick, m_TargetTick);
                // 必须重新模拟上次使用的最后完整 Tick，因为平滑和误差报告会在该 Tick 执行
                if (m_LastFullPredictionTick.IsValid && m_TargetTick.IsNewerThan(m_LastFullPredictionTick))
                    appliedPredictedTicks.TryAdd(m_LastFullPredictionTick, m_LastFullPredictionTick);
                else if (!m_LastFullPredictionTick.IsValid)
                    m_LastFullPredictionTick = m_TargetTick;



                m_AppliedPredictedTickArray = appliedPredictedTicks.GetKeyArray(Allocator.Temp);

                NetworkTick oldestTick = NetworkTick.Invalid;
                for (int i = 0; i < m_AppliedPredictedTickArray.Length; ++i)
                {
                    NetworkTick appliedTick = m_AppliedPredictedTickArray[i];
                    if (!oldestTick.IsValid || oldestTick.IsNewerThan(appliedTick))
                        oldestTick = appliedTick;
                }
                // 如果该条件成立，说明预测起点附近的 Tick 已被移除
                // 此时直接退出是正确行为
                if (!oldestTick.IsValid)
                {
                    uniqueInputTicks.Clear();
                    appliedPredictedTicks.Clear();
                    return false;
                }
                bool hasNew = false;
                for (var i = oldestTick; i != m_TargetTick; i.Increment())
                {
                    var nextTick = i;
                    nextTick.Increment();
                    if (uniqueInputTicks.TryGetValue(nextTick, out var inputTick))
                    {
                        hasNew |= appliedPredictedTicks.TryAdd(i, i);
                    }
                }
                uniqueInputTicks.Clear();
                if (hasNew)
                    m_AppliedPredictedTickArray = appliedPredictedTicks.GetKeyArray(Allocator.Temp);

                appliedPredictedTicks.Clear();
                m_AppliedPredictedTickArray.Sort(new TickComparer(m_CurrentTime.ServerTick));

                m_NumAppliedPredictedTicks = m_AppliedPredictedTickArray.Length;
                // 移除所有比目标 Tick 更新的记录
                while (m_NumAppliedPredictedTicks > 0 && m_AppliedPredictedTickArray[m_NumAppliedPredictedTicks-1].IsNewerThan(m_TargetTick))
                    --m_NumAppliedPredictedTicks;
                // 移除所有早于“服务端 Tick 减去最大输入数”的记录
                int toRemove = 0;
                while (toRemove < m_NumAppliedPredictedTicks && (uint)m_CurrentTime.ServerTick.TicksSince(m_AppliedPredictedTickArray[toRemove]) > CommandDataUtility.k_CommandDataMaxSize)
                    ++toRemove;
                if (toRemove > 0)
                {
                    m_NumAppliedPredictedTicks -= toRemove;
                    for (int i = 0; i < m_NumAppliedPredictedTicks; ++i)
                        m_AppliedPredictedTickArray[i] = m_AppliedPredictedTickArray[i+toRemove];
                }

                networkTime.Flags |= NetworkTimeFlags.IsInPredictionLoop | NetworkTimeFlags.IsFirstPredictionTick;
                networkTime.Flags &= ~(NetworkTimeFlags.IsFinalPredictionTick|NetworkTimeFlags.IsFinalFullPredictionTick|NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
                networkTime.NumPredictedTicksExpected = m_TargetTick.TicksSince(oldestTick) + (m_CurrentTime.IsPartialTick ? 1 : 0);

                group.World.EntityManager.SetComponentEnabled<Simulate>(m_GhostQuery, false);

                if (clientTickRate.MaxPredictionStepBatchSizeRepeatedTick < 1)
                    clientTickRate.MaxPredictionStepBatchSizeRepeatedTick = 1;
                if (clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick < 1)
                    clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick = 1;
                m_MaxBatchSize = (uint)clientTickRate.MaxPredictionStepBatchSizeRepeatedTick;
                m_MaxBatchSizeFirstTimeTick = (uint)clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick;
                if (!m_LastFullPredictionTick.IsValid)
                    m_MaxBatchSize = m_MaxBatchSizeFirstTimeTick;
                m_TickIdx = 1;
            }
            else
            {
                networkTime.Flags &= ~NetworkTimeFlags.IsFirstPredictionTick;
                group.World.PopTime();
                group.World.RestoreGroupAllocator(m_OldGroupAllocators);
            }
            if (m_TickIdx < m_NumAppliedPredictedTicks)
            {
                NetworkTick predictingTick = m_AppliedPredictedTickArray[m_TickIdx];
                NetworkTick prevTick = m_AppliedPredictedTickArray[m_TickIdx-1];
                uint batchSize = (uint)predictingTick.TicksSince(prevTick);
                if (batchSize > m_MaxBatchSize)
                {
                    batchSize = m_MaxBatchSize;
                    predictingTick = prevTick;
                    predictingTick.Add(batchSize);
                    m_AppliedPredictedTickArray[m_TickIdx-1] = predictingTick;
                }
                else
                {
                    ++m_TickIdx;
                }
                uint tickAge = (uint)m_TargetTick.TicksSince(predictingTick);

                // 到达上次预测的最后完整 Tick 后，切换为新 Tick 专用的长步长设置
                if (predictingTick == m_LastFullPredictionTick)
                    m_MaxBatchSize = m_MaxBatchSizeFirstTimeTick;

                if (predictingTick == m_CurrentTime.ServerTick)
                    networkTime.Flags |= NetworkTimeFlags.IsFinalPredictionTick;
                if (predictingTick == m_TargetTick)
                    networkTime.Flags |= NetworkTimeFlags.IsFinalFullPredictionTick;
                if (!m_LastFullPredictionTick.IsValid || predictingTick.IsNewerThan(m_LastFullPredictionTick))
                {
                    networkTime.Flags |= NetworkTimeFlags.IsFirstTimeFullyPredictingTick;
                    m_LastFullPredictionTick = predictingTick;
                }
                networkTime.ServerTick = predictingTick;
                networkTime.SimulationStepBatchSize = (int)batchSize;
                networkTime.ServerTickFraction = 1f;
                group.World.PushTime(new TimeData(m_ElapsedTime - m_FixedTimeStep*tickAge, m_FixedTimeStep*batchSize));
                m_OldGroupAllocators = group.World.CurrentGroupAllocators;
                group.World.SetGroupAllocator(group.RateGroupAllocators);
                networkTime.PredictedTickIndex++;
                return true;
            }

            if (m_TickIdx == m_NumAppliedPredictedTicks && m_CurrentTime.IsPartialTick)
            {
#if UNITY_EDITOR || NETCODE_DEBUG
                if(networkTime.IsFinalPredictionTick)
                    throw new InvalidOperationException("IsFinalPredictionTick should not be set before executing the final prediction tick");
#endif
                networkTime.ServerTick = m_CurrentTime.ServerTick;
                networkTime.EffectiveInputLatencyTicks = m_CurrentTime.EffectiveInputLatencyTicks;
                networkTime.SimulationStepBatchSize = 1;
                networkTime.ServerTickFraction = m_CurrentTime.ServerTickFraction;
                networkTime.Flags |= NetworkTimeFlags.IsFinalPredictionTick;
                networkTime.Flags &= ~(NetworkTimeFlags.IsFinalFullPredictionTick | NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
                group.World.PushTime(new TimeData(group.World.Time.ElapsedTime, m_FixedTimeStep * m_CurrentTime.ServerTickFraction));
                m_OldGroupAllocators = group.World.CurrentGroupAllocators;
                group.World.SetGroupAllocator(group.RateGroupAllocators);
                ++m_TickIdx;
                networkTime.PredictedTickIndex++;
                return true;
            }
#if UNITY_EDITOR || NETCODE_DEBUG
            if (!networkTime.IsFinalPredictionTick)
                throw new InvalidOperationException("IsFinalPredictionTick should not be set before executing the final prediction tick");
            if (networkTime.ServerTick != m_CurrentTime.ServerTick)
                throw new InvalidOperationException("ServerTick should be equals to current server tick at the end of the prediction loop");
            if (math.abs(networkTime.ServerTickFraction-m_CurrentTime.ServerTickFraction) > 1e-6f)
                throw new InvalidOperationException("ServerTickFraction should be equals to current tick fraction at the end of the prediction loop");
#endif
            // 重置全部预测标志，因为它们在预测循环之外无效
            networkTime.Flags &= ~(NetworkTimeFlags.IsInPredictionLoop |
                                   NetworkTimeFlags.IsFirstPredictionTick |
                                   NetworkTimeFlags.IsFinalPredictionTick |
                                   NetworkTimeFlags.IsFinalFullPredictionTick |
                                   NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
            networkTime.SimulationStepBatchSize = m_CurrentTime.SimulationStepBatchSize;
            m_TickIdx = 0;
            return false;
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
