using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    internal class LateJoinCompletionConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent( entity, new GhostOwner());
        }
    }
    internal class LateJoinCompletionTests
    {
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void ServerGhostCountIsVisibleOnClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                // 验证客户端可见的服务端 Ghost 总数与已接收数量一致
                Assert.AreEqual(8, ghostCount.GhostCountOnServer);
                Assert.AreEqual(8, ghostCount.GhostCountReceivedOnClient);

                // 再生成一批 Ghost，并验证计数随之更新
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                Assert.AreEqual(16, ghostCount.GhostCountOnServer);
                Assert.AreEqual(16, ghostCount.GhostCountReceivedOnClient);
            }
        }
        [Test]
        public void ServerGhostCountOnlyIncludesRelevantSet()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // 进入游戏状态
                testWorld.GoInGame();

                testWorld.Tick();

                // 配置白名单相关性，只将前 6 个 Ghost 标记为相关
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(serverConnectionEnt).Value;
                using var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostInstance>());
                var ghosts = query.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                Assert.AreEqual(ghosts.Length, 8);
                for (int i = 0; i < 6; ++i)
                    ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection{Ghost = ghosts[i].ghostId, Connection = serverConnectionId}, 1);

                // 运行若干 Tick，让客户端生成相关 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                // 验证计数只包含白名单中的 6 个 Ghost
                Assert.AreEqual(6, ghostCount.GhostCountOnServer);
                Assert.AreEqual(6, ghostCount.GhostCountReceivedOnClient);

                // 新生成的 Ghost 不在白名单中，因此计数应保持不变
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                Assert.AreEqual(6, ghostCount.GhostCountOnServer);
                Assert.AreEqual(6, ghostCount.GhostCountReceivedOnClient);
            }
        }
        [Test]
        public void ServerGhostCountDoesNotIncludeIrrelevantSet()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LateJoinCompletionConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // 进入游戏状态
                testWorld.GoInGame();

                testWorld.Tick();

                // 配置黑名单相关性，将前 6 个 Ghost 标记为不相关
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var serverConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(serverConnectionEnt).Value;
                using var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostInstance>());
                var ghosts = query.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                Assert.AreEqual(ghosts.Length, 8);
                for (int i = 0; i < 6; ++i)
                    ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection{Ghost = ghosts[i].ghostId, Connection = serverConnectionId}, 1);

                // 运行若干 Tick，让客户端生成仍然相关的 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                // 验证计数排除黑名单中的 6 个 Ghost
                Assert.AreEqual(2, ghostCount.GhostCountOnServer);
                Assert.AreEqual(2, ghostCount.GhostCountReceivedOnClient);

                // 新生成的 Ghost 默认相关，因此计数应增加 8
                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                Assert.AreEqual(10, ghostCount.GhostCountOnServer);
                Assert.AreEqual(10, ghostCount.GhostCountReceivedOnClient);
            }
        }
    }
}
