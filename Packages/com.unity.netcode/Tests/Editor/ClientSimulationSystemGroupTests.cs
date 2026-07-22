using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode;
using Unity.NetCode.Tests;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests.Editor
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class TestClientSimulationSystemGroup : SystemBase
    {
        public bool WasUpdated
        {
            get
            {
                if (m_WasUpdated)
                {
                    m_WasUpdated = false;
                    return true;
                }
                return m_WasUpdated;
            }
        }

        bool m_WasUpdated;

        protected override void OnUpdate()
        {
            m_WasUpdated = true;
        }
    }

    internal class ClientSimulationSystemGroup
    {
        /// <summary>
        /// 触发一次回滚，模拟客户端连续 10 个 Tick 未收到服务端数据
        /// </summary>
        [Test]
        public void RollbackWillSkipUpdate()
        {
            const int rollback = 10;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(1.0f / 60.0f);
                testWorld.GoInGame();

                TriggerRollback(testWorld);
                var existingSystemManaged = testWorld.ClientWorlds[0].GetExistingSystemManaged<TestClientSimulationSystemGroup>();
                Assert.That(existingSystemManaged.WasUpdated, "We should not have skipped the updates to SimulationSystemGroup");
                for (int i = 0; i < rollback - 1; i++)
                {
                    testWorld.Tick(1.0f / 60.0f);
                    existingSystemManaged = testWorld.ClientWorlds[0].GetExistingSystemManaged<TestClientSimulationSystemGroup>();
                    Assert.That(!existingSystemManaged.WasUpdated, "We should have skipped updates to SimulationSystemGroup");
                }

                testWorld.Tick(1.0f / 60.0f);
                existingSystemManaged = testWorld.ClientWorlds[0].GetExistingSystemManaged<TestClientSimulationSystemGroup>();
                Assert.That(existingSystemManaged.WasUpdated, "We should not have skipped the updates to SimulationSystemGroup");
            }
        }

#if !UNITY_SERVER
        /// <summary>
        /// 触发一次回滚，模拟客户端连续 10 个 Tick 未收到服务端数据
        /// </summary>
        [Test]
        public void WhenRollbackPredictionErrorWillBeDisplayed()
        {
            // const int rollback = 10;
            LogAssert.Expect(LogType.Error, new Regex(@"Large serverTick prediction error encountered! The serverTick rolled back to \d+ \(a delta of -\d+ ticks\)!"));
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(1.0f / 60.0f);
                testWorld.GoInGame();

                testWorld.Tick(1.0f / 60.0f);
                testWorld.Tick(1.0f / 60.0f);
                testWorld.Tick(1.0f / 60.0f); // curServerTick 与 previousServerTick 首次同时有效
                for (int i = 0; i < 20; i++)
                {
                    testWorld.TickClientWorld();
                }

                for (int i = 0; i < 10; i++)
                {
                    testWorld.Tick(1.0f / 60.0f);
                }
            }
        }
#endif

        static void TriggerRollback(NetCodeTestWorld testWorld, uint rollback = 10)
        {
            // 将 Server Tick 回退到指定数量的历史 Tick
            NetworkTick predictTargetTick;
            NetworkTimeSystemData networkTimeSystemData;

            Entity entity;
            do
            {
                testWorld.Tick(1.0f / 60.0f);
                entity = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(ComponentType.ReadOnly<NetworkTimeSystemData>())
                    .GetSingletonEntity();
                networkTimeSystemData = testWorld.ClientWorlds[0].EntityManager
                    .GetComponentData<NetworkTimeSystemData>(entity);
                predictTargetTick = networkTimeSystemData.predictTargetTick;
            } while (!predictTargetTick.IsValid);
            predictTargetTick.Subtract(rollback);
            networkTimeSystemData.predictTargetTick = predictTargetTick;
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(entity, networkTimeSystemData);
        }
    }
}
