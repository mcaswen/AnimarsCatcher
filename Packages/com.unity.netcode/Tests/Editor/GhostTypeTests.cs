using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    internal class GhostTypeIndexConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            baker.DependsOn(gameObject);
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostTypeIndex {Value = gameObject.name == "GhostTypeIndex1Test" ? 1 : 0});
        }
    }

    internal struct GhostTypeIndex : IComponentData
    {
        [GhostField] public int Value;
    }
    internal class GhostTypeTests
    {
        void VerifyGhostTypes(World w)
        {
            var type = ComponentType.ReadOnly<GhostTypeIndex>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            var count = new NativeArray<int>(2, Allocator.Temp);
            var ghosts = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var typeIndex = w.EntityManager.GetComponentData<GhostTypeIndex>(ghosts[i]);
                count[typeIndex.Value] = count[typeIndex.Value] + 1;
            }
            Assert.AreEqual(2, count[0]);
            Assert.AreEqual(2, count[1]);
        }
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void GhostsWithSameArchetypeAreDifferent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject0 = new GameObject();
                ghostGameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeIndexConverter();
                ghostGameObject0.name = "GhostTypeIndex0Test";

                var ghostGameObject1 = new GameObject();
                ghostGameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeIndexConverter();
                ghostGameObject1.name = "GhostTypeIndex1Test";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    ghostGameObject0, ghostGameObject1));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject0);
                testWorld.SpawnOnServer(ghostGameObject0);
                testWorld.SpawnOnServer(ghostGameObject1);
                testWorld.SpawnOnServer(ghostGameObject1);

                VerifyGhostTypes(testWorld.ServerWorld);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 验证相同 Archetype 的两个 Ghost 类型仍被正确区分
                VerifyGhostTypes(testWorld.ClientWorlds[0]);
            }
        }
    }
}
