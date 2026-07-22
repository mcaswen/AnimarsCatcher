using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Unity.NetCode.Tests;
using Unity.Networking.Transport;
using Unity.Transforms;

namespace Unity.NetCode.PrespawnTests
{
    struct ServerOnlyTag : IComponentData
    {
    }

    internal class LateJoinOptTests : TestWithSceneAsset
    {
        private static void CheckPrespawnArePresent(int numObjects, NetCodeTestWorld testWorld)
        {
            // 进入游戏前应存在指定数量的预生成对象
            using var serverGhosts = testWorld.ServerWorld.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new [] { ComponentType.ReadOnly(typeof(PreSpawnedGhostIndex))},
                Options = EntityQueryOptions.IncludeDisabledEntities
            });
            Assert.AreEqual(numObjects, serverGhosts.CalculateEntityCount());
            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                using var clientGhosts = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new [] { ComponentType.ReadOnly(typeof(PreSpawnedGhostIndex))},
                    Options = EntityQueryOptions.IncludeDisabledEntities
                });
                Assert.AreEqual(numObjects, clientGhosts.CalculateEntityCount());
            }
        }

        private static void CheckComponents(int numObjects, NetCodeTestWorld testWorld)
        {

            Assert.IsFalse(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(SomeData),typeof(Disabled)).IsEmpty);
            Assert.IsFalse(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(SomeDataElement), typeof(Disabled)).IsEmpty);

            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
            {
                Assert.IsFalse(testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(SomeData),typeof(Disabled)).IsEmpty);
                Assert.IsFalse(testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(SomeDataElement),typeof(Disabled)).IsEmpty);
            }
        }

        int FindGhostType(in DynamicBuffer<GhostCollectionPrefab> ghostCollection, GhostType ghostTypeComponent)
        {
            int ghostType;
            for (ghostType = 0; ghostType < ghostCollection.Length; ++ghostType)
            {
                if (ghostCollection[ghostType].GhostType == ghostTypeComponent)
                    break;
            }
            if (ghostType >= ghostCollection.Length)
                return -1;
            return ghostType;
        }

        private void CheckBaselineAreCreated(World world)
        {
            // 进入游戏前应已创建预生成 Ghost 的 Baseline
            var baselines = world.EntityManager.CreateEntityQuery(typeof(PrespawnGhostBaseline));
            Assert.IsFalse(baselines.IsEmptyIgnoreFilter);
            var entities = baselines.ToEntityArray(Allocator.Temp);
            var ghostCollectionEntity = world.EntityManager.CreateEntityQuery(typeof(GhostCollection)).GetSingletonEntity();
            var ghostCollection = world.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
            var ghostPrefabs = world.EntityManager.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);
            var ghostComponentIndex = world.EntityManager.GetBuffer<GhostCollectionComponentIndex>(ghostCollectionEntity);
            var ghostSerializers = world.EntityManager.GetBuffer<GhostComponentSerializer.State>(ghostCollectionEntity);
            Assert.AreEqual(3, ghostCollection.Length);
            foreach (var ent in entities)
            {
                var buffer = world.EntityManager.GetBuffer<PrespawnGhostBaseline>(ent);
                Assert.AreNotEqual(0, buffer.Length);
                // 检查 Baseline 内容符合预期
                unsafe
                {
                    var ghost = world.EntityManager.GetComponentData<GhostInstance>(ent);
                    if (world.IsClient())
                        Assert.AreEqual(-1, ghost.ghostType); // 客户端此时尚未设置 Ghost 类型
                    var ghostType = world.EntityManager.GetComponentData<GhostType>(ent);
                    var idx = FindGhostType(ghostPrefabs, ghostType);
                    Assert.AreNotEqual(-1, idx);
                    // 根据 GhostType 查找集合索引
                    var typeData = ghostCollection[idx];
                    byte* snapshotPtr = (byte*) buffer.GetUnsafeReadOnlyPtr();
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                    var snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(4 + changeMaskUints * 4);
                    for (int cm = 0; cm < changeMaskUints; ++cm)
                        Assert.AreEqual(0, ((uint*)snapshotPtr)[cm]);
                    var offset = snapshotOffset;
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = ghostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        if (ghostSerializers[serializerIdx].ComponentType.IsBuffer)
                        {
                            Assert.AreEqual(16, ((uint*)(snapshotPtr + offset))[0]);
                            Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)), ((uint*)(snapshotPtr + offset))[1]);
                        }
                        offset += GhostComponentSerializer.SizeInSnapshot(ghostSerializers[serializerIdx]);
                    }
                    if (typeData.NumBuffers > 0)
                    {
                        var dynamicDataPtr = snapshotPtr + typeData.SnapshotSize;
                        var bufferSize = ((uint*)dynamicDataPtr)[0];
                        Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)) +
                                        GhostComponentSerializer.SnapshotSizeAligned(16*sizeof(uint)), bufferSize);
                    }
                }
            }
        }

        void ValidateReceivedSnapshotData(World clientWorld)
        {
            using var query = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
            using var collectionQuery = clientWorld.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var entities = query.ToEntityArray(Allocator.Temp);
            var ghostCollectionEntity = collectionQuery.GetSingletonEntity();
            var ghostCollection = clientWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
            var ghostComponentIndex = clientWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(ghostCollectionEntity);
            var ghostSerializers = clientWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(ghostCollectionEntity);

            unsafe
            {
                for (int i = 0; i < entities.Length; ++i)
                {
                    var ghost = clientWorld.EntityManager.GetComponentData<GhostInstance>(entities[i]);
                    Assert.AreNotEqual(-1, ghost.ghostType);
                    var typeData = ghostCollection[ghost.ghostType];
                    var snapshotData = clientWorld.EntityManager.GetComponentData<SnapshotData>(entities[i]);
                    var snapshotBuffer = clientWorld.EntityManager.GetBuffer<SnapshotDataBuffer>(entities[i]);

                    byte* snapshotPtr = (byte*)snapshotBuffer.GetUnsafeReadOnlyPtr();
                    int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                    int snapshotSize = typeData.SnapshotSize;
                    var snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(4 + changeMaskUints*4);
                    snapshotPtr += snapshotSize * snapshotData.LatestIndex;
                    uint* changeMask = (uint*)(snapshotPtr+4);

                    // 检查全部 ChangeMask 均为零
                    for (int cm = 0; cm < changeMaskUints; ++cm)
                        Assert.AreEqual(0, changeMask[cm]);

                    var offset = snapshotOffset;
                    for (int comp = 0; comp < typeData.NumComponents; ++comp)
                    {
                        int serializerIdx = ghostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        if (ghostSerializers[serializerIdx].ComponentType.IsBuffer)
                        {
                            Assert.AreEqual(16, ((uint*)(snapshotPtr + offset))[0]);
                            Assert.AreEqual(0, ((uint*)(snapshotPtr + offset))[1]);
                        }
                        offset += GhostComponentSerializer.SizeInSnapshot(ghostSerializers[serializerIdx]);
                    }
                    if (typeData.NumBuffers > 0)
                    {
                        var dynamicData = clientWorld.EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(entities[i]);
                        byte* dynamicPtr = (byte*) dynamicData.GetUnsafeReadOnlyPtr();
                        var bufferSize = ((uint*) dynamicPtr)[snapshotData.LatestIndex];
                        Assert.AreEqual(GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint)) +
                                        GhostComponentSerializer.SnapshotSizeAligned(16*sizeof(uint)), bufferSize);
                    }
                }
            }
        }

        unsafe void TestRunner(int numClients, int numObjectsPerPrefabs, int numPrefabs,
            uint[] initialDataSize,
            uint[] initialAvgBitsPerEntity,
            uint[] avgBitsPerEntity,
            bool enableFallbackBaseline)
        {
            var numObjects = numObjectsPerPrefabs * numPrefabs;
            var uncompressed = new uint[numClients];
            var totalDataReceived = new uint[numClients];
            var numReceived = new uint[numClients];
            using (var testWorld = new NetCodeTestWorld())
            {
                // 创建包含多个对象的 SubScene
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);
                var mode = enableFallbackBaseline ? "WithBaseline" : "NoBaseline";

                // 流式加载 SubScene
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect();
                CheckPrespawnArePresent(numObjects, testWorld);
                CheckComponents(numObjects, testWorld);
                // 移除 Baseline 以禁用预生成优化
                if (!enableFallbackBaseline)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp).WithPresent<PrespawnGhostBaseline>().WithOptions(EntityQueryOptions.IncludeDisabledEntities);
                    using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(builder);
                    Assert.AreEqual(numObjects, serverQuery.CalculateEntityCount(), "Sanity! Ensure it'll be removed!");
                    testWorld.ServerWorld.EntityManager.RemoveComponent<PrespawnGhostBaseline>(serverQuery);
                    Assert.AreEqual(0, serverQuery.CalculateEntityCount(), "Sanity! Ensure it has been removed!");
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        using var clientQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(builder);
                        Assert.AreEqual(numObjects, clientQuery.CalculateEntityCount(), "Sanity! Ensure it'll be removed!");
                        testWorld.ClientWorlds[i].EntityManager.RemoveComponent<PrespawnGhostBaseline>(clientQuery);
                        Assert.AreEqual(0, clientQuery.CalculateEntityCount(), "Sanity! Ensure it has been removed!");
                    }
                }

                testWorld.GoInGame();

                var connections = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrespawnSectionAck>()).ToEntityArray(Allocator.Temp);
                for (int i = 0; i< 32; ++i)
                {
                    testWorld.Tick();
                    bool allSceneAcked = false;
                    foreach (var connection in (connections))
                    {
                        var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<PrespawnSectionAck>(connection);
                        allSceneAcked |= buffer.Length > 0;
                    }

                    if (allSceneAcked)
                        break;
                }
                // 从这里开始服务器会发送预生成 Ghost
                uint newObjects = 0;
                uint totalSceneData = 0;
                for(int tick=0;tick<32;++tick)
                {
                    testWorld.Tick();
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        var netStats = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i])).MainStatsWrite;
                        totalSceneData += netStats.PerGhostTypeStatsListRefRW.ElementAt(0).SizeInBits;
                        for (int gtype = 0; gtype < numPrefabs; ++gtype) // 统计列表首项是 NetCode 自有的预生成场景列表 Ghost，因此长度比 numPrefabs 多一
                        {
                            numReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).EntityCount;
                            totalDataReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).SizeInBits;
                            uncompressed[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount;
                        }
                        if(enableFallbackBaseline)
                            ValidateReceivedSnapshotData(testWorld.ClientWorlds[i]);

                        // 未压缩对象总数为零表示没有收到新的 Ghost
                        // 启用 Fallback Baseline 时该值应始终为零
                        newObjects = 0;
                        for (int gtype = 0; gtype < numPrefabs; ++gtype)
                            newObjects += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount;
                    }

                    if (newObjects == 0 && numReceived[0] >= numObjects)
                        break;
                }

                // 记录各客户端初次加入时的接收数据量
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    // 保存初次加入的数据量和每实体平均位数
                    initialAvgBitsPerEntity[i] = totalDataReceived[i] / numReceived[i];
                    initialDataSize[i] = totalDataReceived[i];
                    Debug.Log($"{mode} Client {i} Initial Join: {numReceived[i]} - {totalDataReceived[i]} - {initialAvgBitsPerEntity[i]}");
                }

                // 后续 Tick 中所有实体保持静止，因此实体字段数据应降为零位
                // 只发送 Header、ChangeMask、Ghost ID 和 Baseline，大小应基本稳定
                // Baseline 与 Tick 编码仍可能造成少量波动
                for (int tick = 0; tick < 32; ++tick)
                {
                    testWorld.Tick();
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        var netStats = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i])).MainStatsWrite;

                        for (int gtype = 0; gtype < numPrefabs; ++gtype)
                        {
                            Assert.AreEqual(0, netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).UncompressedCount); // 没有新对象
                            numReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).EntityCount;
                            totalDataReceived[i] += netStats.PerGhostTypeStatsListRefRW.ElementAt(gtype + 1).SizeInBits;
                        }
                        ValidateReceivedSnapshotData(testWorld.ClientWorlds[i]);
                    }
                }

                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    avgBitsPerEntity[i] = totalDataReceived[i] / numReceived[i];
                    Debug.Log($"{mode} Client {i} At Regime: {numReceived[i]} - {totalDataReceived[i]} - {avgBitsPerEntity[i]}");
                }
            }
        }

        [Test]
        public void DataSentWithFallbackBaselineAreLessThanWithout()
        {
            const int numObjectsPerPrefab = 32;
            const int numClients = 1;
            const int numPrefabs = 4;

            // 创建包含多种 Prefab 类型的场景
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "Simple", typeof(GhostAuthoringComponent));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab3 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithBuffer", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            GameObject withChildren = new GameObject("WithChildren", typeof(GhostAuthoringComponent));
            GameObject children1 = new GameObject("Child1", typeof(SomeDataAuthoring));
            GameObject children2 = new GameObject("Child2", typeof(SomeDataAuthoring));
            children1.transform.parent = withChildren.transform;
            children2.transform.parent = withChildren.transform;
            var prefab4 = SubSceneHelper.CreatePrefab(ScenePath, withChildren);

            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab1,
                prefab2,
                prefab3,
                prefab4,
            }, numObjectsPerPrefab);
            var initialDataSize = new uint[numClients];
            var initialAvgBitsPerEntity = new uint[numClients];
            var averageEntityBits = new uint[numClients];
            TestRunner(numClients, numObjectsPerPrefab, numPrefabs, initialDataSize, initialAvgBitsPerEntity, averageEntityBits, false);
            var initialDataSizeWithFallback = new uint[numClients];
            var initialAvgBitsPerEntityWithFallback = new uint[numClients];
            var averageEntityBitsWithFallback = new uint[numClients];
            TestRunner(numClients, numObjectsPerPrefab, numPrefabs, initialDataSizeWithFallback,
                initialAvgBitsPerEntityWithFallback, averageEntityBitsWithFallback, true);
            for (int i = 0; i < numClients; ++i)
            {
                Assert.LessOrEqual(initialDataSizeWithFallback[i], initialDataSize[i]);
                Assert.LessOrEqual(initialAvgBitsPerEntityWithFallback[i], initialAvgBitsPerEntity[i]);
                // 优化后的初始平均大小不应高于未优化结果
                Assert.LessOrEqual(initialAvgBitsPerEntityWithFallback[i], averageEntityBits[i]);
                Assert.LessOrEqual(averageEntityBitsWithFallback[i], averageEntityBits[i]);
            }

        }

        [Test]
        public void Test_BaselineAreCreated()
        {
            // 创建包含多种 Prefab 类型的场景
            const int numObjects = 10;
            var prefab1 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var prefab2 = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithBuffer", typeof(GhostAuthoringComponent),
                typeof(SomeDataElementAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
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
                CheckPrespawnArePresent(numObjects*2, testWorld);
                CheckComponents(numObjects*2, testWorld);
                testWorld.GoInGame();
                // 再推进若干 Tick 以接收和处理 Prefab 并初始化 Baseline
                for(int i=0;i<2;++i)
                    testWorld.Tick();
                CheckBaselineAreCreated(testWorld.ServerWorld);
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    CheckBaselineAreCreated(testWorld.ClientWorlds[i]);
                }
            }
        }

        /// <param name="keepSnapshotHistoryOnStructuralChange">
        /// 确保覆盖 <see cref="GhostChunkSerializer.UpdateChunkHistory"/> 的全部细节
        /// 为该 Ghost 添加 DynamicBuffer 也会强制将 <see cref="keepSnapshotHistoryOnStructuralChange"/> 设为 false
        /// </param>
        /// <param name="latencyProfile">用于在不同网络条件下验证静态优化</param>
        [Test(Description = "Tests only the common set of static-optimized, prespawn ghost replication cases.")]
        public unsafe void UsingStaticOptimizationServerDoesNotSendData([Values]bool keepSnapshotHistoryOnStructuralChange, [Values] NetCodeTestLatencyProfile latencyProfile)
        {
            const int numObjects = 10;
            // 创建静态优化的预生成 Ghost 场景
            var prefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            prefab.GetComponent<GhostAuthoringComponent>().OptimizationMode = GhostOptimizationMode.Static;
            PrefabUtility.SavePrefabAsset(prefab);

            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "LateJoinTest");
            SubSceneHelper.CreateSubSceneWithPrefabs(parentScene, ScenePath, "subscene", new[]
            {
                prefab,
            }, numObjects);

            using (var testWorld = new NetCodeTestWorld())
            {
                // 创建包含多个对象的 SubScene
                testWorld.Bootstrap(true);
                testWorld.SetTestLatencyProfile(latencyProfile);
                testWorld.CreateWorlds(true, 1);
                testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.KeepSnapshotHistoryOnStructuralChange = keepSnapshotHistoryOnStructuralChange;

                // 流式加载 SubScene
                SubSceneHelper.LoadSubSceneInWorlds(testWorld);
                testWorld.Connect(maxSteps:16);
                CheckPrespawnArePresent(numObjects, testWorld);
                testWorld.GoInGame();

                uint uncompressed = 0;
                uint totalDataReceived = 0;
                uint numReceived = 0;
                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                for (int tick = 0; tick < 16; ++tick)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;

                    // 跳过首个 Ghost 类型，它对应 SubScene 列表
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                testWorld.TryLogPacket("\nTEST-CASE: Expect NO snapshot updates, as prespawns 'waking up' (i.e. becoming enabled) doesn't require us to" +
                                       " send individual ghosts (as they wake up as a result of their sub-scene being acked, and their prespawns being mapped, which happens via RPC IIRC).");
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;

                var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                var serverGhosts = serverQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
                var serverEntities = serverQuery.ToEntityArray(Allocator.Temp);
                var ghostCollectionEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostCollection)).GetSingletonEntity();
                Span<SomeData?> baselineSomeDataValues = stackalloc SomeData?[numObjects];
                for (int i = 0; i < numObjects; ++i)
                    baselineSomeDataValues[i] = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After prespawns enable themselves.");

                testWorld.TryLogPacket("\nTEST-CASE: Create a FALSE POSITIVE write, to test out the zero change optimization for prespawn baselines:\n");
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[5]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[5], data);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;

                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After FALSE-POSITIVE write.");

                testWorld.TryLogPacket("\nTEST-CASE: Make a structural change and verify that entities are STILL not sent (no changes in respect to the 0 baselines)\n");
                for (int i = 8; i < 10; ++i)
                {
                    // 添加仅服务器存在的 Tag 会造成服务器结构变化
                    // 客户端仍应将这些实体视为未变化
                    testWorld.ServerWorld.EntityManager.AddComponent<ServerOnlyTag>(serverEntities[i]);
                }

                // 此时实体分布在以下两个 Chunk
                // Chunk 1    实体
                //              0 1 2 3 4 5 6 7
                // 已变化:      否 否 否 否 否 否 否 否
                // 第二个 Chunk
                //              8 9
                // 已变化:      否 否

                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 8,9 changed chunk");
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);

                testWorld.TryLogPacket("\nTEST-CASE: ACTUALLY change some components for entities 0,1,2\n");
                for (int i = 0; i < numObjects; ++i)
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                    if (i < 3)
                    {
                        data.Value += 100;
                        testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], data);
                    }
                }

                // 从后续 Tick 开始发送真实字段变化
                for (int i = 0; i < 32; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                // 即使只修改三个实体，客户端仍会接收整个八实体 Chunk 的增量压缩数据，但只接收一次
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(8, numReceived);
                else Assert.IsTrue(numReceived >= 8 && numReceived % 8 == 0, $"numReceived:{numReceived}");
                Assert.AreNotEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After SomeData change on 0,1,2");

                {
                    var ghostCollection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollectionEntity);
                    // 检查同一 Chunk 中其他实体的 ChangeMask 仍为零
                    for (int i = 3; i < 8; ++i)
                    {
                        var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                        var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                        var snapshotData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(ent);
                        var snapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(ent);
                        var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                        var typeData = ghostCollection[ghostType];
                        int snapshotSize = typeData.SnapshotSize;
                        unsafe
                        {
                            byte* snapshotPtr = (byte*) snapshotBuffer.GetUnsafeReadOnlyPtr();
                            snapshotPtr += snapshotSize * snapshotData.LatestIndex;
                            int changeMaskUints = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                            uint* changeMask = (uint*) (snapshotPtr + 4);

                            for (int cm = 0; cm < changeMaskUints; ++cm)
                                Assert.AreEqual(0, changeMask[cm]);
                        }
                    }
                }
                // 实体 8 和 9 仍未收到 Ghost 类型初始化数据
                for (int i = 8; i < 10; ++i)
                {
                    var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                    var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                    var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                    Assert.AreEqual(-1, ghostType);
                }

                testWorld.TryLogPacket("\nTEST-CASE: From here on I should NOT receive any ghosts again (since they're zero-change, as the zero-change has been acked)\n");
                numReceived = 0;
                totalDataReceived = 0;
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }

                    Assert.AreEqual(0, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreEqual(0, totalDataReceived);
                }

                testWorld.TryLogPacket("\nTEST-CASE: Now make a structural change WITHOUT any GhostField changes,\n");
                // 验证结构变化后实体不会再次发送，因为 GhostField 相对 Baseline 仍为 ZeroChange
                // 若 keepSnapshotHistoryOnStructuralChange 为 false 导致历史未正确复制则例外
                for (int i = 3; i < 6; ++i)
                {
                    testWorld.ServerWorld.EntityManager.AddComponent<ServerOnlyTag>(serverEntities[i]);
                }

                // 此时实体重新分布在以下两个 Chunk
                // Chunk 1    实体
                //              0 1 2 6 7
                // 已变化:      是 是 是 否 否
                // 第二个 Chunk
                //              3 4 5 8 9
                // 已变化:      否 否 否 否 否
                //
                // 即使实体 3、4、5 从第一个 Chunk 移出后版本发生变化，也不应接收第二个 Chunk
                // 因为相对 Fallback Baseline 的实际字段变化均为零
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                if (keepSnapshotHistoryOnStructuralChange)
                {
                    Assert.AreEqual(0, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreEqual(0, totalDataReceived);
                }
                else if (latencyProfile == NetCodeTestLatencyProfile.None)
                {
                    Assert.AreEqual(5, numReceived);
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreNotEqual(0, totalDataReceived);
                }
                else
                {
                    Assert.IsTrue(numReceived >= 5 && numReceived % 5 == 0, $"numReceived:{numReceived}");
                    Assert.AreEqual(0, uncompressed);
                    Assert.AreNotEqual(0, totalDataReceived);
                }

                // 实体 8 和 9 仍未收到 Ghost 类型初始化数据
                for (int i = 8; i < 10; ++i)
                {
                    var ghost = new SpawnedGhost {ghostId = serverGhosts[i].ghostId, spawnTick = serverGhosts[i].spawnTick};
                    var ent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[ghost];
                    var ghostType = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(ent).ghostType;
                    Assert.AreEqual(-1, ghostType);
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4,5 changed chunk.");

                testWorld.TryLogPacket("\nTEST-CASE: Change 3,4 in the second chunk:\n");
                for (int i = 3; i < 5; ++i)
                {
                    var data = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntities[i]);
                    data.Value += 100;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], data);
                }
                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                // 该字段变化会从下一 Tick 开始发送
                for(int i = 0; i < 8; i++)
                {
                    testWorld.Tick();

                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                // 变化所在 Chunk 包含五个实体
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(5, numReceived);
                else Assert.IsTrue(numReceived >= 5 && numReceived % 5 == 0, $"numReceived:{numReceived}");
                Assert.AreNotEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4 updated GhostField data.");

                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;

                testWorld.TryLogPacket("\nTEST-CASE: Expect no changes now.\n");
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, totalDataReceived);
                Assert.AreEqual(0, uncompressed);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After 3,4 update arrives - expect no more updates.");

                testWorld.TryLogPacket("\nTEST-CASE: EXTREMELY esoteric: Prespawn 3 is currently NOT matching their prespawn baseline, and NOT in their prespawn chunk.");
                testWorld.TryLogPacket("If we move prespawn 3 BACK to their prespawn chunk, AND revert their GhostField changes, will the GhostChunkSerializer understand that it needs to send said change?\n");
                testWorld.ServerWorld.EntityManager.RemoveComponent<ServerOnlyTag>(serverEntities[3]);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[3], baselineSomeDataValues[3].Value);

                numReceived = 0;
                totalDataReceived = 0;
                uncompressed = 0;
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }

                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After returning changed ghosts to their original chunk, and matching their GhostField data back to the prespawn values.");
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(6, numReceived);
                else Assert.IsTrue(numReceived >= 6 && numReceived % 6 == 0, $"numReceived:{numReceived}");
                Assert.AreEqual(0, uncompressed);
                Assert.AreNotEqual(0, totalDataReceived);

                testWorld.TryLogPacket("\nTEST-CASE: Again expect no changes.\n");
                numReceived = 0;
                uncompressed = 0;
                totalDataReceived = 0;
                for (int tick = 0; tick < 8; tick++)
                    testWorld.Tick();
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "Expect no more changes.");

                testWorld.TryLogPacket("\nTEST-CASE: Revert all other SomeData back to their pre-spawn values, ensure it works:\n");
                for (int i = 0; i < numObjects; i++)
                {
                    testWorld.ServerWorld.EntityManager.RemoveComponent<ServerOnlyTag>(serverEntities[i]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntities[i], baselineSomeDataValues[i].Value);
                }
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                if(latencyProfile == NetCodeTestLatencyProfile.None)
                    Assert.AreEqual(10, numReceived);
                else Assert.IsTrue(numReceived >= 10 && numReceived % 10 == 0, $"numReceived:{numReceived}");
                Assert.AreEqual(0, uncompressed);
                Assert.AreNotEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "After returning changed ghosts to their original is completed - expect no more changes.");

                testWorld.TryLogPacket("\nTEST-CASE: Again, expect no more changes:\n");
                numReceived = 0;
                uncompressed = 0;
                totalDataReceived = 0;
                for (int tick = 0; tick < 8; tick++)
                {
                    testWorld.Tick();
                    var netStats = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostStatsSnapshotSingleton>(testWorld.TryGetSingletonEntity<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0])).MainStatsWrite;
                    if (netStats.PerGhostTypeStatsListRefRW.Length > 1)
                    {
                        numReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount;
                        totalDataReceived += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits;
                        uncompressed += netStats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount;
                    }
                }
                Assert.AreEqual(0, numReceived);
                Assert.AreEqual(0, uncompressed);
                Assert.AreEqual(0, totalDataReceived);
                VerifyReplicatedValues(numObjects, testWorld, serverEntities, recvGhostMapSingleton, "Final expect no changes.");
            }
        }

        private void VerifyReplicatedValues(int numObjects, NetCodeTestWorld testWorld, NativeArray<Entity> serverEntities, Entity recvGhostMapSingleton, string context)
        {
            string s = context;
            testWorld.TryLogPacket($"\n\nTEST-VerifyReplicatedValues:{context}\n");
            for (int i = 0; i < numObjects; ++i)
            {
                var serverEntity = serverEntities[i];
                var serverTrans = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEntity);
                var serverSomeData = testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntity);
                var serverGhost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
                var clientEntity = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value[serverGhost];
                var clientTrans = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEntity);
                var clientSomeData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SomeData>(clientEntity);
                s += $"\n\t[{i}] GID:{serverGhost.ghostId}\n\tServer[{serverEntity.ToString()} in chunk:{testWorld.ServerWorld.EntityManager.GetChunk(serverEntity).SequenceNumber}, LocalTransform({serverTrans.ToString()}), SomeData:{serverSomeData.Value}]\n\tClient[{clientEntity.ToString()} in chunk:{testWorld.ClientWorlds[0].EntityManager.GetChunk(clientEntity).SequenceNumber}, LocalTransform({clientTrans.ToString()}), SomeData:{clientSomeData.Value}]";
                ApproximatelyEqual(serverTrans.Position, clientTrans.Position, $"[{i}]LocalTransform.Position {context} GID:{serverGhost.ghostId}", 0.0001f);
                ApproximatelyEqual(math.Euler(serverTrans.Rotation), math.Euler(clientTrans.Rotation), $"math.Euler([{i}]LocalTransform.Rotation) {context} GID:{serverGhost.ghostId}", 0.04f);
                ApproximatelyEqual(serverTrans.Scale, clientTrans.Scale, $"[{i}]LocalTransform.Scale {context} GID:{serverGhost.ghostId}", 0.0001f);
                Assert.AreEqual(serverSomeData.Value, clientSomeData.Value, $"[{i}].SomeData.Value {context} GID:{serverGhost.ghostId}");
            }
            Debug.Log(s);
        }

        private void ApproximatelyEqual(float3 server, float3 client, string context, float tolerance)
        {
            var delta = server - client;
            var deltaUnits = math.length(delta);
            Assert.IsTrue(deltaUnits <= tolerance, $"{context}\nserver:{server} - client:{client} = {delta}\n{deltaUnits} <= {tolerance}");
        }
    }
}
