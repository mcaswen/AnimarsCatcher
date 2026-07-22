using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class MultiEntityGhostConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            baker.AddComponent(entity, new ChildLevelComponent());
            var transform = baker.GetComponent<Transform>();
            baker.DependsOn(transform.parent);
            if (transform.parent == null)
                baker.AddComponent(entity, new TopLevelGhostEntity());
        }
    }
    internal struct TopLevelGhostEntity : IComponentData
    {}
    [GhostComponent(SendDataForChildEntity = false)]
    internal struct ChildLevelComponent : IComponentData
    {
        [GhostField] public int Value;
    }
    internal class MultiEntityGhostTests
    {
        [Test]
        public void ChildEntityDataReplicationCanBeDisabledViaFlagOnGhostComponentAttribute()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new MultiEntityGhostConverter();
                var childGhost = new GameObject();
                childGhost.transform.parent = ghostGameObject.transform;
                childGhost.AddComponent<TestNetCodeAuthoring>().Converter = new MultiEntityGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<TopLevelGhostEntity>(testWorld.ServerWorld);
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<LinkedEntityGroup>(serverEnt));
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEnt);
                Assert.AreEqual(2, serverEntityGroup.Length);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[0].Value, new ChildLevelComponent{Value = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[1].Value, new ChildLevelComponent{Value = 42});

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成多实体 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 验证根实体字段已复制，而子实体字段因 SendDataForChildEntity 为 false 保持默认值
                var clientEnt = testWorld.TryGetSingletonEntity<TopLevelGhostEntity>(testWorld.ClientWorlds[0]);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEnt));
                var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEnt);
                Assert.AreEqual(2, clientEntityGroup.Length);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<ChildLevelComponent>(clientEntityGroup[0].Value).Value);
                Assert.AreEqual(0, testWorld.ClientWorlds[0].EntityManager.GetComponentData<ChildLevelComponent>(clientEntityGroup[1].Value).Value);
            }
        }
        [Test]
        public void ChildEntityDataCanBeReplicatedViaFlagOnGhostComponentAttribute()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new MultiEntityGhostConverter();
                var childGhost = new GameObject();
                childGhost.transform.parent = ghostGameObject.transform;
                childGhost.AddComponent<TestNetCodeAuthoring>().Converter = new MultiEntityGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<TopLevelGhostEntity>(testWorld.ServerWorld);
                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<LinkedEntityGroup>(serverEnt));
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEnt);
                Assert.AreEqual(2, serverEntityGroup.Length);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[0].Value, new GhostOwner{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[1].Value, new GhostOwner{NetworkId = 42});

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成多实体 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 验证 GhostOwner 数据同时复制到根实体和子实体
                var clientEnt = testWorld.TryGetSingletonEntity<TopLevelGhostEntity>(testWorld.ClientWorlds[0]);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEnt));
                var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEnt);
                Assert.AreEqual(2, clientEntityGroup.Length);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEntityGroup[0].Value).NetworkId);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEntityGroup[1].Value).NetworkId);
            }
        }
    }
}
