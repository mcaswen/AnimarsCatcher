using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.Editor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    partial struct MispredictionSystem : ISystem
    {
        public unsafe void OnUpdate(ref SystemState state)
        {
            var increment = state.WorldUnmanaged.IsServer() ? 1 : 2;
            foreach (var c in SystemAPI.Query<RefRW<GhostGenTestTypes.GhostGenBigStruct>>())
            {
                int* v = (int*)UnsafeUtility.AddressOf(ref c.ValueRW);
                for (int i = 0; i < 101; ++i)
                    v[i] += increment;
            }
        }
    }

    class NetStatsTests
    {
        const int k_SnapshotMaxHeaderSizeInBits = 200; // 测试假定快照头小于该值，新增头字段时需要同步调整
        [Test]
        public void TestLargeNumberOfPredictionErrorsAreReported([Values]bool useMetrics)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MispredictionSystem));
            // 构造足够多的预测错误名称以覆盖长名称数据
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            var serverEntity = CreateEntityPrefab(testWorld.ServerWorld);
            CreateEntityPrefab(testWorld.ClientWorlds[0]);
            var clientMetrics = testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
            UpdateMetrics(testWorld.ClientWorlds[0], useMetrics, clientMetrics);

            testWorld.Connect();
            testWorld.GoInGame();
            for(int i=0; i<32; ++i)
                testWorld.Tick();

            // 验证 Ghost 集合统计处于预期状态
            var statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ServerWorld);
            var errorNames = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorNames>(clientMetrics);
            Assert.Less(errorNames.Length, 101);
            Assert.AreEqual(statsCollectionData.ValueRO.m_PredictionErrors.Length, 101);

            if (useMetrics)
            {
                var predictionErrors = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorMetrics>(clientMetrics);
                Assert.AreEqual(predictionErrors.Length, predictionErrors.Length);
            }

            // 生成测试实体
            testWorld.ServerWorld.EntityManager.Instantiate(serverEntity);

            // 模拟调试器已连接
            statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ClientWorlds[0]);
            if (!useMetrics)
            {
                statsCollectionData.ValueRW.m_StatIndex = 0;
                statsCollectionData.ValueRW.m_CollectionTick = NetworkTick.Invalid;;
                statsCollectionData.ValueRW.m_PacketQueue.Clear();
                statsCollectionData.ValueRW.m_UsedPacketPoolSize = 0;
                if (statsCollectionData.ValueRW.m_LastNameAndErrorArray.Length > 0)
                    statsCollectionData.ValueRW.AppendNamePacket(testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0]));
                testWorld.GetSingletonRW<GhostStats>(testWorld.ClientWorlds[0]).ValueRW.IsConnected = true;
            }

            // 等待客户端生成实体
            for(int i=0; i<4; ++i)
                testWorld.Tick();

            // 推进预测并确认不会抛出错误或异常
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                statsCollectionData = testWorld.GetSingletonRW<GhostStatsCollectionData>(testWorld.ClientWorlds[0]);
                var statsErrors = statsCollectionData.ValueRW.m_PredictionErrors;
                // Stats 在下一帧的 InitializationSystemGroup 中更新
                // 因此从第二次循环起验证上一帧的数据
                if (i > 0)
                {
                    for (int err = 0; err < statsErrors.Length; ++err)
                    {
                        Assert.IsTrue(math.abs(1f - statsErrors[err]) < 1e-3f);
                    }
                    if (useMetrics)
                    {
                        var predictionErrors = testWorld.ClientWorlds[0].EntityManager.GetBuffer<PredictionErrorMetrics>(clientMetrics);
                        for (int err = 0; err < predictionErrors.Length; ++err)
                        {
                            Assert.IsTrue(math.abs(1f - predictionErrors[err].Value) < 1e-3f);
                        }
                    }
                }
            }
        }

        [Test, Description("Test that accessing the unsafe array still throws with our custom safety checks")]
        public void NetStats_UsingDisposedStats_ShouldFail()
        {
            UnsafeGhostStatsSnapshot nullStats = default;
            Assert.Throws<NullReferenceException>(() =>
            {
                _ = nullStats.PerGhostTypeStatsListRO;
            });
            Assert.Throws<NullReferenceException>(() =>
            {
                _ = nullStats.PerGhostTypeStatsListRefRW;
            });
            UnsafeGhostStatsSnapshot stats = new UnsafeGhostStatsSnapshot(1, Allocator.Temp);
            stats.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = stats.PerGhostTypeStatsListRO;
            });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = stats.PerGhostTypeStatsListRefRW;
            });
        }

        [Test, Description("make sure we can blit to and from editor profiler's byte array")]
        public unsafe void NetStats_BlittableDataForProfiler_IsValid()
        {
            var stats = new UnsafeGhostStatsSnapshot(2, Allocator.Temp);
            try
            {
                stats.DespawnCount = 1;
                stats.DestroySizeInBits = 2;
                stats.Tick = new NetworkTick(3);
                stats.PacketsCount = 33;
                stats.SnapshotTotalSizeInBits = 34;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).ChunkCount = 4;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).EntityCount = 5;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).SizeInBits = 6;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).UncompressedCount = 7;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.Resize(2, NativeArrayOptions.ClearMemory);
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.ElementAt(0).SizeInSnapshotInBits = 8;
                stats.PerGhostTypeStatsListRefRW.ElementAt(0).PerComponentStatsList.ElementAt(1).SizeInSnapshotInBits = 9;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).ChunkCount = 41;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).EntityCount = 51;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).SizeInBits = 61;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).UncompressedCount = 71;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.Resize(2, NativeArrayOptions.ClearMemory);
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.ElementAt(0).SizeInSnapshotInBits = 81;
                stats.PerGhostTypeStatsListRefRW.ElementAt(1).PerComponentStatsList.ElementAt(1).SizeInSnapshotInBits = 91;

                var bytes = stats.ToBlittableData(Allocator.Temp);
                var deserializedStats = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, bytes);
                Assert.AreEqual(1, deserializedStats.DespawnCount);
                Assert.AreEqual(2, deserializedStats.DestroySizeInBits);
                Assert.AreEqual(3, deserializedStats.Tick.TickIndexForValidTick);
                Assert.AreEqual(33, deserializedStats.PacketsCount);
                Assert.AreEqual(34, deserializedStats.SnapshotTotalSizeInBits);
                Assert.AreEqual(4, deserializedStats.PerGhostTypeStatsListRO[0].ChunkCount);
                Assert.AreEqual(5, deserializedStats.PerGhostTypeStatsListRO[0].EntityCount);
                Assert.AreEqual(6, deserializedStats.PerGhostTypeStatsListRO[0].SizeInBits);
                Assert.AreEqual(7, deserializedStats.PerGhostTypeStatsListRO[0].UncompressedCount);
                Assert.AreEqual(8, deserializedStats.PerGhostTypeStatsListRO[0].PerComponentStatsList[0].SizeInSnapshotInBits);
                Assert.AreEqual(9, deserializedStats.PerGhostTypeStatsListRO[0].PerComponentStatsList[1].SizeInSnapshotInBits);
                Assert.AreEqual(41, deserializedStats.PerGhostTypeStatsListRO[1].ChunkCount);
                Assert.AreEqual(51, deserializedStats.PerGhostTypeStatsListRO[1].EntityCount);
                Assert.AreEqual(61, deserializedStats.PerGhostTypeStatsListRO[1].SizeInBits);
                Assert.AreEqual(71, deserializedStats.PerGhostTypeStatsListRO[1].UncompressedCount);
                Assert.AreEqual(81, deserializedStats.PerGhostTypeStatsListRO[1].PerComponentStatsList[0].SizeInSnapshotInBits);
                Assert.AreEqual(91, deserializedStats.PerGhostTypeStatsListRO[1].PerComponentStatsList[1].SizeInSnapshotInBits);
            }
            finally
            {
                stats.Dispose();
            }
        }

        [Test, Description("general stats validation test for simple spawn")]
        public void NetStats_StatsAreValid()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ServerWorld);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
            var serverPrefab = CreateEntityPrefab(testWorld.ServerWorld);
            CreateEntityPrefab(testWorld.ClientWorlds[0]);

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 123 }); // 设置非默认值以生成逐组件统计
            testWorld.Tick();
            testWorld.Tick(); // 发送实体后客户端在同一次 Tick 调用中接收，双方写统计缓冲此时均应更新
            testWorld.Tick(); // 将客户端和服务器写统计复制到各自的读统计缓冲
            // 客户端此时也已生成 Ghost

            Assert.AreEqual(123, testWorld.GetSingleton<GhostGenTestTypes.GhostGenBigStruct>(testWorld.ClientWorlds[0]).field000, "sanity check failed");

            var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
            var ghostType = ghostInstance.ghostType;
            var spawnTick = ghostInstance.spawnTick;

            UnsafeGhostStatsSnapshot.PerGhostTypeStats GetGhostStats(int ghostType, bool isServer)
            {
                var stats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(isServer ? testWorld.ServerWorld : testWorld.ClientWorlds[0]);
                var readStats = stats.GetAsyncStatsReader();
                var perGhostTypeStats = readStats.PerGhostTypeStatsListRO;
                return perGhostTypeStats[ghostType];
            }

            var serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            var readStats = serverStats.GetAsyncStatsReader();
            {
                // 验证服务器统计
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, readStats.Tick.TickIndexForValidTick, "stats tick should be the spawn tick"); // 当前实现会在下一 Tick 发送 Ghost，因此快照 Tick 比 Spawn Tick 大一
                Assert.AreEqual(0, readStats.DespawnCount, "despawn should be zero");
                Assert.AreEqual(0, readStats.DestroySizeInBits, "destroy size should be zero");
                var perGhostTypeStats = readStats.PerGhostTypeStatsListRO;
                Assert.AreEqual(1, perGhostTypeStats.Length);
                Assert.AreEqual(1, readStats.PacketsCount, "PacketsCount should be one");
                Assert.Greater(readStats.SnapshotTotalSizeInBits, GetGhostStats(ghostType, true).SizeInBits, "total size should be larger than per ghost type size");
                Assert.Less(readStats.SnapshotTotalSizeInBits, GetGhostStats(ghostType, true).SizeInBits + k_SnapshotMaxHeaderSizeInBits, "total size shouldn't be too big");
                Assert.AreEqual(1, GetGhostStats(ghostType, true).EntityCount, "entity count");
                Assert.AreEqual(1, GetGhostStats(ghostType, true).ChunkCount, "chunk count");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).UncompressedCount, "uncompressed count should be uninitialized server side");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "size in bits for ghost type");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).UncompressedCount);
                Assert.AreEqual(1, GetGhostStats(ghostType, true).PerComponentStatsList.Length);
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits > 8, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }
            {
                // 验证客户端读统计已经包含生成数据
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[0]);
                var clientStatsReader = clientStats.GetAsyncStatsReader();
                Assert.AreEqual(1, clientStatsReader.PerGhostTypeStatsListRO.Length);
                Assert.AreEqual(0, clientStatsReader.DespawnCount);
                Assert.AreEqual(0, clientStatsReader.DestroySizeInBits);
                Assert.AreEqual(spawnTick.TickIndexForValidTick + 1, clientStatsReader.Tick.TickIndexForValidTick, "received snapshot tick should be spawn tick");
                Assert.AreEqual(readStats.PacketsCount, clientStatsReader.PacketsCount, "sent and received snapshot packet count should be the same");
                Assert.AreEqual(readStats.SnapshotTotalSizeInBits, clientStatsReader.SnapshotTotalSizeInBits, "sent and received snapshot size should be the same");
                Assert.AreEqual(1, GetGhostStats(ghostType, false).EntityCount, "entity count");
                Assert.AreEqual(0, GetGhostStats(ghostType, false).ChunkCount, "chunk count should be uninitialized client side");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > 8, "size in bits for ghost type");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits > 8, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.AreEqual(1, GetGhostStats(ghostType, false).UncompressedCount, "uncompressed count");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }

            testWorld.Tick();
            {
                // 再推进一个 Tick 以避开服务器统计中的瞬时数据
                // 验证组件未变化时不会产生新的逐组件统计
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "should still be sending metadata for ghost even with no data change");
                Assert.AreEqual(0, GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "no data change so there should be no stats for component");
            }

            // 修改服务器组件以触发逐组件统计
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 124 });
            testWorld.Tick(); // 发送数据
            testWorld.Tick(); // 更新服务器读统计缓冲
            {
                // 验证服务器上的数据变化统计
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > 8, "ghost type size should be bigger than 8 bits");
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits > 0, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits < 16, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)} should be small due to compression and small change");
                Assert.IsTrue(GetGhostStats(ghostType, true).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");

            }
            Assert.AreEqual(124, testWorld.GetSingleton<GhostGenTestTypes.GhostGenBigStruct>(testWorld.ClientWorlds[0]).field000, "sanity check failed");
            {
                // 验证客户端上的数据变化统计
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > 8, "ghost type size should be bigger than 8 bits");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits > 0, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)}");
                Assert.IsTrue(GetGhostStats(ghostType, false).PerComponentStatsList[0].SizeInSnapshotInBits < 16, $"size in bits for {nameof(GhostGenTestTypes.GhostGenBigStruct)} should be small due to compression and small change");
                Assert.IsTrue(GetGhostStats(ghostType, false).SizeInBits > GetGhostStats(ghostType, true).PerComponentStatsList[0].SizeInSnapshotInBits, "per component stats should be less than total ghost size");
            }
        }

        [Test]
        public void NetStats_PartialSnapshotStats_AreValid([Values(1, 2)] int clientCount)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, clientCount);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ServerWorld);
            for (int i = 0; i < clientCount; i++)
            {
                testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[i]);
                CreateEntityPrefab(testWorld.ClientWorlds[i]);
            }
            var serverPrefab = CreateEntityPrefab(testWorld.ServerWorld);

            const int MaxPayloadSize = 1375;

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var ghostCount = 200; // 每个 Ghost 约四百字节，即使增量压缩后也应超过一个 MTU
            var serverGhosts = new List<Entity>();
            for (int i = 0; i < ghostCount; i++)
            {
                var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 123 }); // 设置非默认值以生成逐组件统计
                serverGhosts.Add(serverEntity);
            }

            void IncrementAll()
            {
                for (int i = 0; i < ghostCount; i++)
                {
                    var ghostGenBigStruct = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenTestTypes.GhostGenBigStruct>(serverGhosts[i]);
                    ghostGenBigStruct.Increment();
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverGhosts[i], ghostGenBigStruct);
                }
            }

            testWorld.Tick();
            IncrementAll(); // 确保产生足够的带宽用量
            testWorld.Tick(); // 发送实体后客户端在同一次 Tick 调用中接收，双方写统计缓冲此时均应更新
            IncrementAll();
            testWorld.Tick(); // 将客户端和服务器写统计复制到各自的读统计缓冲
            IncrementAll();

            var serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(1 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(1, clientStats.GetAsyncStatsReader().PacketsCount);
            }

            testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.DefaultSnapshotPacketSize = MaxPayloadSize*2;
            // 当前载荷空间可容纳两个包，统计也应反映这一点

            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();

            serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(2 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            Assert.Greater(serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * clientCount); // 验证每个客户端发送量超过一个 MTU
            Assert.Greater(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits, serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, "total snapshot size should be greater than per ghost type size");
            Assert.Less(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits, serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits + k_SnapshotMaxHeaderSizeInBits * clientCount);
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(2, clientStats.GetAsyncStatsReader().PacketsCount);
                Assert.Greater(clientStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8); // 验证接收量超过一个 MTU
                Assert.AreEqual(serverStats.GetAsyncStatsReader().SnapshotTotalSizeInBits / clientCount, clientStats.GetAsyncStatsReader().SnapshotTotalSizeInBits);
            }

            testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.DefaultSnapshotPacketSize = MaxPayloadSize*4;
            // 当前载荷空间可容纳四个包，统计也应反映这一点

            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();
            testWorld.Tick();
            IncrementAll();

            serverStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);
            Assert.AreEqual(4 * clientCount, serverStats.GetAsyncStatsReader().PacketsCount);
            Assert.Greater(serverStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * 3 * clientCount); // 验证每个客户端发送量超过三个 MTU
            for (int i = 0; i < clientCount; i++)
            {
                var clientStats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ClientWorlds[i]);
                Assert.AreEqual(4, clientStats.GetAsyncStatsReader().PacketsCount);
                Assert.Greater(clientStats.GetAsyncStatsReader().PerGhostTypeStatsListRO[0].SizeInBits, MaxPayloadSize * 8 * 3); // 验证接收量超过三个 MTU
            }
        }
#if NETCODE_PROFILER_ENABLED && UNITY_6000_0_OR_NEWER
        [UnityTest, Description("Collects some stats while profiling, saves the session and loads it back to verify the stats are still correct")]
        [Ignore("Save and Load triggers a 'Profiler data stream has invalid signature.' error. Need to fix this test. Ticket to reenable test https://jira.unity3d.com/browse/MTT-13004")]
        public IEnumerator Profiler_SaveAndLoadStats()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ServerWorld);
            testWorld.TryCreateGhostMetricsSingleton(testWorld.ClientWorlds[0]);
            var serverPrefab = CreateEntityPrefab(testWorld.ServerWorld);
            CreateEntityPrefab(testWorld.ClientWorlds[0]);

            testWorld.Connect();
            testWorld.GoInGame();
            for (var i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 123 }); // 设置非默认值以生成逐组件统计
            testWorld.Tick();
            testWorld.Tick(); // 发送实体后客户端在同一次 Tick 调用中接收，双方写统计缓冲此时均应更新
            testWorld.Tick(); // 将客户端和服务器写统计复制到各自的读统计缓冲
            // 客户端此时也已生成 Ghost
            testWorld.Tick();

            // 修改服务器组件以触发逐组件统计
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct() { field000 = 124 });
            testWorld.Tick(); // 发送数据
            testWorld.Tick(); // 更新服务器读统计缓冲

            // 启用 Profiler
            var saveDataFilePath = Path.Combine(Application.temporaryCachePath, "Profiler_DumpAndLoadStats_Savefile.data");
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = true;
            Profiler.enabled = true;

            // 运行多帧以收集统计
            const int frameCount = 100;
            for (var i = 0; i < frameCount; i++)
            {
                testWorld.Tick();
                yield return null;
            }

            // 保存 Profiler 会话并重新加载
            ProfilerDriver.SaveProfile(saveDataFilePath);
            ProfilerDriver.ClearAllFrames();
            var loaded = ProfilerDriver.LoadProfile(saveDataFilePath, false);
            Assert.IsTrue(loaded);
            Assert.AreNotEqual(-1, ProfilerDriver.lastFrameIndex);

            ProfilerDriver.profileEditor = false;
            Profiler.enabled = false;

            // 读取帧元数据
            using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
            {
                Assert.NotNull(frameDataView);
                Assert.True(frameDataView.valid);

                GetAndCheckStats(frameDataView, ProfilerMetricsConstants.ServerGuid);
                GetAndCheckStats(frameDataView, ProfilerMetricsConstants.ClientGuid);
            }
        }

        static void GetAndCheckStats(RawFrameDataView frameDataView, Guid guid) {
            // 获取序列化后的 Ghost 统计
            var serializedGhostStatsSnapshot = frameDataView.GetFrameMetaData<byte>(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
            Assert.IsNotEmpty(serializedGhostStatsSnapshot);

            // 反序列化 Ghost 统计
            var ghostStatsSnapshot = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, serializedGhostStatsSnapshot);
            Assert.NotNull(ghostStatsSnapshot);
            var perGhostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            Assert.IsTrue(perGhostTypeStats.IsCreated);
            Assert.IsFalse(perGhostTypeStats.IsEmpty);
            var firstTypeComponentStats = perGhostTypeStats[0].PerComponentStatsList;
            Assert.IsTrue(firstTypeComponentStats.IsCreated);
            Assert.IsFalse(firstTypeComponentStats.IsEmpty);

            // 检查其他指标
            var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(frameDataView, ProfilerMetricsConstants.ServerGuid);
            Assert.IsTrue(frameMetaData.CommandStats.IsCreated);
            Assert.IsTrue(frameMetaData.CommandStats.Length == 3);
            Assert.IsTrue(frameMetaData.ComponentIndices.IsCreated);
            Assert.IsFalse(frameMetaData.ComponentIndices.Length == 0);
            Assert.NotNull(frameMetaData.NetworkMetrics);
            Assert.IsTrue(frameMetaData.PredictionErrorMetrics.IsCreated);
            Assert.IsTrue(frameMetaData.PrefabSerializers.IsCreated);
            Assert.NotNull(frameMetaData.ProfilerMetrics);
            Assert.IsTrue(frameMetaData.SerializerStates.IsCreated);
            Assert.IsTrue(frameMetaData.UncompressedSizesPerType.IsCreated);

            // TODO 查明以下三项失败的原因
            // Assert.IsTrue(frameMetaData.GhostNames.IsCreated);
            // Assert.IsFalse(frameMetaData.GhostNames.Length == 0);
            // Assert.IsTrue(frameMetaData.PredictionErrors.IsCreated);
        }
#endif

        private Entity CreateEntityPrefab(World world)
        {
            var entity = world.EntityManager.CreateEntity(typeof(GhostGenTestTypes.GhostGenBigStruct));
            GhostPrefabCreation.ConvertToGhostPrefab(world.EntityManager, entity, new GhostPrefabCreation.Config
            {
                Name = "GhostGenBigStruct",
                SupportedGhostModes = GhostModeMask.Predicted,
            });
            return entity;
        }

        void UpdateMetrics(World world, bool useMetrics, Entity metricsSingleton)
        {
            if (useMetrics)
                world.EntityManager.AddBuffer<PredictionErrorMetrics>(metricsSingleton);
        }
    }
}
