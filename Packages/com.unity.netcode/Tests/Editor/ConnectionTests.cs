using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using State = Unity.NetCode.ConnectionState.State;
namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class CheckConnectionSystem : SystemBase
    {
        public int numConnected;
        public int numInGame;
        private EntityQuery inGame;
        private EntityQuery connected;
        protected override void OnCreate()
        {
            connected = GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
            inGame = GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
        }

        protected override void OnUpdate()
        {
            numConnected = connected.CalculateEntityCount();
            numInGame = inGame.CalculateEntityCount();
        }
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class ConnectionTests
    {
        internal struct CheckApproval : IApprovalRpcCommand
        {
            public int Payload;
        }

        [Test]
        [Category(NetcodeTestCategories.Smoke)]
        public void ConnectSingleClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckConnectionSystem));
                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numConnected);

                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ServerWorld.GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numConnected);
                Assert.AreEqual(1, testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckConnectionSystem>().numInGame);
            }
        }

        [TestCase(60, 60, 1)]
        [TestCase(40, 20, 2)]
        public void ClientTickRate_ServerAndClientsUseTheSameRateSettings(
            int simulationTickRate, int networkTickRate, int predictedFixedStepRatio)
        {
            using var testWorld = new NetCodeTestWorld();
            var tickRate = new ClientServerTickRate
            {
                SimulationTickRate = simulationTickRate,
                PredictedFixedStepSimulationTickRatio = predictedFixedStepRatio,
                NetworkTickRate = networkTickRate,
                HandshakeApprovalTimeoutMS = 10_000, // 防止超时
            };
            SetupTickRate(tickRate, testWorld);
            // 检查预测固定步长频率是否同步设置
            LogAssert.NoUnexpectedReceived();
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTimeStep, testWorld.ServerWorld.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTimeStep, testWorld.ClientWorlds[0].GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep);
        }

        static void SetupTickRate(ClientServerTickRate tickRate, NetCodeTestWorld testWorld)
        {
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.ServerWorld.EntityManager.CreateSingleton(tickRate);
            tickRate.ResolveDefaults();
            tickRate.Validate();
            // 建立连接并确认连接成功
            testWorld.Connect();

            // 检查 Simulation Tick Rate 是否一致
            var serverRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ServerWorld);
            var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
            Assert.AreEqual(tickRate.SimulationTickRate, serverRate.SimulationTickRate);
            Assert.AreEqual(tickRate.SimulationTickRate, clientRate.SimulationTickRate);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTickRatio, serverRate.PredictedFixedStepSimulationTickRatio);
            Assert.AreEqual(tickRate.PredictedFixedStepSimulationTickRatio, clientRate.PredictedFixedStepSimulationTickRatio);

            // 再推进一步以应用全部新设置
            testWorld.Tick();
        }

        [Test]
        public void IncorrectlyDisposingAConnectionLogsError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                Test(testWorld, testWorld.ClientWorlds[0]);
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                Test(testWorld, testWorld.ServerWorld);
            }

            static void Test(NetCodeTestWorld testWorld, World worldBeingTested)
            {
                testWorld.Connect();
                var connEntity = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(worldBeingTested);
                Assert.IsTrue(worldBeingTested.EntityManager.Exists(connEntity));
                LogAssert.Expect(LogType.Error, new Regex($@"(has been incorrectly disposed)(.*)({worldBeingTested.Name})"));
                worldBeingTested.EntityManager.DestroyEntity(connEntity);
                testWorld.Tick(); // 该 Tick 会触发错误
                testWorld.Tick(); // 该 Tick 不应再次触发错误
            }
        }

        internal enum ApprovalMode
        {
            NoApproval,
            WithApproval,
        }
        internal enum ConnectionStateMode
        {
            UsingConnectionState,
            NoConnectionState,
        }

        private bool isVerifyingConnState;
        [Test]
        public void ConnectionEventsAreRaised([Values]ApprovalMode approvalMode, [Values]ConnectionStateMode connectionStateMode)
        {
            var isApproval = approvalMode == ApprovalMode.WithApproval;
            isVerifyingConnState = connectionStateMode == ConnectionStateMode.UsingConnectionState;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 3);

                // 手动建立连接
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;

                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = isApproval;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                var connectionEntities = new Entity[testWorld.ClientWorlds.Length];
                for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                {
                    var clientWorld = testWorld.ClientWorlds[i];
                    if (isVerifyingConnState)
                    {
                        connectionEntities[i] = clientWorld.EntityManager.CreateEntity(typeof(ConnectionState));
                        testWorld.GetSingletonRW<NetworkStreamDriver>(clientWorld).ValueRW.Connect(clientWorld.EntityManager, ep, connectionEntities[i]);
                        // 确保 Tick 0 时 ConnectionState 也正确
                        var cs = clientWorld.EntityManager.GetComponentData<ConnectionState>(connectionEntities[i]);
                        Assert.AreEqual(State.Connecting, cs.CurrentState);
                    }
                    else connectionEntities[i] = testWorld.GetSingletonRW<NetworkStreamDriver>(clientWorld).ValueRW.Connect(clientWorld.EntityManager, ep);
                }

                // Tick 0：已调用 Connect，但尚无任何事件
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                // Tick 1：仅客户端产生 Connecting 事件
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Connecting);

                // Tick 2：客户端与服务端都应进入 Handshake
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);

                // 此时添加 ConnectionState
                if (isVerifyingConnState)
                {
                    using var serverNetworkStreamConnectionsQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                    Assert.AreEqual(3, serverNetworkStreamConnectionsQuery.CalculateEntityCount(), "Sanity check: Adding ConnectionState to all 3 server entities.");
                    testWorld.ServerWorld.EntityManager.AddComponent<ConnectionState>(serverNetworkStreamConnectionsQuery);
                }

                isVerifyingConnState = false; // 服务端在当前帧才添加 ConnectionState，因此此处状态尚不正确
                ServerHasEventForEachClient(testWorld, ConnectionState.State.Handshake);
                isVerifyingConnState = connectionStateMode == ConnectionStateMode.UsingConnectionState;
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Handshake);

                // Tick 3：客户端向服务端发送 RPC，双方都不产生事件
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                // Tick 4：连接流程在此分支
                // - 需要审批时，服务端进入 Approval 状态并响应
                // - 无需审批时，服务端进入 Connected 状态并响应
                // 两种情况下都只有服务端产生事件，客户端不产生事件
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                if (isApproval) // 进入审批分支
                {
                    using var serverCheckApprovalQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>(), ComponentType.ReadOnly<CheckApproval>());
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());

                    // 服务端应处于 Approval 状态
                    ServerHasEventForEachClient(testWorld, ConnectionState.State.Approval);

                    // 客户端必须等待 ServerRequestApprovalAfterHandshake RPC
                    // Tick 5：客户端进入 Approval 状态
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                    WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Approval);

                    // 执行连接审批流程，客户端用户代码响应事件并发送审批 RPC
                    for (var i = 0; i < testWorld.ClientWorlds.Length; i++)
                    {
                        var world = testWorld.ClientWorlds[i];
                        var approvalRpc = world.EntityManager.CreateEntity();
                        world.EntityManager.AddComponentData(approvalRpc, new CheckApproval() {Payload = 1234});
                        world.EntityManager.AddComponent<SendRpcCommandRequest>(approvalRpc);
                    }

                    // Tick 6：审批 RPC 正在传输
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // Tick 7：审批 RPC 到达，RPC Entity 的 Spawn 已进入队列
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // Tick 8：审批 RPC 可被查询，服务端开始处理
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);

                    // 服务端用户代码响应并添加 ConnectionApproved
                    var rpcEntities = serverCheckApprovalQuery.ToEntityArray(Allocator.Temp);
                    var rpcData = serverCheckApprovalQuery.ToComponentDataArray<ReceiveRpcCommandRequest>(Allocator.Temp);
                    Assert.AreEqual(3, rpcEntities.Length, "Server expecting to have 3 CheckApproval RPCs from clients!");
                    var approvalData = serverCheckApprovalQuery.ToComponentDataArray<CheckApproval>(Allocator.Temp);
                    for (var i = 0; i < rpcData.Length; i++)
                    {
                        Assert.AreEqual(1234, approvalData[i].Payload);
                        testWorld.ServerWorld.EntityManager.DestroyEntity(rpcEntities[i]);
                        testWorld.ServerWorld.EntityManager.AddComponent<ConnectionApproved>(rpcData[i].SourceConnection);
                    }

                    // Tick 9：服务端登记新的审批组件并完成连接，两条流程重新汇合
                    testWorld.Tick();
                    Assert.AreEqual(0, serverCheckApprovalQuery.CalculateEntityCount());
                }

                // 服务端进入 Connected 状态
                AssertCorrectEventCount(testWorld, 3, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);
                ServerHasEventForEachClient(testWorld, ConnectionState.State.Connected, true);

                // 下一 Tick：客户端也应收到 Connected 状态
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                AssertCorrectEventCount(testWorld, 1, testWorld.ClientWorlds);
                WorldHasEventAtIndex(testWorld, testWorld.ClientWorlds, 0, ConnectionState.State.Connected, true);

                // 此后不应再产生事件
                for (int i = 0; i < 3; i++)
                {
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, testWorld.ClientWorlds);
                }

                Debug.Log("Connection flow success! ----------------------");

                using var serverNetworkIdQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var lastClientsConnectionEntity = serverNetworkIdQuery.ToEntityArray(Allocator.Temp)[^1];
                var lastClientWorld = testWorld.ClientWorlds[^1];
                var otherClients = testWorld.ClientWorlds.AsSpan(0, testWorld.ClientWorlds.Length - 1).ToArray();

                // 通过服务端踢出断开最后一个客户端，以同时测试断开原因
                {
                    var conn = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkStreamConnection>(lastClientsConnectionEntity);
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.DriverStore.Disconnect(conn);
                }

                // 下一 Tick：应用断开操作，并在同一帧稍后由 NetworkGroupCommandBufferSystem 为服务端和客户端触发事件
                testWorld.Tick();
                AssertCorrectEventCount(testWorld, 1, testWorld.ServerWorld);
                WorldHasEventAtIndex(testWorld, testWorld.ServerWorld, 0, ConnectionState.State.Disconnected, true, NetworkStreamDisconnectReason.ConnectionClose);
                AssertCorrectEventCount(testWorld, 0, otherClients);
                AssertCorrectEventCount(testWorld, 1, lastClientWorld);
                WorldHasEventAtIndex(testWorld, lastClientWorld, 0, ConnectionState.State.Disconnected, true, NetworkStreamDisconnectReason.ClosedByRemote);

                // 再推进若干帧，确保没有意外事件
                for (int i = 0; i < 3; i++)
                {
                    testWorld.Tick();
                    AssertCorrectEventCount(testWorld, 0, testWorld.ServerWorld);
                    AssertCorrectEventCount(testWorld, 0, otherClients);
                    AssertCorrectEventCount(testWorld, 0, lastClientWorld);
                }
            }
        }

        private static void AssertCorrectEventCount(NetCodeTestWorld testWorld, int numEventsExpectedPerWorld, params World[] worlds)
        {
            foreach (var world in worlds)
            {
                world.EntityManager.CompleteAllTrackedJobs();
                var connectionEventsForTick = testWorld.GetSingleton<NetworkStreamDriver>(world).ConnectionEventsForTick;
                if (numEventsExpectedPerWorld == connectionEventsForTick.Length) continue;

                string all = "";
                for (var i = 0; i < connectionEventsForTick.Length; i++)
                {
                    var evt = connectionEventsForTick[i];
                    all += $"\n\t[{i}]={evt.ToFixedString()}";
                    if (i < numEventsExpectedPerWorld) all += " <-- Expected!";
                    else all += " <-- Surprising!";
                }

                if (connectionEventsForTick.Length > numEventsExpectedPerWorld)
                    Assert.Fail($"Rogue events found! {world.Name} has too MANY events on tick {NetCodeTestWorld.TickIndex}! Expected: {numEventsExpectedPerWorld}, but has: {connectionEventsForTick.Length}\n{all}");
                else Assert.Fail($"{world.Name} has too FEW events on tick {NetCodeTestWorld.TickIndex}! Expected: {numEventsExpectedPerWorld}, but has: {connectionEventsForTick.Length}\n{all}");
            }
        }

        private void ServerHasEventForEachClient(NetCodeTestWorld testWorld, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            var serverWorld = testWorld.ServerWorld;
            serverWorld.EntityManager.CompleteAllTrackedJobs();
            var connectionEventsForServerWorld = testWorld.GetSingleton<NetworkStreamDriver>(serverWorld).ConnectionEventsForTick;
            for (var i = 0; i < connectionEventsForServerWorld.Length; i++)
            {
                WorldHasEventAtIndex(testWorld, serverWorld, i, expectedState, expectedValidNetworkId, expectedDisconnectReason);
            }
        }

        private void WorldHasEventAtIndex(NetCodeTestWorld testWorld, World[] worlds, int index, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            foreach (var world in worlds)
                WorldHasEventAtIndex(testWorld, world, index, expectedState, expectedValidNetworkId, expectedDisconnectReason);
        }

        private void WorldHasEventAtIndex(NetCodeTestWorld testWorld, World world, int index, ConnectionState.State expectedState, bool expectedValidNetworkId = false, NetworkStreamDisconnectReason expectedDisconnectReason = default)
        {
            world.EntityManager.CompleteAllTrackedJobs();
            bool expectEntityExists = expectedState != ConnectionState.State.Disconnected;
            bool expectedConnectionIdToBeValid = expectedState is State.Connecting or State.Handshake or State.Approval or State.Connected or State.Disconnected;
            var connectionEvents = testWorld.GetSingleton<NetworkStreamDriver>(world).ConnectionEventsForTick;
            var evt = connectionEvents[index];
            var s = $"[{world.Name}] ConnectionEventsForTick[{index}]={evt.ToFixedString()}\nOn tick {NetCodeTestWorld.TickIndex}\nExpecting: {expectedState}, validNetworkId:{expectedValidNetworkId}";
            Assert.AreEqual(expectedConnectionIdToBeValid, evt.ConnectionId.IsCreated, s + "\nevt.ConnectionId.IsCreated?");
            Assert.AreEqual(expectedState, evt.State, s + "\nevt.State is correct?");

            Assert.AreEqual(expectedDisconnectReason, evt.DisconnectReason, s + "\nevt.DisconnectReason correct?");
            if (expectedValidNetworkId)
            {
                if (expectEntityExists)
                {
                    Assert.IsTrue(world.EntityManager.HasComponent<NetworkId>(evt.ConnectionEntity), s + "\nHasComponent<NetworkId>(evt.ConnectionEntity) == TRUE");
                    var expectedNetworkId = world.EntityManager.GetComponentData<NetworkId>(evt.ConnectionEntity);
                    Assert.AreEqual(expectedNetworkId.Value, evt.Id.Value, s + "\nComponent value == evt.NetworkId?");
                }
                else Assert.AreNotEqual(0, evt.Id.Value, s + "\nevt.NetworkId.Value != 0?");
            }
            else if (expectEntityExists)
            {
                Assert.IsFalse(world.EntityManager.HasComponent<NetworkId>(evt.ConnectionEntity), s + "\nHasComponent<NetworkId>(evt.ConnectionEntity) == FALSE");
            }

            if (expectEntityExists)
                Assert.AreEqual(expectedState, world.EntityManager.GetComponentData<NetworkStreamConnection>(evt.ConnectionEntity).CurrentState, s + "\nNetworkStreamConnection.CurrentState == " + expectedState);

            if (isVerifyingConnState)
            {
                var cs = world.EntityManager.GetComponentData<ConnectionState>(evt.ConnectionEntity);
                Assert.AreEqual(expectedState, cs.CurrentState, s + "\nConnectionState.CurrentState correct?");
                Assert.AreEqual(expectedDisconnectReason, cs.DisconnectReason, s + "\nConnectionState.DisconnectReason correct?");
                if (expectedValidNetworkId && expectEntityExists)
                {
                    var expectedNetworkId = world.EntityManager.GetComponentData<NetworkId>(evt.ConnectionEntity);
                    Assert.AreEqual(expectedNetworkId.Value, cs.NetworkId, s + "\nConnectionState.NetworkId == evt.NetworkId?");
                }
                if (expectedState == State.Disconnected)
                {
                    bool didRemove = world.EntityManager.RemoveComponent<ConnectionState>(evt.ConnectionEntity);
                    Assert.IsTrue(didRemove, s + "\nRemove ConnectionState success?");
                }
            }

            Assert.AreEqual(expectEntityExists, world.EntityManager.Exists(evt.ConnectionEntity), s + "\nevt.ConnectionEntity exists?");
        }

        [Test]
        public void ConnectionUniqueIdsAreCleanedUp()
        {
            var numClients = 5;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);

                // 连接除最后一个之外的所有客户端，最后一个稍后连接
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < numClients-1; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                testWorld.GoInGame();

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                var firstClientWorld = testWorld.ClientWorlds[0];
                var connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(firstClientWorld);
                var originalClientId = connectionUniqueId.Value;

                // 断开并重新连接第一个客户端
                var firstClientConnectionQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.DriverStore.Disconnect(firstClientConnectionQuery.GetSingleton<NetworkStreamConnection>());
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.Connect(firstClientWorld.EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 确认服务端使用客户端上报的 Unique ID，否则会生成新 ID，并确认重连后 ID 保持不变
                connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(firstClientWorld);
                Assert.AreEqual(originalClientId, connectionUniqueId.Value);

                // 让最后一个客户端使用与第一个客户端重复的 ID
                var lastClientWorld = testWorld.ClientWorlds[numClients - 1];
                lastClientWorld.EntityManager.CreateSingleton(new ConnectionUniqueId() { Value = originalClientId });

                testWorld.GetSingletonRW<NetworkStreamDriver>(lastClientWorld).ValueRW.Connect(lastClientWorld.EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 服务端检测到重复 Unique ID 后应分配新 ID
                connectionUniqueId = testWorld.GetSingleton<ConnectionUniqueId>(lastClientWorld);
                Assert.AreNotEqual(originalClientId, connectionUniqueId.Value);
            }
        }

        [Test]
        public void ReconnectedConnectionsAreDetected()
        {
            var numClients = 5;
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, numClients);

                // 连接除最后一个之外的所有客户端，最后一个稍后连接
                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int i = 0; i < numClients-1; ++i)
                    testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[i]).ValueRW.Connect(testWorld.ClientWorlds[i].EntityManager, ep);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                testWorld.GoInGame();

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 断开并重新连接第一个客户端
                var firstClientWorld = testWorld.ClientWorlds[0];
                var client0ConnectionQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.DriverStore.Disconnect(client0ConnectionQuery.GetSingleton<NetworkStreamConnection>());
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                testWorld.GetSingletonRW<NetworkStreamDriver>(firstClientWorld).ValueRW.Connect(firstClientWorld.EntityManager, ep);
                testWorld.GoInGame(firstClientWorld);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 确认客户端与服务端都将该连接识别为重连
                var clientIsReconnectedOnServerQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsTrue(clientIsReconnectedOnServerQuery.CalculateEntityCount() == 1);
                var clientIsReconnectedQuery = firstClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsTrue(clientIsReconnectedQuery.CalculateEntityCount() == 1);

                // 连接此前尚未连接的最后一个客户端
                var lastClientWorld = testWorld.ClientWorlds[numClients - 1];
                testWorld.GetSingletonRW<NetworkStreamDriver>(lastClientWorld).ValueRW.Connect(lastClientWorld.EntityManager, ep);
                testWorld.GoInGame(lastClientWorld);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 最后一个客户端的本地 Unique ID 与服务端 ID 不匹配，因此不应被识别为重连
                clientIsReconnectedQuery = lastClientWorld.EntityManager.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamIsReconnected));
                Assert.IsFalse(clientIsReconnectedQuery.CalculateEntityCount() == 1);
            }
        }
    }

    // 未启用 NETCODE_DEBUG 时，所有错误日志都会输出到 Console，无法只开启测试所需的特定日志
    // 难以为测试准确配置预期，因此直接禁用整组测试
#if !NETCODE_NDEBUG
    internal class VersionTests
    {
        [Test]
        public void SameVersion_ConnectSuccessfully()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                // 创建后不要推进 World，否则会生成默认协议版本
                // 此处需要使用自定义版本
                testWorld.CreateWorlds(true, 1, false);
                var serverVersion = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                testWorld.ServerWorld.EntityManager.SetComponentData(serverVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });
                var clientVersion = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(NetworkProtocolVersion));
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientVersion, new NetworkProtocolVersion
                {
                    NetCodeVersion = 1,
                    GameVersion = 0,
                    RpcCollectionVersion = 1,
                    ComponentCollectionVersion = 1
                });

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(1, query.CalculateEntityCount());
            }
        }

        internal enum DifferenceType
        {
            GameVersion,
            NetCodeVersion,
            RpcVersion,
            ComponentVersion,
        }
        [Test]
        public void DifferentVersions_AreDisconnnected([Values]DifferenceType differenceType)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, true);

                // 设置 RequireStrictProtocolVersionValidation
                var clientServerTickRate = new ClientServerTickRate();
                clientServerTickRate.ResolveDefaults();
                testWorld.ServerWorld.EntityManager.CreateSingleton(clientServerTickRate);

                // 获取默认协议版本
                int maxTicks = 3;
                Entity serverProtocolVersionEntity;
                while ((serverProtocolVersionEntity = testWorld.TryGetSingletonEntity<NetworkProtocolVersion>(testWorld.ServerWorld)) == Entity.Null)
                {
                    testWorld.Tick();
                    if(maxTicks-- <= 0) Assert.Fail("Sanity: Expected singleton creation!");
                }
                // 在服务端修改协议版本
                var serverProtocolVersion = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkProtocolVersion>(serverProtocolVersionEntity);
                switch (differenceType)
                {
                    case DifferenceType.GameVersion:
                        serverProtocolVersion.GameVersion = 99;
                        break;
                    case DifferenceType.NetCodeVersion:
                        serverProtocolVersion.NetCodeVersion = 98;
                        break;
                    case DifferenceType.RpcVersion:
                        serverProtocolVersion.RpcCollectionVersion = 97;
                        break;
                    case DifferenceType.ComponentVersion:
                        serverProtocolVersion.ComponentCollectionVersion = 96;
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(differenceType), differenceType, null);
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverProtocolVersionEntity, serverProtocolVersion);

                // 协议版本错误消息的顺序可能交错，因此不能按精确顺序设置日志预期
                LogAssert.ignoreFailingMessages = true;
                LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest(.*)\] RpcSystem received bad protocol version from NetworkConnection"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[ServerTest(.*)\] RpcSystem received bad protocol version from NetworkConnection"));

                switch (differenceType)
                {
                    case DifferenceType.GameVersion:
                        LogAssert.Expect(LogType.Error, "The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
                        break;
                    case DifferenceType.NetCodeVersion:
                        LogAssert.Expect(LogType.Error, "The NetCode version mismatched between remote and local. Ensure that you are using the same version of Netcode for Entities on both client and server.");
                        break;
                    case DifferenceType.RpcVersion:
                        LogAssert.Expect(LogType.Error, "The RPC Collection mismatched between remote and local. Compare the following list of RPCs against the set produced by the remote, to find which RPCs are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                        break;
                    case DifferenceType.ComponentVersion:
                        LogAssert.Expect(LogType.Error, "The Component Collection mismatched between remote and local. Compare the following list of Components against the set produced by the remote, to find which components are misaligned. You can also enable `RpcCollection.DynamicAssemblyList` to relax this requirement (which is recommended during development, see documentation for more details).");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(differenceType), differenceType, null);
                }

                // 错误发生在 Handshake 阶段，因此建立连接会触发错误
                testWorld.Connect(failTestIfConnectionFails: false);

                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld), "Expected no connection left!");
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]), "Expected no connection left!");
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public void ProtocolVersionDebugInfoAppearsOnMismatch(bool debugServer)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // 只在一个 World 中输出协议版本调试错误，确保输出可确定地验证
                // 若客户端与服务端同时输出，日志会相互交错并导致检查失败
                testWorld.EnableLogsOnServer = debugServer;  // 警告：必须禁用 Force Log Settings 工具，否则测试会失败
                testWorld.EnableLogsOnClients = !debugServer;
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);

                float dt = 16f / 1000f;
                var entity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(ComponentType.ReadWrite<GameProtocolVersion>());
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(entity, new GameProtocolVersion(){Version = 9000});
                testWorld.Tick(dt);
                testWorld.Tick(dt);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                LogExpectProtocolError(testWorld, testWorld.ServerWorld, debugServer);

                // 等待断开连接完成
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 确认客户端连接已断开
                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        [Test]
        public void DisconnectEventAndRPCVersionErrorProcessedInSameFrame([Values] bool checkServer)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // 只在一个 World 中输出协议版本调试错误，确保输出可确定地验证
                // 若客户端与服务端同时输出，日志会相互交错并导致检查失败
                testWorld.EnableLogsOnServer = checkServer; // 警告：必须禁用 Force Log Settings 工具，否则测试会失败
                testWorld.EnableLogsOnClients = !checkServer;
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);

                float dt = 16f / 1000f;
                testWorld.ClientWorlds[0].EntityManager.CreateSingleton(new GameProtocolVersion(){Version = 9000});
                testWorld.Tick(dt);

                var ep = NetworkEndpoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                LogExpectProtocolError(testWorld, testWorld.ServerWorld, checkServer);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick(dt);

                // 确认客户端连接已断开
                using var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, query.CalculateEntityCount());
            }
        }

        void LogExpectProtocolError(NetCodeTestWorld testWorld, World world, bool checkServer)
        {
            LogAssert.Expect(LogType.Error, new Regex(@$"\[{(checkServer ? "Server" : "Client")}Test(.*)\] RpcSystem received bad protocol version from NetworkConnection\[id0,v1\]"
                                                      + @$"\nLocal protocol: NPV\[NetCodeVersion:{NetworkProtocolVersion.k_NetCodeVersion}, GameVersion:{(checkServer ? "0" : "9000")}, RpcCollection:(\d+), ComponentCollection:(\d+)\]"
                                                      + @$"\nRemote protocol: NPV\[NetCodeVersion:{NetworkProtocolVersion.k_NetCodeVersion}, GameVersion:{(!checkServer ? "0" : "9000")}, RpcCollection:(\d+), ComponentCollection:(\d+)\]"));
            LogAssert.Expect(LogType.Error, "The Game version mismatched between remote and local. Ensure that you are using the same version of the game on both client and server.");
            var rpcs = testWorld.GetSingleton<RpcCollection>(world).Rpcs;
            Assert.AreNotEqual(0, rpcs.Length, "Sanity.");
            LogAssert.Expect(LogType.Error, "RPC List (for above 'bad protocol version' error): " + rpcs.Length);
            for (int i = 0; i < rpcs.Length; ++i)
                LogAssert.Expect(LogType.Error, new Regex("Unity.NetCode"));
            using var collection = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            // GhostCollection Serializer 不会重置为 0
            ref var ghostCollection = ref testWorld.GetSingletonRW<GhostComponentSerializerCollectionData>(testWorld.ClientWorlds[0]).ValueRW;
            Assert.AreNotEqual(0, ghostCollection.Serializers.Length, $"Sanity: ghostCollection.Serializers.Length is zero");
            LogAssert.Expect(LogType.Error, $"Component serializer data (for above 'bad protocol version' error): {ghostCollection.Serializers.Length}");
            for (int i = 0; i < ghostCollection.Serializers.Length; ++i)
                LogAssert.Expect(LogType.Error, new Regex(@$"ComponentHash\[{i}\] = Type:"));
        }

        internal class TestConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner());
                baker.AddComponent(entity, new GhostGenTestUtils.GhostGenTestType_IComponentData());
                // TODO 将 Input、RPC 等其他类型加入该测试
            }
        }
        [Test]
        public void GhostCollectionGenerateSameHashOnClientAndServer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghost1 = new GameObject();
                ghost1.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost1.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostMode.Predicted;
                var ghost2 = new GameObject();
                ghost2.AddComponent<TestNetCodeAuthoring>().Converter = new TestConverter();
                ghost2.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostMode.Interpolated;

                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection(ghost1, ghost2);

                testWorld.CreateWorlds(true, 1);
                var serverCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var clientCollectionSingleton = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ClientWorlds[0]);
                // 第一个 Tick：在客户端与服务端分别计算 Ghost Collection Hash
                testWorld.Tick();
                Assert.AreEqual(GhostCollectionSystem.CalculateComponentCollectionHash(testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(serverCollectionSingleton)),
                    GhostCollectionSystem.CalculateComponentCollectionHash(testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostComponentSerializer.State>(clientCollectionSingleton)));

                // 比较已加载 Prefab 列表
                Assert.AreNotEqual(Entity.Null, serverCollectionSingleton);
                Assert.AreNotEqual(Entity.Null, clientCollectionSingleton);
                var serverCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefab>(serverCollectionSingleton);
                var clientCollection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefab>(clientCollectionSingleton);
                Assert.AreEqual(serverCollection.Length, clientCollection.Length);
                for (int i = 0; i < serverCollection.Length; ++i)
                {
                    Assert.AreEqual(serverCollection[i].GhostType, clientCollection[i].GhostType);
                    Assert.AreEqual(serverCollection[i].Hash, clientCollection[i].Hash);
                }

                // 检查客户端与服务端在 Component Hash 相同时可以连接
                testWorld.Connect();

                testWorld.GoInGame();
                for(int i=0;i<10;++i)
                    testWorld.Tick();

                Assert.IsTrue(testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]) != Entity.Null);
            }
        }

        [Test]
        public void DefaultVariantHashAreCalculatedCorrectly()
        {
            var realHash = GhostVariantsUtility.UncheckedVariantHash(typeof(LocalTransform).FullName, typeof(LocalTransform).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadWrite<LocalTransform>()));
            var compName = new FixedString512Bytes(typeof(LocalTransform).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(compName, compName));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(compName, ComponentType.ReadWrite<LocalTransform>()));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHashNBC(typeof(LocalTransform), ComponentType.ReadWrite<LocalTransform>()));
        }
        [Test]
        public void tVariantHashAreCalculatedCorrectly()
        {
            var realHash = GhostVariantsUtility.UncheckedVariantHash(typeof(TransformDefaultVariant).FullName, typeof(LocalTransform).FullName);
            var compName = new FixedString512Bytes(typeof(LocalTransform).FullName);
            var variantName = new FixedString512Bytes(typeof(TransformDefaultVariant).FullName);
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(variantName, compName));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHash(variantName, ComponentType.ReadWrite<LocalTransform>()));
            Assert.AreEqual(realHash, GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformDefaultVariant), ComponentType.ReadWrite<LocalTransform>()));
        }
        [Test]
        public void RuntimeAndCodeGeneratedVariantHashMatch()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                // 取得全部 Serializer，在本地重新计算 Hash 并验证结果一致
                // TODO 完整端到端测试仍缺少原始 Variant System.Type
                // 可在代码生成时以仅供测试和调试的字符串保存，或在登记 Serializer 时存储该类型
                // 该能力优先级不高，但有助于完善验证
                // 当前 Serializer 暴露 VariantTypeFullHashName，至少可以完成最关键的 Hash 一致性检查
                var data = testWorld.GetSingleton<GhostComponentSerializerCollectionData>(testWorld.ServerWorld);
                for (int i = 0; i < data.Serializers.Length; ++i)
                {
                    var variantTypeHash = data.Serializers.ElementAt(i).VariantTypeFullNameHash;
                    var componentType = data.Serializers.ElementAt(i).ComponentType;
                    var variantHash = GhostVariantsUtility.UncheckedVariantHash(variantTypeHash, componentType);
                    Assert.AreEqual(data.Serializers.ElementAt(i).VariantHash, variantHash,
                        $"Expect variant hash for code-generated serializer is identical to the" +
                        $"calculated at runtime for component {componentType.GetManagedType().FullName}." +
                        $"generated: {data.Serializers.ElementAt(i).VariantHash} runtime:{variantHash}");
                }
            }
        }
    }
#endif
}
