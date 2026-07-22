#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using NUnit.Framework;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode.Tests
{
    internal class GhostRelevancyTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class AutoMarkIrrelevantSystem : SystemBase
    {
        public int ConnectionId;
        public NativeHashSet<int> IrrelevantGhosts;
        protected override void OnCreate()
        {
            IrrelevantGhosts = new NativeHashSet<int>(100, Allocator.TempJob);
        }

        protected override void OnDestroy()
        {
            IrrelevantGhosts.Dispose();
        }

        protected override void OnUpdate()
        {
            ref var ghostRelevancy = ref SystemAPI.GetSingletonRW<GhostRelevancy>().ValueRW;
            var relevancySet = ghostRelevancy.GhostRelevancySet;
            var clearDep = Job.WithCode(() => {
                relevancySet.Clear();
            }).Schedule(Dependency);
            Dependency = JobHandle.CombineDependencies(clearDep, Dependency);
            var connectionId = ConnectionId;
            var irrelevantGhosts = IrrelevantGhosts;
            Entities.ForEach((in GhostInstance ghost, in GhostOwner owner) => {
                if (irrelevantGhosts.Contains(owner.NetworkId))
                    relevancySet.TryAdd(new RelevantGhostForConnection(connectionId, ghost.ghostId), 1);
            }).Schedule();
        }
    }

    internal class RelevancyTests
    {
        GameObject bootstrapAndSetup(NetCodeTestWorld testWorld, System.Type additionalSystem = null)
        {
            if (additionalSystem != null)
                testWorld.Bootstrap(true, additionalSystem);
            else
                testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostRelevancyTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

            testWorld.CreateWorlds(true, 1);
            return ghostGameObject;
        }
        Entity spawnAndSetId(NetCodeTestWorld testWorld, GameObject ghostGameObject, int id)
        {
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = id});
            return serverEnt;
        }

        static int ConnectAndGoInGame(NetCodeTestWorld testWorld)
        {
            // 建立连接并确认连接成功
            testWorld.Connect();

            // 进入游戏状态
            testWorld.GoInGame();

            var con = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, con);
            return testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(con).Value;
        }
        [Test]
        public void EmptyIsRelevantSetSendsNoGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                spawnAndSetId(testWorld, ghostGameObject, 1);

                ConnectAndGoInGame(testWorld);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(Entity.Null, clientEnt);
            }
        }
        [Test]
        public void FullIsRelevantSetSendsAllGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick();
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void HalfIsRelevantSetSendsHalfGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick();
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);
                spawnAndSetId(testWorld, ghostGameObject, 2);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void EmptyIsIrrelevantSetSendsAllGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                spawnAndSetId(testWorld, ghostGameObject, 1);

                ConnectAndGoInGame(testWorld);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void FullIsIrrelevantSetSendsNoGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick();
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(Entity.Null, clientEnt);
            }
        }
        [Test]
        public void HalfIsIrrelevantSetSendsHalfGhosts()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld);

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.Tick();
                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);
                spawnAndSetId(testWorld, ghostGameObject, 2);

                // 推进若干帧以便客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(2, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
            }
        }
        [Test]
        public void MarkedIrrelevantAtSpawnIsNeverSeen()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>().ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);
                testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>().IrrelevantGhosts.Add(1);

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                for (int i = 0; i < 16; ++i)
                {
                    var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    Assert.AreEqual(128, clientValues.Length);
                    for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);

                    testWorld.Tick();
                }
            }
        }
        [Test]
        public void MarkedIrrelevantIsDespawned()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                var autoMarkIrrelevantSystem = testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>();
                autoMarkIrrelevantSystem.ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }
                var serverEnt = spawnAndSetId(testWorld, ghostGameObject, 1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(129, clientValues.Length);
                bool foundOne = false;
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                {
                    if (!foundOne && clientValues[ghost].NetworkId == 1)
                        foundOne = true;
                    else
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);
                }
                Assert.IsTrue(foundOne);

                testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>().IrrelevantGhosts.Add(1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(128, clientValues.Length);
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                    Assert.AreEqual(2, clientValues[ghost].NetworkId);
            }
        }
        void checkValidSet(HashSet<int> checkHashSet, NativeArray<GhostOwner> clientValues, int start, int end)
        {
            checkHashSet.Clear();
            Assert.AreEqual(end-start, clientValues.Length);
            for (int ghost = 0; ghost < clientValues.Length; ++ghost)
            {
                var id = clientValues[ghost].NetworkId;
                Assert.IsTrue(id > start && id <= end);
                Assert.IsFalse(checkHashSet.Contains(id));
                checkHashSet.Add(id);
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void MarkIrrelevantAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>().ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                checkValidSet(checkHashSet, clientValues, 0, 128);

                // 每次更新将 ghostsPerFrame 个新 Ghost 标记为不相关并检查变更是否同步
                for (int start = 0; start+ghostsPerFrame < 128; start += ghostsPerFrame)
                {
                    var autoMarkIrrelevantSystem = testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>();
                    for (int i = 0; i < ghostsPerFrame; ++i)
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Add(start + i + 1);

                    for (int i = 0; i < 6; ++i)
                        testWorld.Tick();

                    clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, start+ghostsPerFrame, 128);
                }
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void MarkRelevantAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                var autoMarkIrrelevantSystem = testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>();
                autoMarkIrrelevantSystem.ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                    autoMarkIrrelevantSystem.IrrelevantGhosts.Add(ghost+1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(0, clientValues.Length);

                // 每次更新将 ghostsPerFrame 个新 Ghost 标记为相关并检查变更是否同步
                for (int start = 0; start+ghostsPerFrame < 128; start += ghostsPerFrame)
                {
                    // 完成依赖以便安全读取查询结果
                    testWorld.ServerWorld.EntityManager.GetComponentData<GhostRelevancy>(testWorld.TryGetSingletonEntity<GhostRelevancy>(testWorld.ServerWorld));
                    for (int i = 0; i < ghostsPerFrame; ++i)
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Remove(start+i+1);
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick();

                    clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, 0, start+ghostsPerFrame);
                }
            }
        }
        [Test]
        [TestCase(16)]
        [TestCase(23)]
        public void ChangeRelevantSetAtRuntimeReachTheClient(int ghostsPerFrame)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                var autoMarkIrrelevantSystem = testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>();
                autoMarkIrrelevantSystem.ConnectionId = serverConnectionId;

                // 相关集合大小为每帧变更量的三倍，因此每帧新增三分之一、移除三分之一并保留三分之一
                int end = ghostsPerFrame*3;
                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, ghost+1);
                    if (ghost >= end)
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Add(ghost+1);
                }

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());

                var checkHashSet = new HashSet<int>();
                var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                checkValidSet(checkHashSet, clientValues, 0, end);

                // 每次更新滑动相关窗口并检查变更是否同步
                for (int start = 0; end+ghostsPerFrame < 128; start += ghostsPerFrame, end += ghostsPerFrame)
                {
                    for (int i = 0; i < ghostsPerFrame; ++i)
                    {
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Add(start+i+1);
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Remove(end+i+1);
                    }
                    for (int i = 0; i < 6; ++i)
                        testWorld.Tick();

                    clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    checkValidSet(checkHashSet, clientValues, start+ghostsPerFrame, end+ghostsPerFrame);
                }
            }
        }
        [Test]
        public void ToggleEveryFrameDoesNotRepetedlySpawn()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 10;
                var ghostGameObject = bootstrapAndSetup(testWorld, typeof(AutoMarkIrrelevantSystem));

                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;

                var serverConnectionId = ConnectAndGoInGame(testWorld);
                var autoMarkIrrelevantSystem = testWorld.ServerWorld.GetExistingSystemManaged<AutoMarkIrrelevantSystem>();
                autoMarkIrrelevantSystem.ConnectionId = serverConnectionId;

                for (int ghost = 0; ghost < 128; ++ghost)
                {
                    spawnAndSetId(testWorld, ghostGameObject, 2);
                }
                spawnAndSetId(testWorld, ghostGameObject, 1);
                // 初始状态下 Ghost 不相关
                autoMarkIrrelevantSystem.IrrelevantGhosts.Add(1);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                // 检查客户端尚未生成该 Ghost
                Assert.AreEqual(128, clientValues.Length);
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                    Assert.AreEqual(2, clientValues[ghost].NetworkId);


                int sawGhost = 0;
                bool foundOne;
                // 切换奇数次以使 Ghost 最终处于相关状态
                for (int i = 0; i < 63; ++i)
                {
                    clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    if (clientValues.Length == 128)
                    {
                        Assert.AreEqual(128, clientValues.Length);
                        for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                            Assert.AreEqual(2, clientValues[ghost].NetworkId);
                    }
                    else
                    {
                        Assert.AreEqual(129, clientValues.Length);

                        foundOne = false;
                        for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                        {
                            if (!foundOne && clientValues[ghost].NetworkId == 1)
                                foundOne = true;
                            else
                                Assert.AreEqual(2, clientValues[ghost].NetworkId);
                        }
                        Assert.IsTrue(foundOne);
                        ++sawGhost;
                    }

                    // 每帧在相关与不相关状态之间切换
                    if ((i&1) == 0)
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Remove(1);
                    else
                        autoMarkIrrelevantSystem.IrrelevantGhosts.Add(1);
                    testWorld.Tick();
                }
                // 由于等待 Despawn 时会跳过部分 Spawn，Ghost 实际相关的帧数应少于一半
                Assert.Less(sawGhost, 32);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // 检查多次切换后以相关状态结束时 Ghost 最终存在
                clientValues = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                foundOne = false;
                for (int ghost = 0; ghost < clientValues.Length; ++ghost)
                {
                    if (!foundOne && clientValues[ghost].NetworkId == 1)
                        foundOne = true;
                    else
                        Assert.AreEqual(2, clientValues[ghost].NetworkId);
                }
                Assert.IsTrue(foundOne);
            }
        }
        [Test]
        public void ManyEntitiesCanBecomeIrrelevantSameTick([Values(NetCodeTestLatencyProfile.PL33, NetCodeTestLatencyProfile.RTT16ms_PL5)]NetCodeTestLatencyProfile profile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.SetTestLatencyProfile(profile);
                testWorld.Bootstrap(true);

                var staticGo = new GameObject("Static");
                staticGo.AddComponent<GhostAuthoringComponent>().OptimizationMode = GhostOptimizationMode.Static;
                staticGo.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                var dynamicGo = new GameObject("Dynamic");
                dynamicGo.AddComponent<GhostAuthoringComponent>().OptimizationMode = GhostOptimizationMode.Dynamic;
                dynamicGo.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(staticGo, dynamicGo));

                testWorld.CreateWorlds(true, 1);

                var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var netCodeTestPrefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection);
                var prefabStatic = netCodeTestPrefabs[0].Value;
                var prefabDynamic = netCodeTestPrefabs[1].Value;
                using (var staticEntities = testWorld.ServerWorld.EntityManager.Instantiate(prefabStatic, 8_000, Allocator.Persistent))
                using (var dynamicEntities = testWorld.ServerWorld.EntityManager.Instantiate(prefabDynamic, 2_000, Allocator.Persistent))
                {
                    testWorld.Connect(maxSteps:32);
                    testWorld.GoInGame();

                    // 推进若干帧以便客户端生成 Ghost
                    for (int i = 0; i < 200; ++i)
                        testWorld.Tick();

                    var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                    Assert.AreEqual(10000, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(10000, ghostCount.GhostCountReceivedOnClient);

                    // 将全部一万个 Ghost 标记为不相关
                    ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                    ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick();

                    // 检查客户端同步结果是否正确
                    Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);

                    testWorld.ServerWorld.EntityManager.DestroyEntity(staticEntities);
                    testWorld.ServerWorld.EntityManager.DestroyEntity(dynamicEntities);

                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick();

                    // 再次检查客户端同步结果是否正确
                    Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);
                }
            }
        }

        [Test(Description = "Tests the BatchScaleWithRelevancy fast-path.")]
        public void Relevancy_ViaDistanceImportanceScaling_Works([Values] GhostOptimizationMode optMode)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.SetTestLatencyProfile(NetCodeTestLatencyProfile.RTT16ms_PL5);
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject("Ghost");
            ghostGameObject.AddComponent<GhostAuthoringComponent>().OptimizationMode = optMode;
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;

            // 每个 1x1x1 网格单元放置一个 Ghost
            const int gridXYZ = 10;
            const int instanceCount = gridXYZ * gridXYZ * gridXYZ;
            testWorld.Connect(maxSteps: 16);
            testWorld.GoInGame();
            using var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, instanceCount, Allocator.Persistent);
            int entId = 0;
            for (int x = 0; x < gridXYZ; x++)
            for (int y = 0; y < gridXYZ; y++)
            for (int z = 0; z < gridXYZ; z++)
            {
                testWorld.ServerWorld.EntityManager.SetComponentData(entities[entId], new LocalTransform
                {
                    Position = new float3(x, y, z),
                    Scale = 1,
                    Rotation = quaternion.identity,
                });
                testWorld.ServerWorld.EntityManager.AddSharedComponent(entities[entId], new GhostDistancePartitionShared
                {
                    Index = new int3(x, y, z),
                });
                entId++;
            }

            var client0NetworkId = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
            testWorld.ServerWorld.EntityManager.AddComponentData(client0NetworkId, new GhostConnectionPosition
            {
                Position = new float3(0),
            });

            // 推进若干帧以便客户端生成 Ghost
            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
            Assert.AreEqual(instanceCount, ghostCount.GhostCountInstantiatedOnClient);
            Assert.AreEqual(instanceCount, ghostCount.GhostCountReceivedOnClient);

            // 将全部 instanceCount 个 Ghost 标记为不相关
            ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
            ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

            testWorld.TryLogPacket("SetIsRelevant:0");
            for (int i = 0; i < 64; ++i)
                testWorld.Tick();

            // 检查客户端同步结果是否正确
            ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
            Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
            Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);
            Assert.AreEqual(0, ghostCount.GhostCountOnServer);

            // 启用 Ghost 距离重要度缩放
            var gridSingleton = testWorld.ServerWorld.EntityManager.CreateSingleton(new GhostDistanceData
            {
                TileSize = new int3(1),
                TileCenter = new int3(.5f),
                TileBorderWidth = new float3(.1f),
            });
            testWorld.ServerWorld.EntityManager.AddComponentData(gridSingleton, new GhostImportance
            {
                BatchScaleImportanceFunction = GhostDistanceImportance.BatchScaleWithRelevancyFunctionPointer,
                GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
                GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
                GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
            });

            // 同步相关 Ghost，启用重要度缩放后边界处的 Ghost 可能需要更多 Tick 才能同步
            for (int i = 0; i < 64; ++i)
                testWorld.Tick();

            // 确认客户端已收到部分 Ghost
            {
                ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                const int expectedCount = 54;
                Assert.That(ghostCount.GhostCountInstantiatedOnClient, Is.EqualTo(expectedCount));
                Assert.That(ghostCount.GhostCountReceivedOnClient, Is.EqualTo(expectedCount));
                Assert.That(ghostCount.GhostCountOnServer, Is.EqualTo(expectedCount));
                Assert.AreEqual(0, ghostRelevancy.GhostRelevancySet.Count(), "No ghosts need to be added to the set.");
            }

            // 移动连接位置并确认收到的 Ghost 集合随之改变
            testWorld.ServerWorld.EntityManager.SetComponentData(client0NetworkId, new GhostConnectionPosition
            {
                Position = new float3(gridXYZ * .5f),
            });
            for (int i = 0; i < 32; ++i)
                testWorld.Tick();

            // 确认客户端收到了新的 Ghost
            {
                ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                const int expectedCount = 257;
                Assert.That(ghostCount.GhostCountInstantiatedOnClient, Is.EqualTo(expectedCount));
                Assert.That(ghostCount.GhostCountReceivedOnClient, Is.EqualTo(expectedCount));
                Assert.That(ghostCount.GhostCountOnServer, Is.EqualTo(expectedCount));
                Assert.AreEqual(0, ghostRelevancy.GhostRelevancySet.Count(), "No ghosts need to be added to the set.");
            }
        }

        [Test]
        public void TestAlwaysRelevantQuery()
        {
            // 验证基础行为，用户查询选中的自定义组件应始终相关

            // 准备生成测试实体
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
            var entity = testWorld.ServerWorld.EntityManager.Instantiate(prefab);

            var serverRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var clientGhostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostValueSerializer));
            var relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            relevancy.ValueRW.GhostRelevancySet.Clear(); // 确保只能通过查询将 Ghost 标记为相关
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick();
            }

            // 确认当前没有相关 Ghost
            Assert.That(clientGhostQuery.IsEmpty);

            // 设置查询并确认 Ghost 变为相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostValueSerializer));
            for (int i = 0; i < 4; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQuery.CalculateEntityCount(), Is.EqualTo(1));
        }

        internal class GhostRelevancyConverterA : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                baker.DependsOn(gameObject);
                var entity = baker.GetEntity(TransformUsageFlags.None);
                baker.AddComponent(entity, new GhostRelevancyA());
            }
        }
        internal class GhostRelevancyConverterB : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                baker.DependsOn(gameObject);
                var entity = baker.GetEntity(TransformUsageFlags.None);
                baker.AddComponent(entity, new GhostRelevancyB());
            }
        }

        internal struct GhostRelevancyA : IComponentData
        {
            [GhostField] public int Value;
        }
        internal struct GhostRelevancyB : IComponentData
        {
            [GhostField] public int Value;
        }

        [Test]
        public void TestMoreComplexAlwaysRelevantQuery()
        {
            // 准备生成测试实体
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var ghostGameObjectPrefabA = new GameObject();
            ghostGameObjectPrefabA.AddComponent<TestNetCodeAuthoring>().Converter = new GhostRelevancyConverterA();
            var authoringA = ghostGameObjectPrefabA.AddComponent<GhostAuthoringComponent>();
            authoringA.DefaultGhostMode = GhostMode.Predicted;
            var ghostGameObjectPrefabB = new GameObject();
            ghostGameObjectPrefabB.AddComponent<TestNetCodeAuthoring>().Converter = new GhostRelevancyConverterB();
            var authoringB = ghostGameObjectPrefabB.AddComponent<GhostAuthoringComponent>();
            authoringB.DefaultGhostMode = GhostMode.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjectPrefabA, ghostGameObjectPrefabB));

            testWorld.CreateWorlds(true, 1);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefabA = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
            var prefabB = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[1].Value;
            var ghostEntityA = testWorld.ServerWorld.EntityManager.Instantiate(prefabA);
            var ghostEntityB = testWorld.ServerWorld.EntityManager.Instantiate(prefabB);

            var serverRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var clientGhostQueryA = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostRelevancyA));
            var clientGhostQueryB = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostRelevancyB));
            var relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            relevancy.ValueRW.GhostRelevancySet.Clear(); // 确保只能通过查询将 Ghost 标记为相关
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick();
            }

            int tickCountForReplication = 4;

            // 清理状态以执行下一项检查
            void Clear()
            {
                relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();

                relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                relevancy.ValueRW.DefaultRelevancyQuery = default;
                relevancy.ValueRW.GhostRelevancySet.Clear();
                for (int i = 0; i < tickCountForReplication; i++)
                {
                    testWorld.Tick();
                }

                Assert.That(clientGhostQueryA.IsEmpty);
                Assert.That(clientGhostQueryB.IsEmpty);
            }

            // 确认当前没有相关 Ghost
            Assert.That(clientGhostQueryA.IsEmpty);
            Assert.That(clientGhostQueryB.IsEmpty);

            // 设置 A 查询并确认对应 Ghost 变为相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancyA));
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.IsEmpty);

            Clear();

            // 设置 B 查询并确认对应 Ghost 变为相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancyB));
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.IsEmpty);
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            Clear();

            // 设置联合查询并确认两个 Ghost 均变为相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithAny<GhostRelevancyA, GhostRelevancyB>().Build(testWorld.ServerWorld.EntityManager);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            Clear();

            // 验证逐连接集合与默认查询取并集
            var connection = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId)).GetSingleton<NetworkId>();
            var ghostIDA = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntityA).ghostId;
            var ghostIDB = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntityB).ghostId;

            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancySet.Clear();
            relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDA), 0);
            relevancy.ValueRW.DefaultRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancyB));
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            Clear();

            // 验证逐连接集合可以在查询未匹配时补充相关 Ghost
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancySet.Clear();
            relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDA), 0);
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<GhostRelevancyA, GhostRelevancyB>().Build(testWorld.ServerWorld.EntityManager);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.IsEmpty);

            // 保持逐连接集合不变并更换相关性查询
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            // A 已由逐连接集合标记为相关
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<GhostRelevancyB>().Build(testWorld.ServerWorld.EntityManager);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            // 验证未配置查询且集合为空时没有相关 Ghost
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.DefaultRelevancyQuery = default;
            relevancy.ValueRW.GhostRelevancySet.Clear();
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.IsEmpty);
            Assert.That(clientGhostQueryB.IsEmpty);

            Clear();

            // 验证禁用相关性过滤后内部查询不会受用户查询限制
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.Disabled;
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<GhostRelevancyA, GhostRelevancyB>().Build(testWorld.ServerWorld.EntityManager); // 禁用过滤后应忽略该查询
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            Clear();

            // 验证用户显式标记 Ghost 不相关时全匹配查询不会覆盖该结果
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithAny<GhostRelevancyA, GhostRelevancyB>().Build(testWorld.ServerWorld.EntityManager);
            relevancy.ValueRW.GhostRelevancySet.Clear();
            relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDA), 0);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.IsEmpty);
            Assert.That(clientGhostQueryB.CalculateEntityCount(), Is.EqualTo(1));

            Clear();
            // 验证兼容行为，使用 SetIsIrrelevant 且未指定查询时集合外的 Ghost 默认相关
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
            relevancy.ValueRW.DefaultRelevancyQuery = default; // 默认查询匹配所有实体，因此该配置有效
            relevancy.ValueRW.GhostRelevancySet.Clear();
            // B 被显式排除，A 则被隐式包含
            relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDB), 0);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.IsEmpty);

            Clear();
            // 验证 None 查询过滤条件与 SetIsIrrelevant 的组合行为
            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
            relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<GhostRelevancyA>().Build(testWorld.ServerWorld.EntityManager);
            relevancy.ValueRW.GhostRelevancySet.Clear();
            // 查询排除 A，但集合未显式排除 A，因此 A 仍然相关
            relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDB), 0);
            for (int i = 0; i < tickCountForReplication; i++)
            {
                testWorld.Tick();
            }
            Assert.That(clientGhostQueryA.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(clientGhostQueryB.IsEmpty);
        }

        [Test(Description = "Set the relevancy of EntityA only, then ensures the relevancy sub-system works correctly (and that GhostCount's are correct).")]
        [TestCase(GhostRelevancyMode.SetIsRelevant, true, true, true)]
        [TestCase(GhostRelevancyMode.SetIsRelevant, true, false, true)]
        [TestCase(GhostRelevancyMode.SetIsRelevant, false, true, true)]
        [TestCase(GhostRelevancyMode.SetIsRelevant, false, false, false)]
        [TestCase(GhostRelevancyMode.SetIsIrrelevant, true, true, false)]
        [TestCase(GhostRelevancyMode.SetIsIrrelevant, true, false, true)]
        [TestCase(GhostRelevancyMode.SetIsIrrelevant, false, true, false)]
        [TestCase(GhostRelevancyMode.SetIsIrrelevant, false, false, true)] // 集合未包含时默认需要同步该 Ghost
        [TestCase(GhostRelevancyMode.Disabled, true, true, true)]
        [TestCase(GhostRelevancyMode.Disabled, true, false, true)]
        [TestCase(GhostRelevancyMode.Disabled, false, true, true)]
        [TestCase(GhostRelevancyMode.Disabled, false, false, true)]
        public void TestRelevancyScenarios(GhostRelevancyMode mode, bool queryMatchesGhost, bool setContainsGhost, bool expectedRelevancyResult)
        {
            // 准备生成测试实体
            using var testWorld = new NetCodeTestWorld();
            testWorld.SetTestLatencyProfile(NetCodeTestLatencyProfile.RTT16ms_PL5);
            testWorld.Bootstrap(true);
            var ghostGameObjectPrefabA = new GameObject();
            ghostGameObjectPrefabA.AddComponent<TestNetCodeAuthoring>().Converter = new GhostRelevancyConverterA();
            var authoringA = ghostGameObjectPrefabA.AddComponent<GhostAuthoringComponent>();
            authoringA.DefaultGhostMode = GhostMode.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjectPrefabA));

            testWorld.CreateWorlds(true, 1);
            var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefabA = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
            var ghostEntityA = testWorld.ServerWorld.EntityManager.Instantiate(prefabA);

            var serverRelevancyQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostRelevancy));
            var clientGhostQueryA = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostRelevancyA));
            var relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();

            relevancy.ValueRW.GhostRelevancyMode = mode;
            relevancy.ValueRW.GhostRelevancySet.Clear(); // 确保只能通过查询将 Ghost 标记为相关
            testWorld.Connect(maxSteps:16);
            testWorld.GoInGame();
            for (int i = 0; i < 8; i++)
            {
                testWorld.Tick();
            }

            var connection = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId)).GetSingleton<NetworkId>();
            var ghostIDA = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ghostEntityA).ghostId;

            relevancy = serverRelevancyQuery.GetSingletonRW<GhostRelevancy>();
            if (queryMatchesGhost)
            {
                relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithAny<GhostRelevancyA>().Build(testWorld.ServerWorld.EntityManager);
            }
            else
            {
                relevancy.ValueRW.DefaultRelevancyQuery = new EntityQueryBuilder(Allocator.Temp).WithNone<GhostRelevancyA>().Build(testWorld.ServerWorld.EntityManager);
            }

            if (setContainsGhost)
            {
                relevancy.ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(connection.Value, ghostIDA), 0);
            }

            for (int i = 0; i < 8; i++)
            {
                testWorld.Tick();
            }

            Assert.That(clientGhostQueryA.CalculateEntityCount(), expectedRelevancyResult ? Is.EqualTo(1) : Is.EqualTo(0));

            // 检查 GhostCount 单例中的统计值
            var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
            string msg = ghostCount.ToString();
            int expectedGhostInstancesCount = expectedRelevancyResult ? 1 : 0;
            Assert.AreEqual(expectedGhostInstancesCount, ghostCount.GhostCountOnServer, msg);
            Assert.AreEqual(expectedGhostInstancesCount, ghostCount.GhostCountReceivedOnClient, msg);
            Assert.AreEqual(expectedGhostInstancesCount, ghostCount.GhostCountInstantiatedOnClient, msg);
        }
    }
}
