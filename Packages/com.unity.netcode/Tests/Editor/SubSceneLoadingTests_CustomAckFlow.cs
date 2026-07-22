#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;

namespace Unity.NetCode.Tests
{
    internal struct RequestUnLoadScene : IRpcCommand
    {
        public ulong SceneHash;
        public NetworkTick ServerTick;
    }
    internal struct NotifySceneLoaded : IRpcCommand
    {
        public ulong SceneHash;
    }
    internal struct NotifyUnloadingScene : IRpcCommand
    {
        public ulong SceneHash;
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    partial class ServerSceneNotificationSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem m_Barrier;
        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystemManaged<EndSimulationEntityCommandBufferSystem>();
            RequireForUpdate<PrespawnSceneLoaded>();
        }

        protected override void OnUpdate()
        {
            var ecb = m_Barrier.CreateCommandBuffer();
            var serverTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            Entities.ForEach((Entity entity, in NotifySceneLoaded streamingReq, in ReceiveRpcCommandRequest requestComponent) =>
            {
                var prespawnSceneAcks = SystemAPI.GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx == -1)
                    prespawnSceneAcks.Add(new PrespawnSectionAck { SceneHash = streamingReq.SceneHash });
                ecb.DestroyEntity(entity);
            }).Schedule();

            Entities.ForEach((Entity entity, in NotifyUnloadingScene streamingReq, in ReceiveRpcCommandRequest requestComponent) =>
            {
                var prespawnSceneAcks = SystemAPI.GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx != -1)
                {
                    prespawnSceneAcks.RemoveAt(ackIdx);
                    // 回传 RPC 以确认服务器已处理卸载通知
                    var reqEnt = ecb.CreateEntity();
                    ecb.AddComponent(reqEnt, new RequestUnLoadScene
                    {
                        SceneHash = streamingReq.SceneHash,
                        ServerTick = serverTick
                    });
                    ecb.AddComponent(reqEnt, new SendRpcCommandRequest
                    {
                        TargetConnection = requestComponent.SourceConnection
                    });
                }
                ecb.DestroyEntity(entity);
            }).Schedule();

            m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial class ClientUnloadSceneSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var hashmap = new NativeParallelHashMap<ulong, Entity>(16, Allocator.TempJob);
            Entities.ForEach((Entity entity, in SubSceneWithPrespawnGhosts sub) =>
            {
                hashmap[sub.SubSceneHash] =  entity;
            }).Run();
            var barrier = World.GetExistingSystemManaged<BeginSimulationEntityCommandBufferSystem>();
            var ecb = barrier.CreateCommandBuffer();
            Entities
                .WithDisposeOnCompletion(hashmap)
                .ForEach((Entity entity, in RequestUnLoadScene unloadScene, in ReceiveRpcCommandRequest requestComponent) =>
                {
                    if(hashmap.TryGetValue(unloadScene.SceneHash, out var sceneEntity))
                    {
                        ecb.RemoveComponent<RequestSceneLoaded>(sceneEntity);
                    }
                    ecb.DestroyEntity(entity);
                }).Schedule();
            barrier.AddJobHandleForProducer(Dependency);
        }
    }

    internal partial class SubSceneLoadingTests
    {
        [Test]
        public void CustomSceneAckFlowTest()
        {
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var subScenes = new SubScene[4];
            for(int i=0;i<4;++i)
            {
                subScenes[i] = SubSceneHelper.CreateSubSceneWithPrefabs(
                    parentScene,
                    ScenePath, $"Sub{i}", new[]
                    {
                        ghostPrefab,
                    }, numObjects);
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(LoadingGhostCollectionSystem),
                    typeof(ServerSceneNotificationSystem),
                    typeof(ClientUnloadSceneSystem));
                testWorld.CreateWorlds(true, 1);
                // 服务器加载全部场景
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                testWorld.Connect();
                // 禁用预生成场景区段的自动上报
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(DisableAutomaticPrespawnSectionReporting));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(DisableAutomaticPrespawnSectionReporting));
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(4, subSceneList.Length);
                ulong lastLoadedSceneHash = 0ul;
                for(int scene=0; scene<4; ++scene)
                {
                    var sceneEntity = SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[scene].SceneGUID);
                    // 推进若干帧以完成场景初始化
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick();

                    var prespawnSection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(sceneEntity)[1].Value;
                    var loadedScenHash = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SubSceneWithPrespawnGhosts>(prespawnSection).SubSceneHash;
                    // 主动通知服务器场景已加载
                    var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                    var notifyLoaded = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(notifyLoaded, new NotifySceneLoaded
                    {
                        SceneHash = loadedScenHash
                    });
                    commandBuffer.AddComponent(notifyLoaded, new SendRpcCommandRequest());
                    commandBuffer.Playback(testWorld.ClientWorlds[0].EntityManager);
                    commandBuffer.Dispose();
                    // 推进若干帧以处理加载确认
                    for (int i = 0; i < 32; ++i)
                        testWorld.Tick();
                    // 通知服务器准备卸载上一个场景
                    if (lastLoadedSceneHash != 0)
                    {
                        commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                        // 为上一个已加载场景发送卸载通知 RPC
                        var reqUnload = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent(reqUnload, new NotifyUnloadingScene { SceneHash = lastLoadedSceneHash });
                        commandBuffer.AddComponent(reqUnload, new SendRpcCommandRequest());
                        commandBuffer.Playback(testWorld.ClientWorlds[0].EntityManager);
                        commandBuffer.Dispose();
                    }
                    lastLoadedSceneHash = loadedScenHash;
                    for (int i = 0; i < 32; ++i)
                        testWorld.Tick();
                    // 客户端应只保留一个活动场景
                    var subSceneEntity = testWorld.TryGetSingletonEntity<PrespawnsSceneInitialized>(testWorld.ClientWorlds[0]);
                    Assert.AreNotEqual(Entity.Null, subSceneEntity);
                    // 客户端应只存在当前场景的五个 Ghost
                    var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                    Assert.AreEqual(numObjects, query.CalculateEntityCount());

                }
            }
        }
    }
}
