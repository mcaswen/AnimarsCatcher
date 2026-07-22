using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

// TODO: 支持 Single World Host 全局测试开关后，此处大多数测试应可移除
namespace Unity.NetCode.Tests
{
    // 用于让测试更新序列更清晰的轻量辅助系统
    // [AutoStaticsCleanup]
    [DisableAutoCreation]
    internal partial class GenericExecuteOnUpdateSystem : SystemBase
    {
        public delegate void ExecOnUpdateDelegate(World world);

        public static ExecOnUpdateDelegate ExecOnUpdate;

        protected override void OnUpdate()
        {
            ExecOnUpdate?.Invoke(this.World);
        }

        protected override void OnDestroy()
        {
            ExecOnUpdate = null;
        }
    }

    internal class SingleWorldHostTests
    {
        // TODO: 发布前补充以下测试
        // GhostOwnerIsLocal 行为变化
        // 本地连接
        // 不存在 NetworkStreamConnection 的情况
        // 模拟连接事件
        // 模拟断线事件
        // 验证客户端系统与服务端系统在同一 World 中执行
        // 验证空闲帧中 Input Tick 比 Server Tick 大 1 的情况
        // 验证 Host 断线时，已断开客户端仍有待处理 RPC 的情况
        // 验证使用自定义序列化的透传 RPC
        // 验证裁剪行为正确
        // 验证空闲帧中的 Spawn，并确认 Spawn Tick 设置正确


        [Test]
        [TestCase(true, Ignore = "not implemented yet")]
        [TestCase(false)]
        public void SingleWorldHostValueChecks(bool useNetcodeAPI)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);

            // if (useNetcodeAPI)
            // {
            //     Netcode.Server.StartAsHost(NetworkEndpoint.AnyIpv4.WithPort(9999), hostWorldMode: NetCodeConfig.HostWorldMode.SingleWorld);
            // }
            // else
            {
                testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);
                testWorld.Connect(withConnectionState: true);
            }

            Assert.That(ClientServerBootstrap.ClientWorld, Is.Not.Null);
            Assert.That(ClientServerBootstrap.ClientWorld.IsClient(), Is.True);
            Assert.That(ClientServerBootstrap.ClientWorld.IsServer(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld, Is.Not.Null);
            Assert.That(ClientServerBootstrap.ServerWorld.IsClient(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld.IsServer(), Is.True);
            Assert.That(ClientServerBootstrap.ClientWorld.IsHost(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld.IsHost(), Is.True);
            Assert.That(ClientServerBootstrap.ServerWorld, Is.EqualTo(ClientServerBootstrap.ClientWorld));
            Assert.That(ClientServerBootstrap.ClientWorlds.Count, Is.EqualTo(1));
            if (useNetcodeAPI)
            {
                // Assert.That(Netcode.Server.Connections.Count, Is.EqualTo(1));
                // Assert.That(Netcode.Client.Connection, Is.EqualTo(Netcode.Server.Connections[0]));
                // Assert.That(Netcode.Client.Connection.IsValid);
                // Assert.That(Netcode.Client.Connection.GetConnectionState(), Is.EqualTo(ConnectionState.State.Connected));
                // Assert.That(Netcode.IsClientRole, Is.True);
                // Assert.That(Netcode.IsServerRole, Is.True);
                // Assert.That(Netcode.IsActive, Is.True);
            }
            else
            {
                var serverConnectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId));
                Assert.AreEqual(1, serverConnectionQuery.CalculateEntityCount());
            }
        }

        // TODO-next：Ghost Adapter 回移后取消注释
        // [UnityTest]
        // public IEnumerator SimpleTest([Values] bool useSingleWorld, [Values] bool useRemotes)
        // {
        //     using var testWorld = new NetCodeTestWorld();
        //     using var _ = new TestRPCSend(); // for auto static cleaning
        //     testWorld.Bootstrap(includeNetCodeSystems: true, userSystems: typeof(SimpleMessageHandler));
        //     testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
        //     testWorld.Connect(isHost: useSingleWorld);
        //
        //     var firstNetworkId = Netcode.Client.Connection.NetworkId;
        //
        //     if (useRemotes)
        //     {
        //         TestRPCSend.SendTestMessage(123);
        //         testWorld.RunTicks(3);
        //         yield return testWorld.RunYieldUpdates(3); // 当前 Remotes 尚未在系统中更新，而是使用内部主循环，因此需要 yield
        //         Assert.That(TestRPCSend.TestMessageReceivedCount, Is.EqualTo(1));
        //     }
        //     else
        //     {
        //         var clientWorld = testWorld.ClientWorlds[0];
        //         var serverWorld = testWorld.ServerWorld;
        //
        //         TestMessageExchange(clientWorld, serverWorld, testWorld);
        //     }
        //
        //     testWorld.CreateAdditionalClientWorlds(1);
        //     World pureClientWorld = testWorld.ClientWorlds[1];
        //     testWorld.GetSingletonRW<NetworkStreamDriver>(pureClientWorld).ValueRW.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(7979));
        //     testWorld.TickUntilConnected(pureClientWorld);
        //
        //     // TODO：Remotes 尚不支持 Multi World 测试，支持后重新启用
        //     // if (useRemotes)
        //     // {
        //     //     testWorld.GoInGame(pureClientWorld); // TODO：确认是否可以移除
        //     //     TestRPCSend.SendToClientsMessage(123);
        //     //     testWorld.RunTicks(3);
        //     //     yield return testWorld.RunYieldUpdates(3);
        //     //     Assert.That(TestRPCSend.TestMessageReceivedCountFromServer, Is.EqualTo(2), "wrong server to client remote call count");
        //     // }
        //     // else
        //     {
        //         TestMessageExchange(ClientServerBootstrap.ServerWorld, pureClientWorld, testWorld);
        //     }
        //
        //     Assert.That(Netcode.Client.Connection.NetworkId, Is.EqualTo(firstNetworkId), "Netcode.Client.Connection should be always the same on a single world host and shouldn't change when other clients connect");
        // }

        public struct TestRPC : IRpcCommand
        {
            public int value;
        }

        [Test]
        public void RPCs_InSingleWorldHost_WorksTheSame([Values] bool useSingleWorld)
        {
            // 验证 RPC 在双 World 和 Single World Host 模式下行为一致
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
            testWorld.Connect();

            int valueToTest = 123;

            var serverEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestRPC), typeof(SendRpcCommandRequest));
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new TestRPC(){value = valueToTest});
            testWorld.TickMultiple(2);

            World worldToCheck;
            if (useSingleWorld)
                worldToCheck = testWorld.ServerWorld;
            else
                worldToCheck = testWorld.ClientWorlds[0];
            var clientQuery = worldToCheck.EntityManager.CreateEntityQuery(typeof(TestRPC), typeof(ReceiveRpcCommandRequest));
            Assert.AreEqual(valueToTest, clientQuery.GetSingleton<TestRPC>().value);
        }

        [Test, Description("Sanity check to make sure the test world setup is as expected")]
        public void NetcodeTestWorld_SanityCheck_WhenUsingSingleWorld()
        {
            var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(GenericExecuteOnUpdateSystem));
            testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);
            // Assert.That(testWorld.ServerWorld, Is.EqualTo(testWorld.ClientWorlds[0]));

            // 验证系统没有重复注册
            var allServerSystems = testWorld.ServerWorld.Unmanaged.GetAllSystems(Allocator.Temp);
            HashSet<SystemHandle> systemSet = new HashSet<SystemHandle>(allServerSystems);
            Assert.That(systemSet.Count, Is.EqualTo(allServerSystems.Length), "duplicate found!");

            // 验证系统每帧只更新一次
            var updateCount = 0;

            void CountUpdates(World _)
            {
                updateCount++;
            }

            GenericExecuteOnUpdateSystem.ExecOnUpdate += CountUpdates;
            testWorld.Tick();
            GenericExecuteOnUpdateSystem.ExecOnUpdate -= CountUpdates;
            Assert.That(updateCount, Is.EqualTo(1));
            testWorld.Dispose();
            // 验证 World 列表已正确清理
            Assert.AreEqual(0, ClientServerBootstrap.ServerWorlds.Count);
            Assert.AreEqual(0, ClientServerBootstrap.ClientWorlds.Count);
        }

        [Test]
        public void SingleWorldHost_PartialSnapshot_Works([Values] bool useSingleWorld)
        {
            // Single World Host 会改变 GhostSendSystem 的工作方式
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(GenericExecuteOnUpdateSystem));

            var ghostGameObject = new GameObject("Ghost");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(server: !useSingleWorld, numClients: 1, numHostWorlds: useSingleWorld ? 1 : 0);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
            testWorld.Connect(maxSteps: 16);
            testWorld.GoInGame();
            testWorld.TickMultiple(100); // 等待状态稳定

            int ghostCount = 200;
            using var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, ghostCount, Allocator.Persistent);

            testWorld.TickMultiple(3);
            var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).ToEntityArray(Allocator.Temp);
            Assert.IsTrue(clientGhosts.Length < ghostCount);
            Assert.IsTrue(0 < clientGhosts.Length);
            testWorld.Tick();
            clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance)).ToEntityArray(Allocator.Temp);
            Assert.AreEqual(ghostCount, clientGhosts.Length);
        }
    }
}
