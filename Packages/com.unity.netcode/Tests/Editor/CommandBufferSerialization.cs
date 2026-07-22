#pragma warning disable CS0618 // 禁用 Entities.ForEach 过时警告
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;

namespace Unity.NetCode.Tests
{
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.OnlyPredictedClients,
        OwnerSendType = SendToOwnerType.SendToNonOwner)]
    internal struct TestInput : ICommandData
    {
        [GhostField] public NetworkTick Tick { get; set; }
        [GhostField] public int Value;
    }

    internal struct TestInput2 : ICommandData
    {
        [GhostField] public NetworkTick Tick { get; set; }
        [GhostField] public int Value2;
    }

    internal class TestInputConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            baker.AddComponent<GhostGen_IntStruct>(entity);
            baker.AddBuffer<TestInput>(entity);
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    internal partial class PredictionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<NetworkStreamInGame>();
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            Entities
                .WithAll<Simulate>()
                .ForEach((Entity entity, ref LocalTransform transform, in DynamicBuffer<TestInput> inputBuffer) =>
                {
                    if (!inputBuffer.GetDataAtTick(tick, out var input))
                        return;

                    transform.Position.y += 1.0f * input.Value;
                }).Run();
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class InputSystem : SystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate<GhostOwner>();
        }
        protected override void OnUpdate()
        {
            var connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();
            var commandTarget = EntityManager.GetComponentData<CommandTarget>(connection);
            if (commandTarget.targetEntity == Entity.Null)
                return;
            var inputBuffer = EntityManager.GetBuffer<TestInput>(commandTarget.targetEntity);
            inputBuffer.AddCommandData(new TestInput
            {
                Tick = SystemAPI.GetSingleton<NetworkTime>().InputTargetTick,
                Value = 1
            });
        }
    }
    internal class CommandBufferTests
    {
        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Interpolated, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void CommandDataBuffer_GhostOwner_WillNotReceiveTheBuffer(GhostModeMask modeMask,
            GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, 1, 0);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                // 服务端可以丢弃到达过晚的 Command，但至少应收到一半
                Assert.GreaterOrEqual(serverBuffer.Length, clientBuffer.Length / 2);
                // 由于冗余发送，服务端始终拥有更多 Input
                int firstServerTick = 0;
                Assert.Less(firstServerTick, serverBuffer.Length);
                Assert.AreNotEqual(0, serverBuffer[firstServerTick].Value);
                // 服务端不能包含比客户端现有数据更早的 Command
                Assert.GreaterOrEqual(serverBuffer[firstServerTick].Tick.TicksSince(clientBuffer[0].Tick), 0);
                for (int i = firstServerTick; i < serverBuffer.Length; ++i)
                    Assert.AreEqual(1, serverBuffer[i].Value);
                for (int i = 0; i < clientBuffer.Length; ++i)
                    Assert.AreEqual(1, clientBuffer[i].Value);
                // 重写服务端 Buffer，并确认客户端数据不会随之变化
                serverBuffer.Length = 4;
                for (int i = 0; i < serverBuffer.Length; ++i)
                    serverBuffer[i] = new TestInput {Tick = serverBuffer[i].Tick, Value = 2};

                for (int i = 0; i < 10; ++i)
                    testWorld.Tick();

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                Assert.Less(serverBuffer.Length, clientBuffer.Length);
                for (int i = 0; i < clientBuffer.Length; ++i)
                    Assert.AreEqual(1, clientBuffer[i].Value);

            }
        }

        [Test]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void CommandDataBuffer_NonOwner_WillReceiveTheBuffer(GhostModeMask modeMask,
            GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 2);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, 2, 0);

                // 运行一系列完整 Tick，检查 Buffer 是否复制到 NonOwner
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientBuffer0 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                var clientBuffer1 = testWorld.ClientWorlds[1].EntityManager.GetBuffer<TestInput>(clientEnt[1]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                Assert.GreaterOrEqual(serverBuffer.Length, clientBuffer1.Length/2);
                Assert.GreaterOrEqual(serverBuffer.Length, clientBuffer0.Length/2);
                for (int i = 4; i < serverBuffer.Length; ++i)
                    Assert.AreEqual(serverBuffer[i].Value, clientBuffer0[i-4].Value);
                var bufferCopy = new TestInput[serverBuffer.Length];
                serverBuffer.AsNativeArray().CopyTo(bufferCopy);
                // 运行若干部分 Tick，检查 Buffer 是否正确保留
                for (int i = 0; i < 3; ++i)
                {
                    testWorld.Tick((1.0f / 60.0f) / 4.0f);
                    clientBuffer1 = testWorld.ClientWorlds[1].EntityManager.GetBuffer<TestInput>(clientEnt[1]);
                    serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                    Assert.AreEqual(serverBuffer.Length, clientBuffer1.Length);
                    for (int k = 0; k < serverBuffer.Length; ++k)
                        Assert.AreEqual(bufferCopy[k].Value, clientBuffer1[k].Value);
                }
                // 执行最后一个部分 Tick，检查 Buffer 是否重新同步
                testWorld.Tick((1.0f / 60.0f) / 4.0f);
                Assert.AreEqual(serverBuffer.Length, clientBuffer1.Length);
                Assert.Greater(clientBuffer1.Length, bufferCopy.Length);
            }
        }


        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.Interpolated, GhostMode.Interpolated)]
        public void CommandDataBuffer_NonOwner_ShouldNotReceiveTheBuffer(GhostModeMask modeMask,
            GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                int numClients = 2;

                testWorld.CreateWorlds(true, numClients);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, numClients, 0);

                // 运行一系列完整 Tick，检查 Buffer 不会复制到 NonOwner
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                for (int i = 0; i < numClients; ++i)
                {
                    var clientBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<TestInput>(clientEnt[i]);
                    if (i != 0)
                    {
                        Assert.AreNotEqual(serverBuffer.Length, clientBuffer.Length);
                        Assert.AreEqual(0, clientBuffer.Length);
                    }
                }
            }
        }

        // 上一个测试的扩展版本，每个活跃客户端各有一个 Entity，并额外包含一个旁观客户端
        [Test]
        public void CommandDataBuffer_OwnerPredicted_InterpolatedClientes_ShouldNotReceiveTheBuffer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = GhostModeMask.All;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                int numClients = 3;

                testWorld.CreateWorlds(true, numClients);
                testWorld.Connect();
                testWorld.GoInGame();

                var serverEnt1 = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var serverEnt2 = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 1);
                var clientEnt = new Entity[2];
                // 执行若干 Tick，等待所有 Entity 完成 Spawn
                for(int i=0;i<16;++i)
                    testWorld.Tick();
                // 在对应客户端上设置 Owner，Client 3 为无 Entity 的被动客户端
                for(int i=0;i<2;++i)
                {
                    using var query = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                    var entities = query.ToEntityArray(Allocator.Temp);
                    var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
                    using var connQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(NetworkId));
                    var conn = connQuery.GetSingletonEntity();
                    var networkId = connQuery.GetSingleton<NetworkId>();
                    for(int e=0;e<entities.Length;++e)
                    {
                        if (owners[e].NetworkId == networkId.Value)
                        {
                            clientEnt[i] = entities[e];
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(conn, new CommandTarget {targetEntity = entities[e]});
                        }
                    }
                }
                // 运行一系列完整 Tick，检查 Buffer 不会复制到客户端的 Interpolated Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                for (int i = 0; i < 3; ++i)
                {
                    using var query = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                    var entities = query.ToEntityArray(Allocator.Temp);
                    for (int e = 0; e < entities.Length; ++e)
                    {
                        var clientBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<TestInput>(entities[e]);
                        if(i == 2 || entities[e] != clientEnt[i])
                        {
                            Assert.AreEqual(0, clientBuffer.Length, $"Client {i} entity {e}");
                        }
                        else
                        {
                            Assert.AreNotEqual(0, clientBuffer.Length, $"Client {i} entity {e}");
                        }

                    }
                }
            }
        }

        private static Entity[] WaitEntitySpawnedOnClientsAndAssignOwner(NetCodeTestWorld testWorld, int numClients, int owner)
        {
            bool entitiesAreNotSpawned;
            var clientEnt = new Entity[numClients];
            int iterations = 0;
            do
            {
                ++iterations;
                testWorld.Tick();
                entitiesAreNotSpawned = false;
                for (int i = 0; i < numClients; ++i)
                {
                    clientEnt[i] = testWorld.TryGetSingletonEntity<TestInput>(testWorld.ClientWorlds[i]);
                    entitiesAreNotSpawned |= clientEnt[i] == Entity.Null;
                }
            } while (entitiesAreNotSpawned && iterations < 128);

            var clientConn = testWorld.TryGetSingletonEntity<NetworkStreamInGame>(testWorld.ClientWorlds[owner]);
            testWorld.ClientWorlds[owner].EntityManager.SetComponentData(clientConn, new CommandTarget {targetEntity = clientEnt[owner]});
            return clientEnt;
        }

        private static Entity SpawnEntityAndAssignOwnerOnServer(NetCodeTestWorld testWorld, GameObject ghostGameObject, int clientOwner)
        {
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            var net1 = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[clientOwner]);
            var netId1 = testWorld.ClientWorlds[clientOwner].EntityManager.GetComponentData<NetworkId>(net1);

            using var entitiesQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
            var entities = entitiesQuery.ToEntityArray(Allocator.Temp);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner {NetworkId = netId1.Value});
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 1000});
            for (int i = 0; i < entities.Length; ++i)
            {
                var netId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(entities[i]);
                if (netId.Value == netId1.Value)
                    testWorld.ServerWorld.EntityManager.SetComponentData(entities[i], new CommandTarget {targetEntity = serverEnt});
            }

            return serverEnt;
        }
    }
}
