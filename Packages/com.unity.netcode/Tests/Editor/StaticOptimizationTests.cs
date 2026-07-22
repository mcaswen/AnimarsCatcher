#pragma warning disable CS0618 // 禁用 Entities.ForEach 的过时警告
using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;
using Unity.Collections;
using Unity.Mathematics;

namespace Unity.NetCode.Tests
{
    internal class StaticOptimizationTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    internal class ZeroChangeGhostStaticOptimizationTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.None);
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class StaticOptimizationTestSystem : SystemBase
    {
        public static int s_ModifyNetworkId;
        protected override void OnUpdate()
        {
            int modifyNetworkId = s_ModifyNetworkId;
            Entities.ForEach((ref LocalTransform trans, in GhostOwner ghostOwner) => {
                if (ghostOwner.NetworkId != modifyNetworkId)
                    return;
                trans.Position.x += 1;
            }).ScheduleParallel();
        }
    }

    internal class StaticOptimizationTests
    {
        void SetupBasicTest(NetCodeTestWorld testWorld, NetCodeTestLatencyProfile latencyProfile, TestNetCodeAuthoring.IConverter testConverter, int entitiesToSpawn = 1)
        {
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = testConverter;
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.OptimizationMode = GhostOptimizationMode.Static;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.SetTestLatencyProfile(latencyProfile);
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < entitiesToSpawn; ++i)
            {
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
            }

            // 建立连接并确认连接成功
            testWorld.Connect(maxSteps:16);

            // 进入游戏状态
            testWorld.GoInGame();

            // 推进若干帧以便客户端生成 Ghost
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
        }
        [Test]
        public void StaticGhostsAreNotSent([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);

                var lastSnapshot = new NativeArray<NetworkTick>(clientEntities.Length, Allocator.Temp);
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // 记录该 Ghost 最近收到的快照 Tick
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    lastSnapshot[i] = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                }

                // 继续推进若干帧
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // 验证客户端没有收到新快照
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // 读取该 Ghost 最近收到的快照 Tick
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    Assert.AreEqual(lastSnapshot[i], clientSnapshot.GetLatestTick(clientSnapshotBuffer));
                }
            }
        }
        [Test]
        public void GhostsCanBeStaticWhenChunksAreDirty([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // 系统取得 LocalTransform 写权限会标脏 Chunk，但不会实际修改任何实体
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = 1;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);

                var lastSnapshot = new NativeArray<NetworkTick>(clientEntities.Length, Allocator.Temp);
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // 记录该 Ghost 最近收到的快照 Tick
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    lastSnapshot[i] = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                }

                // 继续推进若干帧
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();
                // 验证客户端没有收到新快照
                for (int i = 0; i < clientEntities.Length; ++i)
                {
                    var clientEnt = clientEntities[i];
                    // 读取该 Ghost 最近收到的快照 Tick
                    var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                    var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                    Assert.AreEqual(lastSnapshot[i], clientSnapshot.GetLatestTick(clientSnapshotBuffer));
                }
            }
        }
        [Test]
        public void StaticGhostsAreNotApplied([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                const int entitiesToSpawn = 7;
                const int constantlyChangingIndex = 3;
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = constantlyChangingIndex;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), entitiesToSpawn);

                // 指定一个 Ghost 持续修改位置
                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var clientEntities = clientEm.CreateEntityQuery(ComponentType.ReadWrite<GhostOwner>()).ToEntityArray(Allocator.Temp);
                Assert.AreEqual(entitiesToSpawn, clientEntities.Length);
                clientEm.SetComponentData(clientEntities[constantlyChangingIndex], new GhostOwner{NetworkId = constantlyChangingIndex});

                // 直接写入客户端 GhostField，用于验证应用持续变化实体的快照时不会改动其他字段
                var expectedPos = new float3(3, 4, 5);
                var expectedRot = Mathematics.quaternion.Euler(5, 6, 7);
                const int expectedScale = 8;
                for (var i = 0; i < clientEntities.Length; i++)
                {
                    clientEm.SetComponentData(clientEntities[i], new LocalTransform
                    {
                        Position = expectedPos,
                        Rotation = expectedRot,
                        Scale = expectedScale,
                    });
                }

                // 推进若干 Tick
                for(int i = 0; i < 16; i++)
                    testWorld.Tick();

                // 验证各字段的应用结果
                for (var i = 0; i < clientEntities.Length; i++)
                {
                    var clientTrans = clientEm.GetComponentData<LocalTransform>(clientEntities[i]);
                    var serverTick = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;
                    // GhostField 按字段应用，因此即使 LocalTransform.Position 变化，同一组件中的其他 GhostField 也不应变化
                    if (i == constantlyChangingIndex)
                        Assert.AreNotEqual(expectedPos, clientTrans.Position, $"Unexpectedly NOT changed on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    else Assert.AreEqual(expectedPos, clientTrans.Position, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    Assert.AreEqual(expectedRot, clientTrans.Rotation, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                    Assert.AreEqual(expectedScale, clientTrans.Scale, $"Unexpected change on idx:{i} i.e. ServerTick:{serverTick.ToFixedString()}");
                }
            }
        }
        [Test]
        public void StaticGhostsAreSentWhenModified([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(StaticOptimizationTestSystem));
                StaticOptimizationTestSystem.s_ModifyNetworkId = -1;

                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter());

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                // 记录该 Ghost 最近收到的快照 Tick
                var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var lastSnapshot = clientSnapshot.GetLatestTick(clientSnapshotBuffer);

                // 继续推进若干帧
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 验证客户端没有收到新快照
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                Assert.AreEqual(lastSnapshot, clientSnapshot.GetLatestTick(clientSnapshotBuffer));

                // 修改一次位置并继续推进若干 Tick
                StaticOptimizationTestSystem.s_ModifyNetworkId = 0;
                testWorld.Tick();
                StaticOptimizationTestSystem.s_ModifyNetworkId = -1;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 验证修改后客户端收到了新快照
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var newLastSnapshot = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                Assert.AreNotEqual(lastSnapshot, newLastSnapshot);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 验证同步新位置后快照再次保持静态
                clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                Assert.AreEqual(newLastSnapshot, clientSnapshot.GetLatestTick(clientSnapshotBuffer));
            }
        }

        [Test]
        public void StaticGhostsAreSentWhenUnmodified([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            const int entitiesToSpawn = 2;
            SetupBasicTest(testWorld, latencyProfile, new ZeroChangeGhostStaticOptimizationTestConverter(), entitiesToSpawn:entitiesToSpawn);

            // 验证没有字段变化的 Ghost 仍会在客户端生成，此处覆盖 1.5 版本回归问题
            var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
            var clientEntities = clientEntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostOwner>()).ToEntityArray(Allocator.Temp);
            Assert.AreEqual(entitiesToSpawn, clientEntities.Length);

            // 验证静态优化已经生效
            var currentTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
            foreach (var clientEnt in clientEntities)
            {
                var clientSnapshotBuffer = clientEntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = clientEntityManager.GetComponentData<SnapshotData>(clientEnt);
                var ghostsLatestReceivedTick = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                var ticksSince = currentTick.TicksSince(ghostsLatestReceivedTick);
                Assert.IsTrue(ticksSince > 3, ticksSince.ToString());
            }
        }

        [Test]
        public void RelevancyChangesSendsStaticGhosts([Values]NetCodeTestLatencyProfile latencyProfile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                // 生成十六个 Ghost
                SetupBasicTest(testWorld, latencyProfile, new StaticOptimizationTestConverter(), 16);
                // 读取第一个 Ghost ID 以构造相关性键
                using var serverQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                int ghostId;
                var serverEntities = serverQuery.ToEntityArray(Allocator.Temp);
                Assert.AreEqual(16, serverEntities.Length);
                ghostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntities[0]).ghostId;
                var con = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, con);
                var connectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(con).Value;

                // 将当前状态同步到客户端
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEntityManager = testWorld.ClientWorlds[0].EntityManager;
                using var clientQuery = clientEntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostOwner>());
                var clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);


                // 将其中一个 Ghost 标记为不相关
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                var key = new RelevantGhostForConnection{Connection = connectionId, Ghost = ghostId};
                ghostRelevancy.GhostRelevancySet.TryAdd(key, 1);

                // 将相关性变更同步到客户端
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(15, clientEntities.Length);

                // 恢复相关性以允许该 Ghost 再次生成
                ghostRelevancy.GhostRelevancySet.Remove(key);

                // 将恢复后的相关性同步到客户端
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                clientEntities = clientQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                Assert.AreEqual(16, clientEntities.Length);
            }
        }
    }
}
