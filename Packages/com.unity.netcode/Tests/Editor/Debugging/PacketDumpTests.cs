#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG
using NUnit.Framework;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    internal class PacketDumpTests
    {
        [Test]
        public void NetDebugPacket_IsInitialized()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.DebugPackets = true;
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();
            testWorld.Tick(); // 日志器分别在 GhostSendSystem 和 GhostReceiveSystem 中初始化
                              // 因此需要一个 Tick 才能完成准备
            RunTest(testWorld.ServerWorld);
            RunTest(testWorld.ClientWorlds[0]);
            void RunTest(World world)
            {
                ref var enablePacketLogging = ref testWorld.GetSingletonRW<EnablePacketLogging>(world).ValueRW;
                Assert.IsTrue(enablePacketLogging.IsPacketCacheCreated);
                enablePacketLogging.LogToPacket("Test that we can write to the packet dump!");
                // 测试无法取得 Packet Dump 文件路径，因此这里只验证写入接口可用
                // 文件内容留给后续更完善的分析工具验证
            }
        }
    }
}
#endif
