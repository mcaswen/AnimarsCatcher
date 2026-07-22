using NUnit.Framework;
using Unity.NetCode;
using Unity.NetCode.Tests;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Tests.Editor
{
    [Category(NetcodeTestCategories.Foundational)]
    internal class SnapshotSequenceIdTests
    {
        [Test]
        public void CalculateSequenceIdDelta_Works()
        {
            // 检查已通过 ServerTick 确认为较新的快照序列号
            const bool confirmedNewer = true;
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 5, confirmedNewer));
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(250, 250, confirmedNewer));
            Assert.AreEqual(1, NetworkSnapshotAck.CalculateSequenceIdDelta(1, 0, confirmedNewer));
            Assert.AreEqual(1, NetworkSnapshotAck.CalculateSequenceIdDelta(2, 1, confirmedNewer));
            Assert.AreEqual(2, NetworkSnapshotAck.CalculateSequenceIdDelta(1, byte.MaxValue, confirmedNewer));
            Assert.AreEqual(10, NetworkSnapshotAck.CalculateSequenceIdDelta(130, 120, confirmedNewer));
            Assert.AreEqual(255, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 6, confirmedNewer));

            // 检查已通过 ServerTick 确认为较旧的过期快照序列号
            const bool confirmedStale = false;
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 5, confirmedStale));
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(250, 250, confirmedStale));
            Assert.AreEqual(-1, NetworkSnapshotAck.CalculateSequenceIdDelta(0, 1, confirmedStale));
            Assert.AreEqual(-255, NetworkSnapshotAck.CalculateSequenceIdDelta(0, byte.MaxValue, confirmedStale));
            Assert.AreEqual(-2, NetworkSnapshotAck.CalculateSequenceIdDelta(byte.MaxValue, 1, confirmedStale));
            Assert.AreEqual(-(256 - 10), NetworkSnapshotAck.CalculateSequenceIdDelta(130, 120, confirmedStale));
            Assert.AreEqual(-255, NetworkSnapshotAck.CalculateSequenceIdDelta(6, 5, confirmedStale));
        }

        [Test]
        public void SnapshotSequenceId_Statistics_NetworkPacketLoss_Works()
        {
            // 测试 Transport 丢包统计
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedDrop = 20; // 丢包间隔为 20，因此丢包率为 5%
                // 必须限制为只处理接收包，否则收包和发包 Job 都会更新内部共享包计数
                // 先运行的 Job 会导致实际丢包率升高或降低，且未指定随机种子时延迟也会影响结果
                // 此设置确保只有接收 Job 增加包计数，从而按预期间隔丢包
                testWorld.DriverSimulatorPacketMode = ApplyMode.ReceivedPacketsOnly;

                var stats = RunForAWhile(testWorld);
                // 不应出现其他类型的包损失
                Assert.Zero(stats.NumPacketsCulledOutOfOrder);
                Assert.Zero(stats.NumPacketsCulledAsArrivedOnSameFrame);
                // 此处应检测到未到达的包
                Assert.NotZero(stats.NumPacketsDroppedNeverArrived);
                // 样本数量较少时统计值可能偏高
                AssertPercentInRange(stats.NetworkPacketLossPercent, 4, 8, "NetworkPacketLossPercent");
                // 检查综合包损失统计
                Assert.AreEqual(stats.NumPacketsDroppedNeverArrived, stats.CombinedPacketLossCount);
                AssertPercentInRange(stats.CombinedPacketLossPercent, 4, 8, "CombinedPacketLossPercent");
            }
        }

        [Test]
        public void SnapshotSequenceId_Statistics_OutOfOrderAndClobbered_Works()
        {
            // 测试抖动导致的乱序和多个包在同一帧到达
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedJitter = 40;

                var stats = RunForAWhile(testWorld);
                // 不应出现其他类型的包损失

                // 在确认包只是乱序之前，NumPacketsDroppedNeverArrived 会暂时将其计为丢失
                Assert.LessOrEqual(stats.NumPacketsDroppedNeverArrived, 5, "NumPacketsDroppedNeverArrived");
                AssertPercentInRange(stats.NetworkPacketLossPercent, 0, 1, "NetworkPacketLossPercent");
                // 此处应检测到同帧覆盖和乱序淘汰
                Assert.NotZero(stats.NumPacketsCulledAsArrivedOnSameFrame, "NumPacketsCulledAsArrivedOnSameFrame");
                AssertPercentInRange(stats.ArrivedOnTheSameFrameClobberedPacketLossPercent, 4, 11, "ArrivedOnTheSameFrameClobberedPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledOutOfOrder, "NumPacketsCulledOutOfOrder");
                AssertPercentInRange(stats.OutOfOrderPacketLossPercent, 35, 45, "OutOfOrderPacketLossPercent");
                // 检查综合包损失统计
                AssertPercentInRange(stats.CombinedPacketLossPercent, 40, 60, "CombinedPacketLossPercent");
            }
        }



        [Test]
        public void SnapshotSequenceId_Statistics_Combined_Works()
        {
            // 同时测试全部包损失类型
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedJitter = 40;
                testWorld.DriverSimulatedDrop = 20; // 丢包间隔为 20，因此丢包率为 5%
                // 必须限制为只处理接收包，否则收包和发包 Job 都会更新内部共享包计数
                // 先运行的 Job 会导致实际丢包率升高或降低，且未指定随机种子时延迟也会影响结果
                // 此设置确保只有接收 Job 增加包计数，从而按预期间隔丢包
                testWorld.DriverSimulatorPacketMode = ApplyMode.ReceivedPacketsOnly;

                var stats = RunForAWhile(testWorld);
                // 所有包损失类型都应产生统计结果
                Assert.NotZero(stats.NumPacketsDroppedNeverArrived);
                AssertPercentInRange(stats.NetworkPacketLossPercent, 4, 8, "NetworkPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledAsArrivedOnSameFrame);
                AssertPercentInRange(stats.ArrivedOnTheSameFrameClobberedPacketLossPercent, 7, 9, "ArrivedOnTheSameFrameClobberedPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledOutOfOrder);
                AssertPercentInRange(stats.OutOfOrderPacketLossPercent, 30, 50, "OutOfOrderPacketLossPercent");
                // 检查综合包损失统计
                AssertPercentInRange(stats.CombinedPacketLossPercent, 45, 55, "CombinedPacketLossPercent");
            }
        }

        private static void AssertPercentInRange(double perc, int min, int max, string fieldName)
        {
            var percMultiplied = (int)(perc * 100);
            Assert.GreaterOrEqual(percMultiplied, min, $"{fieldName} - Percent {perc:P1} within {min} and {max}!");
            Assert.LessOrEqual(percMultiplied, max, $"{fieldName} - Percent {perc:P1} within {min} and {max}!");
        }

        private static SnapshotPacketLossStatistics RunForAWhile(NetCodeTestWorld testWorld)
        {
            const float frameTime = 1.0f / 60.0f;
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject("RandomGhostToTriggerSnapshotSends");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(GhostTypeConverter.GhostTypes.EnableableComponents, EnabledBitBakedValue.StartEnabledAndWaitForClientSpawn);
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect(frameTime, 32); // 丢包可能延迟连接建立，因此允许更多步数
            testWorld.GoInGame();

            const int seconds = 25;
            for (var i = 0; i < seconds * 60; i++)
                testWorld.Tick();

            var stats = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).SnapshotPacketLoss;
            Debug.Log($"Stats after test: {stats.ToFixedString()}!");
            Assert.NotZero(stats.NumPacketsReceived, "Test setup issue!");
            return stats;
        }
    }
}
