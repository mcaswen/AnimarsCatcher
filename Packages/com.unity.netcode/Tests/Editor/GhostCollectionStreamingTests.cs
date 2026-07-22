using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace Unity.NetCode.Tests
{
    internal class GhostCollectionStreamingConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class OnDemandLoadTestSystem : SystemBase
    {
        public bool IsLoading = false;
        protected override void OnUpdate()
        {
            var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);

            // 当前必须在主线程执行
            for (int i = 0; i < ghostCollection.Length; ++i)
            {
                var ghost = ghostCollection[i];
                if (ghost.GhostPrefab == Entity.Null && IsLoading)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingActive;
                    ghostCollection[i] = ghost;
                }
            }
        }
    }
    internal class GhostCollectionStreamingTests
    {
        [Test]
        public void OnDemandLoadedPrefabsAreUsed()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(OnDemandLoadTestSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostCollectionStreamingConverter();

                testWorld.CreateWorlds(true, 1);

                // 在 World 创建后再创建 Ghost Collection，以便控制其烘焙时机
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.BakeGhostCollection(testWorld.ServerWorld);
                var onDemandSystem = testWorld.ClientWorlds[0].GetExistingSystemManaged<OnDemandLoadTestSystem>();
                onDemandSystem.IsLoading = true;

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();


                // 运行若干 Tick，让客户端有机会生成 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                // 验证 Prefab 尚未加载时客户端不会接收或实例化 Ghost
                Assert.AreEqual(8, ghostCount.GhostCountOnServer);
                Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);

                testWorld.BakeGhostCollection(testWorld.ClientWorlds[0]);
                onDemandSystem.IsLoading = false;
                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();
                // 验证 Prefab 加载完成后客户端已接收并实例化全部 Ghost
                Assert.AreEqual(8, ghostCount.GhostCountOnServer);
                Assert.AreEqual(8, ghostCount.GhostCountReceivedOnClient);
                Assert.AreEqual(8, ghostCount.GhostCountInstantiatedOnClient);
            }
        }
        [Test]
        public void OnDemandLoadFailureCauseError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(OnDemandLoadTestSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostCollectionStreamingConverter();

                testWorld.CreateWorlds(true, 1);

                // 在 World 创建后再创建 Ghost Collection，以便控制其烘焙时机
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.BakeGhostCollection(testWorld.ServerWorld);
                var onDemandSystem = testWorld.ClientWorlds[0].GetExistingSystemManaged<OnDemandLoadTestSystem>();
                onDemandSystem.IsLoading = true;

                for (int i = 0; i < 8; ++i)
                    testWorld.SpawnOnServer(ghostGameObject);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();


                // 运行若干 Tick，让客户端有机会生成 Ghost
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                // 验证 Prefab 尚未加载时客户端不会接收或实例化 Ghost
                Assert.AreEqual(8, ghostCount.GhostCountOnServer);
                Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);

                //testWorld.ConvertGhostCollection(testWorld.ClientWorlds[0]);
                onDemandSystem.IsLoading = false;
                LogAssert.Expect(UnityEngine.LogType.Error, new Regex("^The ghost collection contains a ghost which does not have a valid prefab on the client!"));
                LogAssert.Expect(UnityEngine.LogType.Error, "Disconnecting all the connections because of errors while processing the ghost prefabs (see previous reported errors).");
                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();
            }
        }
    }
}
