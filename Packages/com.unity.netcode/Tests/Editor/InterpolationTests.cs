#pragma warning disable CS0618 // 禁用 Entities.ForEach 过时警告
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Transforms;
using Random = UnityEngine.Random;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class MoveAlongAxisSystem : SystemBase
    {
        // 以 60 Hz 运行时每帧移动 0.1 个单位
        public float moveSpeed = 6f;

        protected override void OnUpdate()
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            var speed = moveSpeed;
            Entities.ForEach((Entity ent, ref LocalTransform tx) => { tx.Position += new float3(speed * deltaTime); }).Run();
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class TestInterpGhost : SystemBase
    {
        private float3 prevPos;
        protected override void OnUpdate()
        {
            Entities
                .WithoutBurst()
                .ForEach((Entity ent, in LocalTransform tx) =>
            {
                Assert.GreaterOrEqual(tx.Position.x, prevPos.x);
                prevPos = tx.Position;
            }).Run();
        }
    }

    internal class NetworkTimeTests
    {
        const float FrameTime = 1.0f / 60.0f;

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        public void WhenUsingIPC_ClientPredictOnlyOneTickAhead()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // 在服务端生成实体，服务端随后开始发送 Snapshot
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                var connectionEnt = testWorld.TryGetSingletonEntity<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                // 该测试对 Tick 数敏感，因为 ServerCommandAge 需要时间逐步收敛到 0
                // 当前平滑系数为 1/8，DeltaTime 最大调整幅度为 20%
                // 因此大约需要 50 个 Tick 才能将 Command Age 调整到 0 附近
                for (int i = 0; i < 51; ++i)
                {
                    // 客户端运行速度略快，因此循环中会出现额外的插值 Tick
                    testWorld.Tick(FrameTime*0.75f);
                    testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                    var ackComponent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAck>(connectionEnt);
                    var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                    if (serverTick.IsValid && ackComponent.LastReceivedSnapshotByLocal.IsValid)
                    {
                        // 客户端在服务端之后更新，因此模拟得到的服务端 Tick 应等于客户端最后接收的 Snapshot Tick 或略高
                        // 两者差距不应超过允许的预测提前量
                        var ackTick = ackComponent.LastReceivedSnapshotByLocal;
                        Assert.IsFalse(ackTick.IsNewerThan(serverTick));
                        ackTick.Add(2);
                        Assert.IsFalse(serverTick.IsNewerThan(ackTick));
                        // 初始 Command Age 可以大于 0，但应在若干 Tick 后收敛到预期值 0
                        Assert.LessOrEqual(ackComponent.ServerCommandAge, 128);
                    }
                }
                var ack = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAck>(connectionEnt);
                Assert.AreEqual(0, ack.ServerCommandAge, "Server command age should be zero!");
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void InterpolationAndPredictedTickNeverGoesBack()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // 在服务端生成实体，服务端随后开始发送 Snapshot
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                for (int i = 0; i < 50; ++i)
                {
                    var currentFrameTime = Random.Range(FrameTime*0.75f, FrameTime*1.25f);
                    testWorld.Tick(currentFrameTime);
                    var networkTimeSystemData = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                    if (networkTimeSystemData.predictTargetTick.IsValid)
                    {
                        if (prevTargetTick.IsValid)
                        {
                            Assert.IsFalse(prevTargetTick.IsNewerThan(networkTimeSystemData.predictTargetTick));
                            Assert.IsFalse(prevInterpTick.IsNewerThan(networkTimeSystemData.interpolateTargetTick));
                        }
                        prevTargetTick = networkTimeSystemData.predictTargetTick;
                        prevInterpTick = networkTimeSystemData.interpolateTargetTick;
                    }
                }
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void InterpolationTickAdaptToPacketDelay()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                // 设置测试允许的最大延迟，确保内部 Buffer 按上限正确分配
                testWorld.DriverSimulatedDelay = 200;
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // 在服务端生成实体，服务端随后开始发送 Snapshot
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                var delays = new []{ 70, 100, 200,150, 100 };
                foreach (var delay in delays)
                {
                    var connectionEnt = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                    var connection = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkStreamConnection>(connectionEnt);
                    // TODO：内部方法支持 readonly 后改为只读获取，避免结构复制
                    ref var driverInstance = ref testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.DriverStore.GetDriverInstanceRW(connection.DriverId);
                    var simStageId = NetworkPipelineStageId.Get<SimulatorPipelineStage>();
                    driverInstance.driver.GetPipelineBuffers(driverInstance.unreliablePipeline, simStageId, connection.Value, out var _, out var _, out var simulatorBuffer);
                    unsafe
                    {
                        var simulatorCtx = (SimulatorUtility.Parameters*)simulatorBuffer.GetUnsafePtr();
                        simulatorCtx->PacketDelayMs = delay;
                    }
                    for (int i = 0; i < 50; ++i)
                    {
                        testWorld.Tick();
                        var networkTimeSystemData = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                        if (networkTimeSystemData.predictTargetTick.IsValid)
                        {
                            if (prevTargetTick.IsValid)
                            {
                                Assert.IsFalse(prevTargetTick.IsNewerThan(networkTimeSystemData.predictTargetTick));
                                Assert.IsFalse(prevInterpTick.IsNewerThan(networkTimeSystemData.interpolateTargetTick));
                            }
                            prevTargetTick = networkTimeSystemData.predictTargetTick;
                            prevInterpTick = networkTimeSystemData.interpolateTargetTick;
                        }
                    }
                }
            }
        }

        [Test]
        [Ignore("Disabled as there is a bug with RTT calculations when sending RPCs - we do not correctly account for (i.e. subtract the cost of) reliable pipeline resends. Tracked as MTT-11335")]
        public void InterpolationTickAdaptToPacketDrop()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                testWorld.DriverSimulatedDelay = 5;
                testWorld.DriverSimulatedDrop = 3; // 按间隔丢包，即每 3 个包丢 1 个，约 33%
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // 在服务端生成实体，服务端随后开始发送 Snapshot
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                var ntsd = default(NetworkTimeSystemData);
                for (int i = 0; i < 100; ++i)
                {
                    testWorld.Tick();
                    ntsd = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                    if (ntsd.predictTargetTick.IsValid)
                    {
                        // UnityEngine.Debug.Log($"[Tick:{NetCodeTestWorld.TickIndex}] predictTargetTick:{ntsd.predictTargetTick.ToFixedString()} interpolateTargetTick:{ntsd.interpolateTargetTick.ToFixedString()} (delta:({ntsd.predictTargetTick.TicksSince(ntsd.interpolateTargetTick)})");
                        if (prevTargetTick.IsValid)
                        {
                            Assert.IsFalse(prevTargetTick.IsNewerThan(ntsd.predictTargetTick));
                            Assert.IsFalse(prevInterpTick.IsNewerThan(ntsd.interpolateTargetTick));
                        }
                        prevTargetTick = ntsd.predictTargetTick;
                        prevInterpTick = ntsd.interpolateTargetTick;
                    }
                }
                Assert.Greater(ntsd.currentInterpolationFrames, 2f, "currentInterpolationFrames");
                Assert.Less(ntsd.currentInterpolationFrames, 4f, "currentInterpolationFrames");
            }
        }
    }
}
