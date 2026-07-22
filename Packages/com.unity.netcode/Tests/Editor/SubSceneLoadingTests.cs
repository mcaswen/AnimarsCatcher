#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostCollectionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class LoadingGhostCollectionSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var ghostCollection = EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
            var subScenes = GetEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>()).ToEntityArray(Allocator.Temp);
            var anyLoaded = false;
            for (int i = 0; i < subScenes.Length; ++i)
                anyLoaded |= SceneSystem.IsSceneLoaded(World.Unmanaged, subScenes[i]);
            for (int g = 0; g < ghostCollection.Length; ++g)
            {
                var ghost = ghostCollection[g];
                if (ghost.GhostPrefab == Entity.Null && !anyLoaded)
                {
                    ghost.Loading = GhostCollectionPrefab.LoadingState.LoadingActive;
                    ghostCollection[g] = ghost;
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class UpdatePrespawnGhostTransform : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SubScenePrespawnBaselineResolved>();
        }

        protected override void OnUpdate()
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            Entities
                .WithAll<PreSpawnedGhostIndex>()
                .ForEach((ref LocalTransform transform) =>
                {
                    transform.Position = new float3(transform.Position.x, transform.Position.y + deltaTime*60.0f, transform.Position.z);
                }).Schedule();
        }
    }

    static class SubSceneStreamingTestHelper
    {
        static public DynamicBuffer<PrespawnSceneLoaded> GetPrespawnLoaded(in NetCodeTestWorld testWorld, World world)
        {
            var collection = testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(world);
            Assert.AreNotEqual(Entity.Null, collection, "The PrespawnLoaded entity does not exist");
            return world.EntityManager.GetBuffer<PrespawnSceneLoaded>(collection);
        }
    }

    internal partial class SubSceneLoadingTests : TestWithSceneAsset
    {
        [Test]
        public void SubSceneListIsSentToClient()
        {
            // 创建包含多种 Prefab 类型的场景
            const int numObjects = 10;
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData2", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            var subScene = SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab1,
                prefab2
            }, numObjects);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                // 流式加载 SubScene
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                testWorld.GoInGame();
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>());
                Assert.IsTrue(query.IsEmptyIgnoreFilter);
                // 第一 Tick 会填充预生成 Ghost，将其加入服务器映射并建立场景列表
                testWorld.Tick();
                query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                // 客户端此时已收到 Prefab，但预生成 Ghost 和 SubScene 要到下一帧才完成初始化
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsTrue(query.IsEmptyIgnoreFilter);
                // 第二 Tick 由服务器发送 SubScene 列表 Ghost
                testWorld.Tick();
                query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>());
                Assert.IsFalse(query.IsEmptyIgnoreFilter);
                // 第三 Tick 开始流式同步预生成 Ghost
                for (int i = 0; i < 10; ++i)
                {
                    testWorld.Tick();
                    var collection = testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ClientWorlds[0]);
                    if(collection != Entity.Null)
                        break;
                }
                var prespawnLoaded = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, prespawnLoaded.Length);

                // 再推进一个 Tick 以更新 Ghost 映射
                testWorld.Tick();

                var sendGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ServerWorld);
                var sendGhostMap = testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton);
                Assert.AreEqual(21, sendGhostMap.Value.Count());
                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                var recvGhostMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton);
                Assert.AreEqual(21, recvGhostMap.ClientGhostEntityMap.Count());
                Assert.AreEqual(21, recvGhostMap.Value.Count());
                // 检查服务器与客户端的预生成 Ghost 映射一致
                foreach (var kv in sendGhostMap.Value)
                {
                    var ghost = kv.Key;
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                        continue;
                    var serverPrespawnId = testWorld.ServerWorld.EntityManager.GetComponentData<PreSpawnedGhostIndex>(kv.Value);
                    Assert.AreEqual(PrespawnHelper.MakePrespawnGhostId(serverPrespawnId.Value + 1), ghost.ghostId);
                    var clientGhost = recvGhostMap.Value[ghost];
                    var clientPrespawnId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PreSpawnedGhostIndex>(clientGhost);
                    Assert.AreEqual(PrespawnHelper.MakePrespawnGhostId(clientPrespawnId.Value + 1), ghost.ghostId);
                    Assert.AreEqual(serverPrespawnId.Value, clientPrespawnId.Value);
                }
            }
        }

        struct SetSomeDataJob : IJobChunk
        {
            public ComponentTypeHandle<SomeData> someDataHandle;
            public int offset;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // 此 Job 不支持包含可启用组件类型的查询
                Assert.IsFalse(useEnabledMask);

                var array = chunk.GetNativeArray(ref someDataHandle);
                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                {
                    array[i] = new SomeData {Value = offset + i};
                }
            }
        }

        [Test]
        public void ClientLoadSceneWhileInGame()
        {
            // 测试包含两个 SubScene
            // 服务器在客户端进入游戏前加载两个场景
            // 客户端先只加载第一个场景，稍后再加载第二个场景

            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.CreateSubSceneWithPrefabs(
                parentScene,
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects);
            var sub1 = SubSceneHelper.CreateSubSceneWithPrefabs(
                parentScene,
                ScenePath, "sub1", new[]
                {
                    ghostPrefab,
                }, numObjects, 5.0f);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                // 服务器加载两个 SubScene，客户端先加载第一个
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, sub0, sub1);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub0);
                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }

                var someDataQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SomeData>());
                Assert.IsFalse(someDataQuery.IsEmptyIgnoreFilter);
                Assert.AreEqual(5, someDataQuery.CalculateEntityCount());

                // 修改服务器上的预生成 Ghost 数据
                var subsceneList = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>())
                    .ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
                var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    ComponentType.ReadWrite<SomeData>(), ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                for (int i = 0; i < subsceneList.Length; ++i)
                {
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subsceneList[i].SubSceneHash
                    });
                    var job = new SetSomeDataJob
                    {
                        someDataHandle = testWorld.ServerWorld.EntityManager.GetComponentTypeHandle<SomeData>(false),
                        offset = 100 + i * 100
                    };
                    Unity.Entities.Internal.InternalCompilerInterface.JobChunkInterface.RunWithoutJobs(ref job, q);
                }

                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub1);
                // 推进若干帧以完成第二个场景的加载和同步
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }

                q = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(10, q.CalculateEntityCount());

                // 检查两个场景的数据均已同步
                q = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    ComponentType.ReadWrite<SomeData>(), ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                for (int i = 0; i < subsceneList.Length; ++i)
                {
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subsceneList[i].SubSceneHash
                    });
                    var data = q.ToComponentDataArray<SomeData>(Allocator.Temp);
                    Assert.AreEqual(1, q.CalculateChunkCount());
                    for (int d = 0; d < numObjects; ++d)
                    {
                        Assert.AreEqual(100 + 100 * i + d, data[d].Value);
                    }
                    data.Dispose();
                }
            }
        }

        [Test]
        public void ServerAndClientsLoadSceneInGame()
        {
            // 测试只包含一个场景，服务器和客户端启动时均不加载它
            // 服务器先发起加载，客户端随后加载同一场景，最终 Ghost 应保持同步



            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.CreateSubSceneWithPrefabs(
                parentScene,
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(LoadingGhostCollectionSystem));
                testWorld.CreateWorlds(true, 1);
                // 只创建场景代理实体，不加载场景内容
                SubSceneHelper.LoadSceneSceneProxies(sub0.SceneGUID, testWorld, 1.0f/60.0f, 200);
                testWorld.Connect();
                testWorld.GoInGame();
                // 推进若干帧，此时不应同步或发送场景内容
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ServerWorld));
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<PrespawnSceneLoaded>(testWorld.ClientWorlds[0]));
                // 服务器先异步加载场景
                SubSceneHelper.LoadSubSceneAsync(testWorld.ServerWorld, testWorld, sub0.SceneGUID);
                // 客户端此时尚未准备好任何 SubScene
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnsSceneInitialized>()).IsEmpty);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubScenePrespawnBaselineResolved>()).IsEmpty);
                // 推进若干帧以同步 Ghost 场景列表
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, subSceneList.Length);
                // 客户端开始加载场景
                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, sub0.SceneGUID);
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // 修改服务器上的预生成 Ghost 数据
                {
                    var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(), ComponentType.ReadWrite<SomeData>());
                    var job = new SetSomeDataJob
                    {
                        someDataHandle = testWorld.ServerWorld.EntityManager.GetComponentTypeHandle<SomeData>(false),
                        offset = 100
                    };
                    Unity.Entities.Internal.InternalCompilerInterface.JobChunkInterface.RunWithoutJobs(ref job, q);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // 检查修改后的数据已同步
                {
                    var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                        ComponentType.ReadWrite<SomeData>());
                    var data = q.ToComponentDataArray<SomeData>(Allocator.Temp);
                    for (int i = 0; i < numObjects; ++i)
                    {
                        Assert.AreEqual(100 + i, data[i].Value);
                    }
                    data.Dispose();
                }
            }
        }

        [Test]
        public void ServerInitiatedSceneUnload()
        {
            Dictionary<ulong, uint2> GetIdsRanges(World world, in DynamicBuffer<PrespawnSceneLoaded> subSceneList)
            {
                // 收集所有 Ghost ID 及其范围，供后续检查 ID 是否复用
                var ranges = new Dictionary<ulong, uint2>();
                using var q = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(),
                    ComponentType.ReadOnly<SubSceneGhostComponentHash>());
                for (int i = 0; i < subSceneList.Length; ++i)
                {
                    q.SetSharedComponentFilter(new SubSceneGhostComponentHash
                    {
                        Value = subSceneList[i].SubSceneHash
                    });
                    var ghostComponents = q.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                    var range = new uint2(uint.MaxValue, uint.MinValue);
                    for (int k = 0; k < ghostComponents.Length; ++k)
                    {
                        range.x = math.min(range.x, (uint)ghostComponents[k].ghostId);
                        range.y = math.max(range.y, (uint)ghostComponents[k].ghostId);
                    }
                    ranges.Add(subSceneList[i].SubSceneHash, range);
                    ghostComponents.Dispose();
                }

                return ranges;
            }

            // 测试包含两个场景，服务器和客户端启动时均已加载
            // 服务器先卸载其中一个场景，客户端稍后跟随卸载
            // 随后服务器和客户端再次加载该场景
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var sub0 = SubSceneHelper.CreateSubSceneWithPrefabs(
                parentScene,
                ScenePath, "Sub0", new[]
                {
                    ghostPrefab,
                }, numObjects);
            SubSceneHelper.CreateSubSceneWithPrefabs(
                parentScene,
                ScenePath, "Sub1", new[]
                {
                    ghostPrefab,
                }, numObjects);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                var idsRanges = GetIdsRanges(testWorld.ServerWorld, subSceneList);
                // 服务器卸载第一个场景，同时销毁其中的 Ghost 并更新场景列表
                SceneSystem.UnloadScene(testWorld.ServerWorld.Unmanaged, sub0.SceneGUID, SceneSystem.UnloadParameters.DestroyMetaEntities);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
                // 此时服务器和客户端的场景列表都只剩一项
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(1, subSceneList.Length);
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(1, subSceneList.Length);
                // 双方都应只剩五个预生成 Ghost
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(numObjects, query.CalculateEntityCount());
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(numObjects, query.CalculateEntityCount());
                // 客户端也卸载该场景
                SceneSystem.UnloadScene(testWorld.ClientWorlds[0].Unmanaged, sub0.SceneGUID, SceneSystem.UnloadParameters.DestroyMetaEntities);
                // 继续推进并确认流程正常
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
                // 重新加载场景，Ghost ID 应复用且双方再次同步
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, sub0);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(2, subSceneList.Length);
                subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ClientWorlds[0]);
                Assert.AreEqual(2, subSceneList.Length);
                // 检查 Sub0 重新分配的 Ghost ID 范围与卸载前一致
                var newRanges = GetIdsRanges(testWorld.ServerWorld, subSceneList);
                for (int i = 0; i < subSceneList.Length; ++i)
                    Assert.AreEqual(idsRanges[subSceneList[i].SubSceneHash], newRanges[subSceneList[i].SubSceneHash]);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], sub0);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
            }
        }

        [Test]
        public void ClientLoadUnloadScene()
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
                testWorld.Bootstrap(true, typeof(LoadingGhostCollectionSystem), typeof(UpdatePrespawnGhostTransform));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                testWorld.Connect();
                // 进入游戏前服务器已提供客户端加载 Prefab 所需的场景列表
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(4, subSceneList.Length);

                // 逐个加载并卸载场景
                for (int scene = 0; scene < 2; ++scene)
                {
                    // 客户端加载当前场景
                    SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[scene].SceneGUID);
                    // 推进若干帧以完成场景初始化
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick();
                    var subSceneEntity = testWorld.TryGetSingletonEntity<PrespawnsSceneInitialized>(testWorld.ClientWorlds[0]);
                    Assert.AreNotEqual(Entity.Null, subSceneEntity);
                    // 客户端应只存在当前场景的五个 Ghost
                    var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>(), ComponentType.ReadOnly<LocalTransform>());
                    Assert.AreEqual(numObjects, query.CalculateEntityCount());

                    // 等待接收服务器上持续变化的 Ghost 状态
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick();

                    var translations = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

                    for (int i = 0; i < translations.Length; ++i)
                        Assert.AreNotEqual(0.0f, translations[i]);

                    // 客户端卸载当前场景
                    SceneSystem.UnloadScene(
                        testWorld.ClientWorlds[0].Unmanaged,
                        subScenes[scene].SceneGUID);
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick();
                    // 卸载后客户端不应存在预生成 Ghost
                    query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                    Assert.AreEqual(0, query.CalculateEntityCount());
                }
            }
        }

        [Test]
        public void ClientReceiveDespawnedGhostsWhenReloadingScene()
        {
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "SimpleGhost", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var subScenes = new SubScene[2];
            for(int i=0;i<subScenes.Length;++i)
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
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[0], subScenes[0]);

                testWorld.Connect();
                testWorld.GoInGame();

                // 只同步场景 0，暂不加载场景 1
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var sendGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ServerWorld);
                var spawnMap = testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton).Value;
                // Host 分别销毁场景 0 和场景 1 中的两个 Ghost
                var despawnedGhosts = new[]
                {
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawnGhostId(1),
                        spawnTick = NetworkTick.Invalid
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawnGhostId(4),
                        spawnTick = NetworkTick.Invalid
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawnGhostId(8),
                        spawnTick = NetworkTick.Invalid
                    },
                    new SpawnedGhost
                    {
                        ghostId = PrespawnHelper.MakePrespawnGhostId(9),
                        spawnTick = NetworkTick.Invalid
                    },
                };

                // 按场景查询顺序调整待销毁 Ghost 的排列
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SceneSectionData>());
                var sceneSectionDatas = query.ToComponentDataArray<SceneSectionData>(Allocator.Temp);
                if (sceneSectionDatas[0].SceneGUID != subScenes[0].SceneGUID)
                {
                    var t1 = despawnedGhosts[0];
                    despawnedGhosts[0] = despawnedGhosts[2];
                    despawnedGhosts[2] = t1;
                    t1 = despawnedGhosts[1];
                    despawnedGhosts[1] = despawnedGhosts[3];
                    despawnedGhosts[3] = t1;
                }

                for(int i=0;i<despawnedGhosts.Length;++i)
                    testWorld.ServerWorld.EntityManager.DestroyEntity(spawnMap[despawnedGhosts[i]]);

                // 客户端应销毁场景 0 中的两个 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                var clientSpawnMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value;
                // 映射中包含三个预生成 Ghost 和一个场景列表 Ghost
                Assert.AreEqual(4, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));

                // 客户端加载第二个场景并补收其中 Ghost 的 Despawn
                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[1].SceneGUID);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                clientSpawnMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value;
                // 映射中包含六个预生成 Ghost 和一个场景列表 Ghost
                Assert.AreEqual(7, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));

                // 客户端卸载场景 0，稍后重载时仍应保留已收到的 Despawn 状态
                // 客户端卸载场景 0
                SceneSystem.UnloadScene(testWorld.ClientWorlds[0].Unmanaged,
                    subScenes[0].SceneGUID);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                clientSpawnMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value;
                // 映射中包含三个预生成 Ghost 和一个场景列表 Ghost
                Assert.AreEqual(4, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));

                SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[0].SceneGUID);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                clientSpawnMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value;
                // 映射中包含六个预生成 Ghost 和一个场景列表 Ghost
                Assert.AreEqual(7, clientSpawnMap.Count());
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[0]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[1]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[2]));
                Assert.IsFalse(clientSpawnMap.ContainsKey(despawnedGhosts[3]));

                LogAssert.Expect(LogType.Warning, new Regex(@"Ack desync at(.*)sent baseline\(s\) we do not have!"));
                LogAssert.Expect(LogType.Warning, new Regex(@"NetworkConnection(.*) reported recoverable snapshot read errors"));
            }
        }
    }
}
