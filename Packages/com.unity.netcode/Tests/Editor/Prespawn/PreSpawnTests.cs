#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.Tests;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Mathematics;
using Unity.Networking.Transport;
using UnityEngine.TestTools;

namespace Unity.NetCode.PrespawnTests
{
    internal struct EnableVerifyGhostIds : IComponentData
    {}

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class VerifyGhostIds : SystemBase
    {
        public int Matches = 0;
        public static int GhostsPerScene = 7;
        private EntityQuery _ghostComponentQuery;
        private EntityQuery _preSpawnedGhostIdsQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<EnableVerifyGhostIds>();
            _ghostComponentQuery = GetEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
            _preSpawnedGhostIdsQuery = GetEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
        }

        protected override void OnUpdate()
        {
            var ghostComponents = _ghostComponentQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            var preSpawnedGhostIds = _preSpawnedGhostIdsQuery.ToComponentDataArray<PreSpawnedGhostIndex>(Allocator.Temp);
            {
                Matches = 0;
                var idList = new List<int>();
                for (int i = 0; i < ghostComponents.Length; ++i)
                {
                    if (ghostComponents[i].ghostId != 0 && preSpawnedGhostIds.Length >= i)
                    {
                        // Ghost ID 会跨多个场景修补，因此不一定与预生成索引完全相等
                        var ghostId = (int)(ghostComponents[i].ghostId & ~PrespawnHelper.PrespawnGhostIdBase);
                        var diff = ghostId - preSpawnedGhostIds[i].Value - 1;
                        Assert.That(diff % GhostsPerScene == 0, "Prespawned ID not applied properly preID=" + preSpawnedGhostIds[i].Value + " ghostID=" + ghostId);
                        Matches++;
                        idList.Add(ghostId);
                    }
                }

                if (idList.Count == ghostComponents.Length)
                {
                    idList.Sort();
                    for (int i = 0; i < idList.Count - 1; ++i)
                    {
                        Assert.That(idList[i] == idList[i + 1] - 1,
                            "Ghost IDs not in incrementing order [i=" + idList[i] + " i+1=" + idList[i + 1] + "]");
                    }
                }
            }
        }
    }

    internal class PreSpawnTests : TestWithSceneAsset
    {
        void CheckAllPrefabsInWorlds(NetCodeTestWorld testWorld)
        {
            CheckAllPrefabsInWorld(testWorld.ServerWorld);
            for(int i=0;i<testWorld.ClientWorlds.Length;++i)
                CheckAllPrefabsInWorld(testWorld.ClientWorlds[i]);
        }

        void CheckAllPrefabsInWorld(World world)
        {
            // TODO 释放这些查询
            Assert.IsFalse(world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<PreSpawnedGhostIndex>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                }).IsEmptyIgnoreFilter);
            Assert.IsFalse(world.EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<NetCodePrespawnTag>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                }).IsEmptyIgnoreFilter);
            // 检查不含 NetCodePrespawnTag 的 Prefab 不会获得 PreSpawnedGhostIndex
            var query = world.EntityManager.CreateEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<Prefab>(),
                        ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    },
                    None = new []
                    {
                        ComponentType.ReadOnly<NetCodePrespawnTag>()
                    },
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
            Assert.IsTrue(query.IsEmptyIgnoreFilter);
        }

        // 检查 Prefab 和运行时生成的 Ghost 不会误获预生成 ID 组件
        [Test]
        public void PrespawnIdComponentDoesntLeaksToOtherEntitiesInScene()
        {
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "nonghost");
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            var subScene = SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            for (int i = 0; i < 10; ++i)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scene.path);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                CheckAllPrefabsInWorlds(testWorld);
            }
        }

        [Test]
        public void PrespawnIdComponentDoesntLeaksToOtherEntitiesInSubScene()
        {
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "nonghost");
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost,
                new Vector3(0f, 0f, 0f));
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub1", 5, 5, prefab,
                new Vector3(5f, 0f, 0f));
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                CheckAllPrefabsInWorlds(testWorld);
            }
        }

        [Test]
        public void WithNoPrespawnsScenesAreNotInitialized()
        {
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 0, 0, null, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                testWorld.GoInGame();
                // 推进若干 Tick 让预生成 Ghost 处理逻辑有机会运行
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PrespawnsSceneInitialized));
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        [Test]
        public void VerifyPreSpawnIDsAreApplied()
        {
            VerifyGhostIds.GhostsPerScene = 25;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 5, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void DestroyedPreSpawnedObjectsCleanup()
        {
            VerifyGhostIds.GhostsPerScene = 7;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, VerifyGhostIds.GhostsPerScene, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 2);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                // 让第一个客户端进入游戏
                testWorld.SetInGame(0);
                // 在服务器删除一个预生成实体
                var deletedId = 0;
                var q = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var prespawnedQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var prespawnedGhost = q.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                    // 等待预生成处理完成并取得 Ghost ID 已有效的实体
                    if (prespawnedGhost.Length == 0 || (prespawnedGhost.Length > 0 && prespawnedGhost[0].ghostId == 0))
                    {
                        prespawnedGhost.Dispose();
                        continue;
                    }

                    deletedId = prespawnedGhost[0].ghostId;
                    var prespawned = prespawnedQuery.ToEntityArray(Allocator.Temp);
                    testWorld.ServerWorld.EntityManager.DestroyEntity(prespawned[0]);
                    prespawned.Dispose();
                    prespawnedGhost.Dispose();
                    break;
                }
                Assert.True(deletedId < 0);
                // 服务器删除一个预生成 Ghost 后数量应减少一
                testWorld.SetInGame(1);
                // 检查已删除实体在第二个客户端完成清理
                bool exists = false;
                int prespawnedCount = 0;
                var query = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] {ComponentType.ReadOnly<PreSpawnedGhostIndex>()},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                prespawnedCount = query.CalculateEntityCount();
                for (int i = 0; i < 128; ++i)
                {
                    testWorld.Tick();
                    exists = false;
                    var prespawnedData = query.ToComponentDataArray<PreSpawnedGhostIndex>(Allocator.Temp);
                    prespawnedCount = prespawnedData.Length;
                    for (int j = 0; j < prespawnedData.Length; ++j)
                    {
                        // 实体会先从 SubScene 数据加载，等待首个 Ghost 快照更新后将其移除
                        if (prespawnedData[j].Value == deletedId)
                            exists = true;
                    }
                    prespawnedData.Dispose();
                    if (!exists)
                        break;
                }

                Assert.True(prespawnedCount > 0);
                Assert.False(exists, "Found the prespawned entity which should be deleted");
            }
        }

        // 验证七个预生成 Ghost、一个运行时 Ghost 和一个场景列表 Ghost 的清理行为
        // 客户端连接后断开，服务器保留全部九个 Ghost
        // 客户端清理运行时和场景列表 Ghost，只保留 SubScene 中的七个预生成 Ghost
        [Test]
        public void GhostCleanup()
        {
            VerifyGhostIds.GhostsPerScene = 7;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, VerifyGhostIds.GhostsPerScene,
                ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateGhostCollection(new GameObject("DynamicGhost"));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                // 等待预生成 Ghost 完成初始化后再生成运行时实体
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex),
                    typeof(GhostInstance));
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var prespawns = query.CalculateEntityCount();
                    if (prespawns > 0)
                        break;
                }

                // 生成一个运行时 Ghost
                testWorld.SpawnOnServer(0);
                var ghostCount = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                // 等待客户端生成该运行时 Ghost
                int currentCount = 0;
                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(PreSpawnedGhostIndex));
                for (int i = 0; i < 64 && currentCount != ghostCount; ++i)
                {
                    testWorld.Tick();
                    currentCount = clientQuery.CalculateEntityCount();
                }
                Assert.That(ghostCount == currentCount, "Client did not spawn runtime entity (clientCount=" + currentCount + " serverCount=" + ghostCount + ")");

                // 验证运行时生成实体不包含预生成 ID 组件
                var prespawnCount = testWorld.ServerWorld.EntityManager
                    .CreateEntityQuery(typeof(PreSpawnedGhostIndex), typeof(GhostInstance)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnCount, "Runtime spawned server entity got prespawn component added");
                prespawnCount = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(typeof(PreSpawnedGhostIndex), typeof(GhostInstance)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawnCount, "Runtime spawned client entity got prespawn component added");

                testWorld.ClientWorlds[0].EntityManager.AddComponent<NetworkStreamRequestDisconnect>(
                    testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).GetSingletonEntity());

                // 等待客户端 Ghost 完成清理
                int serverGhostCount = 0;
                int clientGhostCount = 0;
                int expectedServerGhostCount = VerifyGhostIds.GhostsPerScene + 2; // 额外包含运行时 Ghost 和场景列表 Ghost
                int expectedClientGhostCount = VerifyGhostIds.GhostsPerScene; // 只保留预生成 Ghost
                var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance));
                var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    // 客户端会短暂创建初始 Archetype Ghost，并在目标 Tick 延迟生成正式实例，因此计数会暂时波动
                    serverGhostCount = serverGhosts.CalculateEntityCount();
                    clientGhostCount = clientGhosts.CalculateEntityCount();
                    //Debug.Log("serverCount=" + serverGhostCount + " clientCount=" + clientGhostCount);
                    //DumpGhosts(serverWorld, clientWorld);
                    if (serverGhostCount == expectedServerGhostCount && clientGhostCount == 0)
                        break;
                }
                Assert.That(serverGhostCount == expectedServerGhostCount, "Server ghosts not correct (count=" + serverGhostCount + " should be " + expectedServerGhostCount);
                Assert.That(clientGhostCount == expectedClientGhostCount, "Ghosts not cleaned up on client (count=" + clientGhostCount + " should be " + expectedClientGhostCount);
            }
        }

        [Test]
        public void MultipleSubscenes()
        {
            const int GhostScenes = 3;
            VerifyGhostIds.GhostsPerScene = 4;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            for (int i = 0; i < GhostScenes; ++i)
            {
                SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), $"Sub_{i}", 2, 2, ghost,
                    new Vector3(i*2.0f, 0.0f, 0.0f)); }
            SceneManager.SetActiveScene(scene);
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                    testWorld.Tick();

                int prespawnedGhostCount = VerifyGhostIds.GhostsPerScene*GhostScenes;
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void ManyPrespawnedObjects()
        {
            const int SubSceneCount = 10;
            const int GhostsPerScene = 500;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            for (int i = 0; i < 10; ++i)
            {
                SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), $"Sub_{i}", 10, 50, ghost,
                    new Vector3((i%5)*10f, 0.0f, (i/5)*50f));
            }
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = GhostsPerScene;
            var prespawnedGhostCount = GhostsPerScene * SubSceneCount;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for (int i=0; i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }

                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(prespawnedGhostCount, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(prespawnedGhostCount, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");

                var clientGhosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var clientGhostPos = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var serverGhostPos = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform), ComponentType.ReadOnly<PreSpawnedGhostIndex>())
                    .ToComponentDataArray<LocalTransform>(Allocator.Temp);

                var serverPosLookup = new NativeParallelHashMap<int, (float3 pos, GhostInstance ghostInstance)>(serverGhostPos.Length, Allocator.Temp);
                Assert.AreEqual(clientGhostPos.Length, serverGhostPos.Length);
                // 建立服务器 Ghost ID 到位置和 GhostInstance 的映射
                for (int i = 0; i < serverGhosts.Length; ++i)
                {
                    serverPosLookup.Add(serverGhosts[i].ghostId, (serverGhostPos[i].Position, serverGhosts[i]));
                }
                for (int i = 0; i < clientGhosts.Length; ++i)
                {
                    Assert.IsTrue(PrespawnHelper.IsPrespawnGhostId(clientGhosts[i].ghostId), "Prespawned ghosts not initialized");
                    // 验证客户端 Ghost ID 在服务器存在且位置与 GhostType 一致
                    Assert.IsTrue(serverPosLookup.TryGetValue(clientGhosts[i].ghostId, out var serverPos));
                    Assert.LessOrEqual(math.distance(clientGhostPos[i].Position, serverPos.pos), 0.001f);
                    Assert.AreEqual(clientGhosts[i].ghostType, serverPosLookup[clientGhosts[i].ghostId].ghostInstance.ghostType);

                    // 移除已匹配的服务器 Ghost ID，以确认不存在重复项
                    serverPosLookup.Remove(clientGhosts[i].ghostId);
                }
                // 验证服务器没有多余实体
                Assert.AreEqual(0, serverPosLookup.Count());
            }
        }

        [Test]
        public void PrefabVariantAreHandledCorrectly()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var variant = SubSceneHelper.CreatePrefabVariant(ghost);
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene,Path.GetDirectoryName(scene.path), "Sub0", new []{ghost, variant}, 5);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 10;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void PrefabModelsAreHandledCorrectly()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.unity.netcode/Tests/Editor/Prespawn/Assets/Whitebox_Ground_1600x1600_A.prefab");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.unity.netcode/Tests/Editor/Prespawn/Assets/Whitebox_Ground_1600x1600_A Variant.prefab");
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene,Path.GetDirectoryName(scene.path), "Sub0", new []{prefab, variant}, 2);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 4;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void MulitpleSubScenesWithSameObjectsPositionAreHandledCorrectly()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub0", 1, 5, ghost, Vector3.zero);
            SubSceneHelper.CreateSubScene(scene,Path.GetDirectoryName(scene.path), "Sub1", 1, 5, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = 5;
            var totalPrespawned = 2 * VerifyGhostIds.GhostsPerScene;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(VerifyGhostIds));
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0;i<64;++i)
                {
                    testWorld.Tick();
                    if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                        testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                        break;
                }
                var prespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(totalPrespawned, prespawned, "Didn't find expected amount of prespawned entities in the server subscene");
                prespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
                Assert.AreEqual(totalPrespawned, prespawned, "Didn't find expected amount of prespawned entities in the client subscene");
                Assert.AreEqual(totalPrespawned, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
                Assert.AreEqual(totalPrespawned, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            }
        }

        [Test]
        public void MismatchedPrespawnClientServerScenesCantConnect()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", 10, 50, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                // 篡改服务器预生成数据，使客户端与服务器 Baseline 不一致
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PreSpawnedGhostIndex>(),
                    ComponentType.ReadOnly<Disabled>());
                var entities = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < 10; ++i)
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(entities[i],
                        LocalTransform.FromPosition(new float3(-10000, 10, 10 * i)));
                }
                entities.Dispose();

                testWorld.Connect();
                testWorld.GoInGame();

                // 收到错误后会立即断开，因此只应记录一次错误
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new Regex(@"Subscene (\w+) baseline mismatch."));
                for(int i=0;i<10;++i)
                    testWorld.Tick();

                // 验证连接已经断开
                var conQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                Assert.AreEqual(0, conQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void ServerTick_AndMaxBaselineAge_WrapAroundDoesNotCauseIssue()
        {
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            const int ensureTwoChunksWorthOfEntities = TypeManager.MaximumChunkCapacity + 2;
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, Path.GetDirectoryName(scene.path), "Sub0", new[] {ghost}, ensureTwoChunksWorthOfEntities);
            SceneManager.SetActiveScene(scene);
            VerifyGhostIds.GhostsPerScene = ensureTwoChunksWorthOfEntities;

            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(VerifyGhostIds));
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            var networkTick = new NetworkTick((UInt32.MaxValue >> 1) - 32);
            testWorld.SetServerTick(networkTick);
            Debug.Log($"Set ServerWorld.NetworkTime.ServerTick:{networkTick.ToFixedString()} to test NetworkTick wraparound!");
            testWorld.ServerWorld.EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));
            testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(EnableVerifyGhostIds));

            // 连接并生成预生成 Ghost
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
                if (testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene &&
                    testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches == VerifyGhostIds.GhostsPerScene)
                    break;
            }

            var serverPrespawned = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, serverPrespawned, "Didn't find expected amount of prespawned entities in the server subscene");
            var clientPrespawned = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PreSpawnedGhostIndex)).CalculateEntityCount();
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, clientPrespawned, "Didn't find expected amount of prespawned entities in the client subscene");
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ServerWorld.GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on server");
            Assert.AreEqual(VerifyGhostIds.GhostsPerScene, testWorld.ClientWorlds[0].GetExistingSystemManaged<VerifyGhostIds>().Matches, "Prespawn components added but didn't get ghost ID applied at runtime on client");
            Assert.AreEqual(testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick.TickValue, testWorld.GetNetworkTime(testWorld.ServerWorld).InterpolationTick.TickValue, "ServerTick is not equal to InterpolationTick on server world");

            // 修改部分 LocalTransform
            const int numToModify = 10;
            var serverLocalTransQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(LocalTransform));
            Assert.IsTrue(serverLocalTransQuery.CalculateChunkCount() > 1, $"Sanity - At least 2 chunks! Capacity:{serverLocalTransQuery.ToArchetypeChunkArray(Allocator.Temp)[0].Capacity}");
            const float multiplierA = 1.11f;
            ModifySomeLocalTransforms(serverLocalTransQuery, multiplierA);

            // 推进并验证 Tick 回绕没有造成异常
            for (int i = 0; i < 32; i++)
                testWorld.Tick();
            VerifyLocalTransforms(serverLocalTransQuery, multiplierA, "NetworkTick-WrapAround");

            // 修改实体的同时将服务器 Tick 跳过至少 MaxBaselineAge，以验证过期 Baseline 处理正确
            ref var networkTime = ref testWorld.GetSingletonRW<NetworkTime>(testWorld.ServerWorld).ValueRW;
            var prevServerTick = networkTime.ServerTick;
            Assert.IsTrue(prevServerTick.TickIndexForValidTick < 1000, "Sanity! Did wrap around!");
            networkTime.ServerTick.Add(GhostSystemConstants.MaxBaselineAge + 1);
            Debug.Log($"Jumped ServerWorld.NetworkTime.ServerTick:{prevServerTick} >> {networkTime.ServerTick} (delta:{networkTime.ServerTick.TicksSince(prevServerTick)}) for MaxBaselineAge!");
            Assert.AreNotEqual(networkTime.ServerTick, prevServerTick, "Sanity!");
            Assert.IsTrue(serverLocalTransQuery.CalculateChunkCount() > 1, $"Sanity - At least 2 chunks! Capacity:{serverLocalTransQuery.ToArchetypeChunkArray(Allocator.Temp)[0].Capacity}");
            const float multiplierB = 5.3f;
            ModifySomeLocalTransforms(serverLocalTransQuery, multiplierB);

            // 推进并验证 MaxBaselineAge 跳跃没有造成异常
            for (int i = 0; i < 64; i++)
                testWorld.Tick();
#if NETCODE_DEBUG
            LogAssert.Expect(LogType.Warning, new Regex("MaxBaselineAge"));
            LogAssert.Expect(LogType.Warning, new Regex("MaxBaselineAge"));
#endif
            VerifyLocalTransforms(serverLocalTransQuery, multiplierB, "MaxBaselineAge");

            // 以下本地函数负责修改和验证 LocalTransform
            static void ModifySomeLocalTransforms(EntityQuery serverLocalTransQuery, float mul)
            {
                var localTransforms = serverLocalTransQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                Assert.IsTrue(localTransforms.Length > numToModify, "Sanity!");
                Assert.AreEqual(ensureTwoChunksWorthOfEntities, localTransforms.Length, "Sanity!");
                var rw = localTransforms.AsSpan();
                for (var i = 0; i < numToModify; i++)
                {
                    ref var trans = ref rw[i];
                    trans.Position = new float3(1, 2, i * mul);
                    trans.Rotation = quaternion.Euler(3, 4, i * mul);
                }
                serverLocalTransQuery.CopyFromComponentDataArray(localTransforms);
            }
            static void VerifyLocalTransforms(EntityQuery serverLocalTransQuery, float mul, string context)
            {
                var localTransforms = serverLocalTransQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                var ro = localTransforms.AsReadOnlySpan();
                for (var i = 0; i < numToModify; i++)
                {
                    ref readonly var trans = ref ro[i];
                    //Debug.Log($"[A] mul:{mul} {i} = {trans.ToString()}");
                    Assert.AreEqual(new float3(1, 2, i * mul), trans.Position, context);
                    Assert.AreEqual(quaternion.Euler(3, 4, i * mul), trans.Rotation, context);
                }
                for (var i = numToModify; i < localTransforms.Length; i++)
                {
                    ref readonly var trans = ref ro[i];
                    //Debug.Log($"[B] mul:{mul} {i} = {trans.ToString()}");
                    Assert.AreNotEqual(new float3(1, 2, i * mul), trans.Position, context);
                    Assert.AreNotEqual(quaternion.Euler(3, 4, i * mul), trans.Rotation, context);
                }
            }
        }

        [Test]
        public void PrespawnsCanGetRelevantAgain()
        {
            int rows = 5;
            int columns = 2;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", rows, columns, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                testWorld.Connect();
                testWorld.GoInGame();

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var relevancySet = ghostRelevancy.GhostRelevancySet;
                var query = testWorld.ServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var ghostComponents = query.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                for (int i = 0; i < ghostComponents.Length; ++i)
                {
                    var ghostId = ghostComponents[i].ghostId;
                    relevancySet.Add(new RelevantGhostForConnection(1, ghostId), 1);
                }

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 验证全部预生成 Ghost 已销毁
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                Assert.AreEqual(0, query.CalculateEntityCount());

                relevancySet.Clear();

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                // 验证全部预生成 Ghost 已重新生成
                query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                // 该查询只统计预生成业务 Ghost，不包含场景列表 Ghost
                Assert.AreEqual(rows*columns, query.CalculateEntityCount());
            }
        }

        [Test]
        public void PrespawnBasicSerialization()
        {
            int rows = 5;
            int columns = 2;
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "Scene1");
            var subScene = SubSceneHelper.CreateSubScene(parentScene, Path.GetDirectoryName(parentScene.path), $"SubScene1", rows, columns, ghost,
                    Vector3.zero);
            SceneManager.SetActiveScene(parentScene);

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 2);
                SubSceneHelper.LoadSubSceneInWorlds(testWorld, subScene);

                testWorld.Connect();
                testWorld.GoInGame();

                // 即使尚未复制，客户端与服务器的预生成数据也应一致
                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                // 验证早期复制不会破坏这些值
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                // 修改服务器上的预生成 Ghost 数据
                {
                    using var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TestComponent1, TestComponent2, TestBuffer3>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
                    using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(builder);
                    var s2 = serverQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var sEntities = serverQuery.ToEntityArray(Allocator.Temp);
                    for (int i = 0; i < sEntities.Length; i++)
                    {
                        var sEntityManager = testWorld.ServerWorld.EntityManager;
                        sEntityManager.SetComponentEnabled<TestComponent1>(sEntities[i], true);
                        sEntityManager.SetComponentEnabled<TestComponent2>(sEntities[i], false);
                        sEntityManager.SetComponentEnabled<TestBuffer3>(sEntities[i], true);

                        s2[i] = new TestComponent2
                        {
                            Test1 = 11,
                            Test2 = 12,
                            Test3 = 13,
                            Test4 = "TEST_14",
                        };

                        var sBuffer = sEntityManager.GetBuffer<TestBuffer3>(sEntities[i]);
                        sBuffer.Length = 20;
                        for (int j = 0; j < sBuffer.Length; j++)
                        {
                            sBuffer[j] = new TestBuffer3
                            {
                                Test1 = 21,
                                Test2 = 22,
                                Test3 = 23,
                                Test4 = 24
                            };
                        }
                    }
                    serverQuery.CopyFromComponentDataArray(s2);
                }

                // 复制新值后再次验证数据同步正确
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                foreach (var clientWorld in testWorld.ClientWorlds)
                    ValidateClientVsServer(testWorld.ServerWorld, clientWorld);

                static void ValidateClientVsServer(World serverWorld, World clientWorld)
                {
                    using var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<TestComponent1, TestComponent2, TestBuffer3>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
                    using var serverQuery = serverWorld.EntityManager.CreateEntityQuery(builder);
                    using var clientQuery = clientWorld.EntityManager.CreateEntityQuery(builder);

                    var s2 = serverQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var sEntities = serverQuery.ToEntityArray(Allocator.Temp);

                    var c1 = clientQuery.ToComponentDataArray<TestComponent1>(Allocator.Temp);
                    var c2 = clientQuery.ToComponentDataArray<TestComponent2>(Allocator.Temp);
                    var cEntities = clientQuery.ToEntityArray(Allocator.Temp);

                    Assert.AreEqual(sEntities.Length, cEntities.Length, "Different number of ghosts on the server vs client!");
                    for (var i = 0; i < sEntities.Length; i++)
                    {
                        // TestComponent1 是标记组件
                        Assert.IsTrue(s2[i].Equals(c2[i]), "TestComponent2 is not the same on client vs server!");

                        var sBuffer = serverWorld.EntityManager.GetBuffer<TestBuffer3>(sEntities[i]);
                        var cBuffer = clientWorld.EntityManager.GetBuffer<TestBuffer3>(cEntities[i]);
                        Assert.AreEqual(sBuffer.Length, cBuffer.Length, "TestBuffer3.Length is not the same on client vs server!");
                        for (int j = 0; j < sBuffer.Length; j++)
                        {
                            Assert.IsTrue(sBuffer[j].Equals(cBuffer[j]), $"TestBuffer3[{j}] entry is not the same on client vs server!");
                        }

                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestComponent1>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestComponent1>(cEntities[i]), "TestComponent1 Enabled bit is not the same on client vs server!");
                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestComponent2>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestComponent2>(cEntities[i]), "TestComponent2 Enabled bit is not the same on client vs server!");
                        Assert.AreEqual(serverWorld.EntityManager.IsComponentEnabled<TestBuffer3>(sEntities[i]), clientWorld.EntityManager.IsComponentEnabled<TestBuffer3>(cEntities[i]), "TestBuffer3 Enabled bit is not the same on client vs server!");
                    }
                }
            }
        }

        [Test]
        public void DisconnectReconnectWithPrespawns()
        {
            // 在客户端和服务器加载预生成场景
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent), typeof(NetCodePrespawnAuthoring));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubSceneWithPrefabs(scene, ScenePath, "subscene", new[] { ghost }, 5);
            SceneManager.SetActiveScene(scene);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 8; i++)
                testWorld.Tick();

            // 服务器连接上的 PrespawnSectionAck 表示已收到客户端开始流式同步预生成 Ghost 的请求
            var serverPrespawnAckQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnSectionAck>());
            Assert.AreEqual(1, serverPrespawnAckQuery.GetSingletonBuffer<PrespawnSectionAck>().Length);

            for (int i = 0; i < 4; i++)
                testWorld.Tick();

            // 断开客户端连接
            using var driverQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamDriver>());
            using var clientConnectionToServer = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            var clientNetworkDriver = driverQuery.GetSingleton<NetworkStreamDriver>();
            testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
            clientNetworkDriver.DriverStore.Disconnect(clientConnectionToServer.GetSingleton<NetworkStreamConnection>());

            for (int i = 0; i < 4; i++)
                testWorld.Tick();

            // 客户端的流式同步请求标志应已禁用
            using var clientGhostCleanupComponentQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithGhostCleanup>());
            var cleanupData = clientGhostCleanupComponentQuery.GetSingleton<SubSceneWithGhostCleanup>();
            Assert.AreEqual(0, cleanupData.Streaming);

            // 在客户端断开期间修改预生成数据
            var serverGhostQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>());
            var serverGhostEntities = serverGhostQuery.ToEntityArray(Allocator.Temp);
            foreach (var testGhost in serverGhostEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentData(testGhost, new TestComponent2(){ Test2 = 10000 });
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<TestBuffer3>(testGhost, true);
                var buf = testWorld.ServerWorld.EntityManager.GetBuffer<TestBuffer3>(testGhost);
                buf.Add(new TestBuffer3() { Test2 = 10 });
                buf.Add(new TestBuffer3() { Test2 = 20 });
            }

            // 重新连接客户端
            clientNetworkDriver.Connect(testWorld.ClientWorlds[0].EntityManager, NetworkEndpoint.LoopbackIpv4.WithPort(7979));
            for (int i = 0; i < 5; i++)
                testWorld.Tick();
            testWorld.GoInGame();

            // 流式同步请求标志应再次启用
            cleanupData = clientGhostCleanupComponentQuery.GetSingleton<SubSceneWithGhostCleanup>();
            Assert.AreEqual(1, cleanupData.Streaming);

            for (int i = 0; i < 6; i++)
                testWorld.Tick();

            // 验证客户端收到最新预生成 Ghost 数据
            using var clientGhostTest2Query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>());
            using var clientGhostEntitiesQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestComponent2>(), ComponentType.ReadOnly<TestBuffer3>());
            var clientGhostTest2Data = clientGhostTest2Query.ToComponentDataArray<TestComponent2>(Allocator.Temp);
            var clientGhostEntities = clientGhostEntitiesQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < clientGhostTest2Data.Length; ++i)
            {
                Assert.AreEqual(10000, clientGhostTest2Data[i].Test2);
                var buf = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestBuffer3>(clientGhostEntities[i]);
                Assert.AreEqual(7, buf.Length);
                Assert.AreEqual(10, buf[^2].Test2);
                Assert.AreEqual(20, buf[^1].Test2);
            }

            // 再次修改预生成数据并验证变更同步到客户端
            foreach (var testGhost in serverGhostEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentData(testGhost, new TestComponent2(){ Test2 = 20000 });
                var buf = testWorld.ServerWorld.EntityManager.GetBuffer<TestBuffer3>(testGhost);
                buf.Add(new TestBuffer3() { Test2 = 30 });
                buf.Add(new TestBuffer3() { Test2 = 40 });
            }
            for (int i = 0; i < 6; i++)
                testWorld.Tick();
            clientGhostTest2Data = clientGhostTest2Query.ToComponentDataArray<TestComponent2>(Allocator.Temp);
            for (int i = 0; i < clientGhostTest2Data.Length; ++i)
            {
                Assert.AreEqual(20000, clientGhostTest2Data[i].Test2);
                var buf = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestBuffer3>(clientGhostEntities[i]);
                Assert.AreEqual(9, buf.Length);
                Assert.AreEqual(30, buf[^2].Test2);
                Assert.AreEqual(40, buf[^1].Test2);
            }
        }

        [Test]
        public void TestPrespawnRelevancy()
        {
            // 预生成场景信息存储在内部 Ghost 中，该 Ghost 必须始终相关

            // 在客户端和服务器加载预生成场景
            var ghost = SubSceneHelper.CreateSimplePrefab(ScenePath, "ghost", typeof(GhostAuthoringComponent));
            var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
            SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), $"Subscene", 2, 2, ghost, Vector3.zero);
            SceneManager.SetActiveScene(scene);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            SubSceneHelper.LoadSubSceneInWorlds(testWorld);

            var serverRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var clientPrespawnSceneQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PrespawnSceneLoaded));
            testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs(); // 完成依赖以访问相关性集合
            var relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

            // 清空相关性集合，此时只有内部预生成跟踪 Ghost 仍应相关
            relevancy.ValueRW.GhostRelevancySet.Clear();
            // 设置相关性后再连接，避免 Ghost 在测试条件生效前意外完成复制
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 4; i++)
            {
                testWorld.Tick();
            }

            Assert.That(clientPrespawnSceneQuery.CalculateEntityCount(), Is.EqualTo(1));

            // 设置排除预生成跟踪 Ghost 的默认查询，并确认内部 Ghost 仍然相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<PrespawnSceneLoaded>().Build(testWorld.ServerWorld.EntityManager);
            for (int i = 0; i < 4; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientPrespawnSceneQuery.CalculateEntityCount(), Is.EqualTo(1));

            // 验证预生成业务 Ghost 正确生成
            Assert.That(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform)).CalculateEntityCount(), Is.EqualTo(4));
            Assert.That(testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform)).CalculateEntityCount(), Is.EqualTo(4));
        }
    }
}
