using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine.Scripting;

namespace Unity.NetCode.Tests
{
    internal class RpcTests
    {
        [Test]
        public void Rpc_UsingBroadcastOnClient_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 10;
                ClientRcpSendSystem.SendCount = SendCount;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 12; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        [Test]
        public void Rpc_UsingConnectionEntityOnClient_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 10;
                ClientRcpSendSystem.SendCount = SendCount;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                var remote = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                testWorld.ClientWorlds[0].GetExistingSystemManaged<ClientRcpSendSystem>().Remote = remote;

                for (int i = 0; i < 12; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        [Test]
        public void Rpc_SerializedRpcFlow_Works()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedClientRcpSendSystem),
                    typeof(SerializedServerRpcReceiveSystem),
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 1;
                var SendCmd = new SerializedRpcCommand
                { intValue = 123456, shortValue = 32154, floatValue = 12345.67f };
                SerializedClientRcpSendSystem.SendCount = SendCount;
                SerializedClientRcpSendSystem.Cmd = SendCmd;

                SerializedServerRpcReceiveSystem.ReceivedCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, SerializedServerRpcReceiveSystem.ReceivedCount);
                Assert.AreEqual(SendCmd, SerializedServerRpcReceiveSystem.ReceivedCmd);
            }
        }

        [Test]
        public void Rpc_ServerBroadcast_Works([Values(32, 64)] int windowSize)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverReliablePipelineWindowSize = windowSize;
                testWorld.Bootstrap(true,
                    typeof(ServerRpcBroadcastSendSystem),
                    typeof(MultipleClientBroadcastRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 2);

                ServerRpcBroadcastSendSystem.SendCount = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0] = 0;
                MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1] = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                int SendCount = 5;
                ServerRpcBroadcastSendSystem.SendCount = SendCount;

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[0]);
                Assert.AreEqual(SendCount, MultipleClientBroadcastRpcReceiveSystem.ReceivedCount[1]);
            }
        }

        public static readonly SharedStatic<int> VariableSizedResultCnt = SharedStatic<int>.GetOrCreate<VariableSizedRpc>();

        /// <summary>
        /// 从 1.3.x 起正式支持
        /// </summary>
        [Test]
        public void Rpc_VariableSizedCompression_Works([Values] bool useDynamicAssemblyList)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);
                testWorld.GetSingletonRW<RpcCollection>(testWorld.ServerWorld).ValueRW.RegisterRpc<VariableSizedRpc, VariableSizedRpc>();
                testWorld.GetSingletonRW<RpcCollection>(testWorld.ClientWorlds[0]).ValueRW.RegisterRpc<VariableSizedRpc, VariableSizedRpc>();
                testWorld.Connect();

                // 不创建 RPC 实体，直接通过队列发送
                const int sendCount = 35;
                var rpcQueue = testWorld.GetSingletonRW<RpcCollection>(testWorld.ClientWorlds[0]).ValueRW.GetRpcQueue<VariableSizedRpc>();
                var outBuf = testWorld.GetSingletonBuffer<OutgoingRpcDataStreamBuffer>(testWorld.ClientWorlds[0]);
                VariableSizedResultCnt.Data = 0;
                for (int i = 0; i < sendCount; i++)
                {
                    rpcQueue.Schedule(outBuf, default, new VariableSizedRpc
                    {
                        Value1 = VariableSizedRpc.Value1Multiplier * i,
                        Value2 = VariableSizedRpc.Value2Multiplier * i,
                        Value3 = VariableSizedRpc.Value3Multiplier * i,
                    });
                }
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 具体数据断言在 VariableSizedRpc.Execute 中执行
                Assert.AreEqual(sendCount, VariableSizedResultCnt.Data);
            }
        }

        // TODO 引入连接审批 RPC 后该行为已不再成立
        // [Test]
        // public void Rpc_SendingBeforeGettingNetworkId_LogWarning()
        // {
        //     using (var testWorld = new NetCodeTestWorld())
        //     {
        //         testWorld.Bootstrap(true,
        //             typeof(FlawedClientRcpSendSystem),
        //             typeof(ServerRpcReceiveSystem),
        //             typeof(NonSerializedRpcCommandRequestSystem));
        //         testWorld.CreateWorlds(true, 1);
        //
        //         int SendCount = 1;
        //         ServerRpcReceiveSystem.ReceivedCount = 0;
        //         FlawedClientRcpSendSystem.SendCount = SendCount;
        //
        //         // 建立连接并确认连接成功
        //         testWorld.Connect();
        //
        //         LogAssert.Expect(LogType.Warning, new Regex("Cannot send RPC with no remote connection."));
        //         for (int i = 0; i < 33; ++i)
        //             testWorld.Tick();
        //
        //         Assert.AreEqual(0, ServerRpcReceiveSystem.ReceivedCount);
        //     }
        // }


        [Test]
        [Ignore("Need significant package hardening to make guarantees about what happens when fuzzing packets!. Tracked as MTT-11334")]
        // TODO 对 Ghost 和输入执行模糊测试
        // TODO 对玩法样例执行模糊测试，确认不会破坏服务器或其他客户端
        // TODO 验证异常客户端最终会被断开
        // TODO 对服务器数据包执行模糊测试，确认客户端具备合理容错能力
        public void Rpc_MalformedPackets_ThrowsAndLogError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverRandomSeed = 0xbadc0de;
                testWorld.DriverFuzzOffset = 1; // TODO 理想值应为零
                testWorld.DriverFuzzFactor = new int[2];
                testWorld.DriverFuzzFactor[0] = 10;
                testWorld.Bootstrap(true,
                    typeof(MalformedClientRcpSendSystem),
                    typeof(ServerMultipleRpcReceiveSystem),
                    typeof(MultipleClientSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 2);

                int SendCount = 15;
                MalformedClientRcpSendSystem.SendCount[0] = SendCount;
                MalformedClientRcpSendSystem.SendCount[1] = SendCount;
                MalformedClientRcpSendSystem.Cmds[0] = new ClientIdRpcCommand { Id = 0 };
                MalformedClientRcpSendSystem.Cmds[1] = new ClientIdRpcCommand { Id = 1 };

                ServerMultipleRpcReceiveSystem.ReceivedCount[0] = 0;
                ServerMultipleRpcReceiveSystem.ReceivedCount[1] = 0;

                // 数据包模糊测试可能产生错误、警告、跟踪日志或 Tick 与大小序列化异常
                // 某些损坏也可能完全没有可见错误
                // 例如 RPC Header 大小变化后，损坏的包索引可能恰好指向序列化布局相同的另一种 RPC
                // 此时测试会静默成功且没有序列化错误，只能从计数异常推断问题
                LogAssert.ignoreFailingMessages = true;

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // TODO 以下两项检查无效，因为模糊测试可能将 ClientIdRpcCommand.Id 从零改为一或反向修改
                // 这会使双方计数都超出预期，且当前健壮性不足时还可能抛出异常
                Debug.Log($"Received: [0]={ServerMultipleRpcReceiveSystem.ReceivedCount[0]}, [1]={ServerMultipleRpcReceiveSystem.ReceivedCount[0]}");
                //Assert.Less(ServerMultipleRpcReceiveSystem.ReceivedCount[0], SendCount);
                //Assert.AreEqual(SendCount, ServerMultipleRpcReceiveSystem.ReceivedCount[1]);
            }
        }

        [Test]
        public void Rpc_IndividualRpcTooLarge_ThrowsAndLogError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();

                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var entity = clientEm.CreateEntity(typeof(SerializedTooBigCommand), typeof(SendRpcCommandRequest));
                var serializedTooBigCommand = default(SerializedTooBigCommand);
                unsafe
                {
                    var ptr0 = (int*)&serializedTooBigCommand.bytes0;
                    for (int i = 0; i < sizeof(FixedBytes4094)/4; i++) ptr0[i] = i;
                    var ptr1 = (int*)&serializedTooBigCommand.bytes1;
                    for (int i = 0; i < sizeof(FixedBytes4094)/4; i++) ptr1[i] = i;
                    var ptr2 = (int*)&serializedTooBigCommand.bytes3;
                    for (int i = 0; i < sizeof(FixedBytes126)/4; i++) ptr2[i] = i;
                }
                clientEm.SetComponentData(entity, serializedTooBigCommand);

                LogAssert.Expect(LogType.Exception, new Regex("is too large to serialize into the RpcQueue!"));
                for (int i = 0; i < 24; ++i)
                    testWorld.Tick();
            }
        }

        [Test]
        public void Rpc_IndividualRpcIncorrectDeserialization_ThrowsAndLogError([Values] bool useDynamicAssemblyList, [Values] IncorrectDeserializationCommand.IncorrectMode incorrectDeserializationMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(IncorrectDeserializationCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);
                testWorld.Connect();

                var clientEm = testWorld.ClientWorlds[0].EntityManager;
                var clientEntity = clientEm.CreateEntity();
                var command = new IncorrectDeserializationCommand
                {
                    bytes = 999,
                    mode = incorrectDeserializationMode,
                };
                Assert.IsTrue(clientEm.AddComponentData(clientEntity, command));
                Assert.IsTrue(clientEm.AddComponent<SendRpcCommandRequest>(clientEntity));

                if (incorrectDeserializationMode == IncorrectDeserializationCommand.IncorrectMode.DeserializeTooManyBytes)
                    LogAssert.Expect(LogType.Error, new Regex(@"Trying to read \d bytes from a stream where only \d are available"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[ServerTest(.*)\](.*)RpcSystem failed to deserialize RPC(.*)as bits read(.*)did not match expected"));
                // 即使反序列化失败，系统仍会创建收到的 RPC 实体
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ServerWorld), "Expected no connection left!");
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]), "Expected no connection left!");
            }
        }

        [Test]
        public void Rpc_CanSendMoreThanOnePacketPerFrame([Values] bool useDynamicAssemblyList, [Values(2, 100)] int sendCount, [Values(32, 64)] int windowSize)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverReliablePipelineWindowSize = windowSize;
                testWorld.Bootstrap(true,
                    typeof(SerializedClientLargeRcpSendSystem),
                    typeof(SerializedServerLargeRpcReceiveSystem),
                    typeof(SerializedLargeRpcCommandRequestSystem));

                testWorld.CreateWorlds(true, 1, false);
                testWorld.SetDynamicAssemblyList(useDynamicAssemblyList);

                var SendLargeCmd = new SerializedLargeRpcCommand
                { stringValue = new FixedString512Bytes("baaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaavaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac") };
                var SendSmallCmd = new SerializedSmallRpcCommand { Value = 8 };
                SerializedClientLargeRcpSendSystem.SendCount = sendCount;
                SerializedClientLargeRcpSendSystem.LargeCmd = SendLargeCmd;
                SerializedClientLargeRcpSendSystem.SmallCmd = SendSmallCmd;

                SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount = 0;
                SerializedServerLargeRpcReceiveSystem.ReceivedSmallCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                var numTicks = Mathf.Max(2, sendCount * .1f);
                for (int i = 0; i < numTicks; ++i)
                    testWorld.Tick();

                Assert.AreEqual(sendCount, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount);
                Assert.AreEqual(sendCount, SerializedServerLargeRpcReceiveSystem.ReceivedSmallCount);
                Assert.AreEqual(SendLargeCmd, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCmd);
                Assert.AreEqual(SendSmallCmd, SerializedServerLargeRpcReceiveSystem.ReceivedSmallCmd);
            }
        }

        [Test]
        public void Rpc_IsRemovedWithConnectionDeletion()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var client = testWorld.ClientWorlds[0];

                // 从客户端向服务器发送 RPC
                var rpcData = new SerializedRpcCommand
                    {intValue = 12345, shortValue = 12345, floatValue = 123.45f};
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                for (int i = 0; i < 2; ++i)
                    testWorld.Tick();

                // 服务器此时尚未创建 RPC 实体
                var rpcReqQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ReceiveRpcCommandRequest>(), ComponentType.ReadOnly<SerializedRpcCommand>());
                Assert.AreEqual(0, rpcReqQuery.CalculateEntityCount());

                testWorld.Tick();

                // 服务器此时已经收到 RPC
                Assert.AreEqual(1, rpcReqQuery.CalculateEntityCount());

                // 在服务器端直接断开客户端
                var clientConnectionOnServer = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ServerWorld);
                testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ServerWorld).DriverStore.Disconnect(clientConnectionOnServer.ValueRO);

                var clientConnectionQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(1, clientConnectionQuery.CalculateEntityCount());

                testWorld.Tick();

                // 源连接删除后 RPC 实体也应完成清理
                Assert.AreEqual(0, rpcReqQuery.CalculateEntityCount());
            }
        }

        [Test]
        public void Rpc_IsRemovedWithConnectionDeletionInSystem()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(ClientRcpSendSystem),
                    typeof(ServerRpcReceiveSystem),
                    typeof(NonSerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);

                testWorld.Connect();

                ClientRcpSendSystem.SendCount = 1;
                ServerRpcReceiveSystem.ReceivedCount = 0;

                // 客户端在 ClientRcpSendSystem 内发送 RPC
                testWorld.Tick();
                Assert.AreEqual(0, ClientRcpSendSystem.SendCount);

                // 客户端触发断开
                // 若 NetworkGroupCommandBufferSystem.PatchConnectionEvents 未执行清理，服务器会继续处理该 RPC
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                var clientConnection = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ClientWorlds[0]).DriverStore.Disconnect(clientConnection.ValueRO);

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                // 验证客户端已经断开
                var clientConnectionQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                Assert.AreEqual(0, clientConnectionQuery.CalculateEntityCount());

                // 验证服务器从未收到该 RPC
                Assert.AreEqual(0, ServerRpcReceiveSystem.ReceivedCount);
            }
        }

        // RPC 在服务器上的接收和处理位置
        internal enum SystemSetup
        {
            UpdateBeforeNetworkECB,
            UpdateAfterNetworkECB,
            UpdateInSimulation
        }

        // 系统触发连接的位置
        internal enum ConnectSetup
        {
            BeforeNetworkECB,
            AfterNetworkECB
        }

        // 系统触发断开的位置
        internal enum DisconnectSetup
        {
            BeforeNetworkECB,
            AfterNetworkECB
        }

        // 本测试直接触发连接和断开，下一项测试则由特定更新位置的系统触发
        // 预期可能出现连接尚未完成就断开，以及连接断开后 SendRpcData Job 仍运行的警告
        // 这些情况来自同一帧发送 RPC 后立即断开或快速重连
        [Test]
        public void Rpc_IsCleanedUpWithFastReconnectManual(
            [Values] bool useApproval,
            [Values] SystemSetup systemSetup)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var updateSystem = typeof(ReceiveFastReconnectRpcBefore);
                if (systemSetup == SystemSetup.UpdateAfterNetworkECB)
                    updateSystem = typeof(ReceiveFastReconnectRpcAfter);
                if (systemSetup == SystemSetup.UpdateInSimulation)
                    updateSystem = typeof(ReceiveFastReconnectRpc);

                testWorld.Bootstrap(true,
                    typeof(SendFastReconnectRpc), typeof(SendFastReconnectApprovalRpc), typeof(ReceiveFastReconnectApprovalRpc), updateSystem);

                testWorld.CreateWorlds(true, 1, true);

                // 使用不同 Tick 间隔和执行位置反复连接、断开并重连
                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                for (int ticksBeforeDisconnecting = 0; ticksBeforeDisconnecting < 8; ticksBeforeDisconnecting++)
                {
                    // 断开与重连之间至少间隔一个 Tick，否则会被视为在已连接状态再次连接
                    for (int ticksBeforeReconnecting = 1; ticksBeforeReconnecting < 8; ticksBeforeReconnecting++)
                    {
                        testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.Connect(testWorld.ClientWorlds[0].EntityManager, ep);

                        for (int i = 0; i < ticksBeforeDisconnecting; i++)
                            testWorld.Tick();

                        testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                        var clientConnection = testWorld.GetSingletonRW<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                        testWorld.GetSingleton<NetworkStreamDriver>(testWorld.ClientWorlds[0]).DriverStore.Disconnect(clientConnection.ValueRO);

                        for (int i = 0; i < ticksBeforeReconnecting; i++)
                            testWorld.Tick();
                    }
                }
            }
        }

        [Test]
        public void Rpc_IsCleanedUpWithFastReconnectInSystems(
            [Values] bool useApproval,
            [Values] SystemSetup systemSetup,
            [Values] ConnectSetup connectSetup,
            [Values] DisconnectSetup disconnectSetup)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var updateSystem = typeof(ReceiveFastReconnectRpcBefore);
                if (systemSetup == SystemSetup.UpdateAfterNetworkECB)
                    updateSystem = typeof(ReceiveFastReconnectRpcAfter);
                if (systemSetup == SystemSetup.UpdateInSimulation)
                    updateSystem = typeof(ReceiveFastReconnectRpc);
                var connectSystem = typeof(FastReconnectRpcConnectAfterSystem);
                if (connectSetup == ConnectSetup.BeforeNetworkECB)
                    connectSystem = typeof(FastReconnectRpcConnectBeforeSystem);
                var disconnectSystem = typeof(FastReconnectRpcDisconnectAfterSystem);
                if (disconnectSetup == DisconnectSetup.BeforeNetworkECB)
                    disconnectSystem = typeof(FastReconnectRpcDisconnectBeforeSystem);

                testWorld.Bootstrap(true,
                    typeof(SendFastReconnectRpc), typeof(SendFastReconnectApprovalRpc), typeof(ReceiveFastReconnectApprovalRpc),
                    updateSystem, connectSystem, disconnectSystem);

                // 创建 World 后不预先推进 Tick，以覆盖立即连接流程
                testWorld.CreateWorlds(true, 1, false);

                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);

                for (int ticksBeforeReconnecting = 1; ticksBeforeReconnecting < 3; ticksBeforeReconnecting++)
                {
                    for (int ticksBeforeDisconnecting = 0; ticksBeforeDisconnecting < 7; ticksBeforeDisconnecting++)
                    {
                        // 立即连接
                        FastReconnectRpcConnectAfterSystem.ConnectNow = true;
                        FastReconnectRpcConnectBeforeSystem.ConnectNow = true;

                        // 从零帧开始测试不同等待时间，因为 Connect 与 Disconnect 可以在同一帧调用
                        FastReconnectRpcDisconnectAfterSystem.DisconnectDelay = ticksBeforeDisconnecting;
                        FastReconnectRpcDisconnectBeforeSystem.DisconnectDelay = ticksBeforeDisconnecting;

                        // 推进对应数量的 Tick
                        for (int i = 0; i < ticksBeforeDisconnecting + ticksBeforeReconnecting; i++) testWorld.Tick();
                    }
                }
            }
        }

        [Test]
        public void Rpc_CanPackMultipleRPCs()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedClientLargeRcpSendSystem),
                    typeof(SerializedServerLargeRpcReceiveSystem),
                    typeof(SerializedLargeRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1);

                int SendCount = 500;
                var SendCmd = new SerializedLargeRpcCommand
                { stringValue = new FixedString512Bytes("\0\0\0\0\0\0\0\0\0\0") };
                SerializedClientLargeRcpSendSystem.SendCount = SendCount;
                SerializedClientLargeRcpSendSystem.LargeCmd = SendCmd;

                SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                Assert.AreEqual(SendCount, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCount);
                Assert.AreEqual(SendCmd, SerializedServerLargeRpcReceiveSystem.ReceivedLargeCmd);
            }
        }

        internal class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner());
            }
        }

        [Test]
        public void Rpc_CanSendEntityFromClientAndServer()
        {
            void SendRpc(World world, Entity entity)
            {
                var req = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(req, new RpcWithEntity { entity = entity });
                world.EntityManager.AddComponentData(req, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            }

            RpcWithEntity RecvRpc(World world)
            {
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RpcWithEntity>());
                Assert.AreEqual(1, query.CalculateEntityCount());
                var rpcReceived = query.GetSingleton<RpcWithEntity>();
                world.EntityManager.DestroyEntity(query);
                return rpcReceived;
            }


            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(RpcWithEntityRpcCommandRequestSystem));
                var ghostGameObject = new GameObject("SimpleGhost");
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                // 进入游戏状态
                testWorld.GoInGame();

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                // 推进若干帧以便客户端也生成 Ghost
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                var recvGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
                // 取得对应的客户端实体
                var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var clientEntity));

                // 向服务器发送 RPC
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                var rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == serverEntity);

                // 服务器向客户端发送 RPC
                SendRpc(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity != Entity.Null);
                Assert.IsTrue(rpcReceived.entity == clientEntity);

                // 客户端发送仅本地存在的实体引用，服务器应解析为 Entity.Null
                // 向服务器发送 RPC
                var clientOnlyEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity();
                SendRpc(testWorld.ClientWorlds[0], clientOnlyEntity);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);

                // 以下覆盖实体正处于 Despawn 过程的边界情况
                // 客户端实体已销毁或即将销毁时，服务器应在 RPC 中收到 Entity.Null
                // 服务器已销毁但客户端尚未销毁时，Ghost 映射重置窗口内服务器也无法解析实体

                // 在服务器销毁实体
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverEntity);
                // 让客户端继续为该实体发送 RPC，以模拟网络延迟
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                // 服务器实体已失去 GhostInstance，此时发送 RPC 会将引用转换为 Entity.Null
                SendRpc(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                // 服务器不应解析出实体引用
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                // 客户端收到的引用也必须为 Entity.Null
                rpcReceived = RecvRpc(testWorld.ClientWorlds[0]);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
                var sendGhostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ServerWorld);
                // 此时实体已不存在，客户端和服务器的映射都应完成重置
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(recvGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var _));
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.GetComponentData<SpawnedGhostEntityMap>(sendGhostMapSingleton).Value
                    .TryGetValue(new SpawnedGhost { ghostId = ghost.ghostId, spawnTick = ghost.spawnTick }, out var _));
                SendRpc(testWorld.ClientWorlds[0], clientEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                // 收到的实体引用必须为 Entity.Null
                rpcReceived = RecvRpc(testWorld.ServerWorld);
                Assert.IsTrue(rpcReceived.entity == Entity.Null);
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS && !NETCODE_NDEBUG
        [Test]
        public void Rpc_WarnIfSendingApprovalRpcWithoutApprovalRequired([Values]bool suppressWarning)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);
                testWorld.Connect();
                Debug.Assert(testWorld.TrySuppressNetDebug(true, suppressWarning), "Sanity check");

                var client = testWorld.ClientWorlds[0];
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponent<MyApprovalRpc>(rpcEntity);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                if(!suppressWarning)
                    LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Sending approval RPC '(.*)' to the server but connection approval is disabled"));
                testWorld.Tick();
                LogAssert.NoUnexpectedReceived();
            }
        }

        /* 测试各种非法 RPC 发送场景
         * 未启用连接审批时
         *   - 连接已开始但尚未完成且没有 NetworkId 时不能发送
         *   - 禁用审批时不能发送 IApprovalRpc
         * 启用连接审批时
         *   - 审批完成前没有 NetworkId，不能发送普通 RPC
         * 两种模式均适用
         *   - 尚未建立任何连接时不能发送
         *   - 目标连接没有 RPC 发送缓冲时不能发送
         */
        [Test]
        public void Rpc_WarnIfSendingBeforeConnectionEstablished([Values]bool useApprovalRpc)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(SerializedRpcCommandRequestSystem));
                testWorld.CreateWorlds(true, 1, true);

                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.RequireConnectionApproval = useApprovalRpc;

                // 从客户端向服务器发送 RPC
                var rpcData = new SerializedRpcCommand
                    {intValue = 12345, shortValue = 12345, floatValue = 123.45f};
                var client = testWorld.ClientWorlds[0];
                var rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                testWorld.Tick();
                LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as not connected"));
                // 开始建立连接以执行下一阶段测试
                var ep = NetworkEndpoint.LoopbackIpv4.WithPort(7979);
                testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ServerWorld).ValueRW.Listen(ep);
                var connectionEntity = testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);

                if (useApprovalRpc)
                {
                    for (int i = 0; i < 2; ++i)
                        testWorld.Tick();

                    // 验证连接当前处于 Handshake 状态
                    client.EntityManager.CompleteAllTrackedJobs();
                    var clientConnectionOnClient = testWorld.GetSingleton<NetworkStreamConnection>(client);
                    Assert.AreEqual(ConnectionState.State.Handshake, clientConnectionOnClient.CurrentState);

                    // 在连接审批完成前再次尝试发送
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as it is not an Approval RPC, and its NetworkConnection(.*) - on Entity(.*) - is in state `Handshake`"));
                    testWorld.Tick();

                    // 改为指定目标连接而非广播
                    var clientConnectionToServer = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(client);
                    Assert.AreNotEqual(Entity.Null, clientConnectionToServer);
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = clientConnectionToServer});

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as it is not an Approval RPC, and its NetworkConnection(.*) - on Entity(.*) - is in state `Handshake`"));
                    testWorld.Tick();

                    // 断开连接以使连接实体失效
                    client.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);
                    for (int i = 0; i < 4; ++i)
                        testWorld.Tick();
                    testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);
                }
                else
                {
                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = connectionEntity});

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    // 连接仍在建立中且尚未收到 NetworkId
                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its NetworkConnection(.*) - on Entity(.*) - is in state `Connecting`"));
                    // 验证连接随后成功建立
                    Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkId>(client));

                    // 断开后使用广播 RPC 再次测试
                    client.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    testWorld.GetSingletonRW<NetworkStreamDriver>(client).ValueRW.Connect(client.EntityManager, ep);

                    rpcEntity = client.EntityManager.CreateEntity();
                    client.EntityManager.AddComponentData(rpcEntity, rpcData);
                    client.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

                    for (int i = 0; i < 5; ++i)
                        testWorld.Tick();

                    LogAssert.Expect(LogType.Error, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its NetworkConnection(.*) - on Entity(.*) - is in state `Connecting`"));
                    Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<NetworkId>(client));
                }

                // 尝试向无效连接实体发送 RPC
                rpcEntity = client.EntityManager.CreateEntity();
                client.EntityManager.AddComponentData(rpcEntity, rpcData);
                client.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest(){TargetConnection = connectionEntity});

                LogAssert.Expect(LogType.Warning, new Regex(@"\[ClientTest0(.*)\] Cannot send RPC '(.*)' to the server as its connection entity \(Entity(.*)\) does not have a `NetworkStreamConnection` or `OutgoingRpcDataStreamBuffer` component"));
                testWorld.Tick();
            }
        }

        [Test]
        public void WarnsIfApplicationRunInBackgroundIsFalse()
        {
            var existingRunInBackground = Application.runInBackground;
            try
            {
                using var testWorld = new NetCodeTestWorld();
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                Application.runInBackground = false;
                testWorld.Connect();
                // 默认抑制该警告
                testWorld.Tick();
                // 取消警告抑制
                Assert.IsTrue(testWorld.TrySuppressNetDebug(false, true), "Failed to un-suppress!");
                // 客户端和服务器 World 应各记录一次错误
                var regex = new Regex(@"Netcode detected that you don't have Application\.runInBackground enabled.*Project Settings > Player > Resolution and Presentation > Run in Background");
                LogAssert.Expect(LogType.Error, regex);
                LogAssert.Expect(LogType.Error, regex);
                testWorld.Tick();
                // 客户端断开后不应继续警告
                testWorld.DisposeServerWorld();
                testWorld.Tick();
            }
            finally
            {
                Application.runInBackground = existingRunInBackground;
            }
        }

        [Test]
        public void Rpc_SendingRPCLargerThanMaxMessageSizeGivesTheCorrectError()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverMaxMessageSize = 548;
                testWorld.Bootstrap(true,
                    typeof(VeryLargeRcpSendSystem),
                    typeof(VeryLargeRpcReceiveSystem));
                testWorld.CreateWorlds(true, 1);

                FixedString512Bytes largeString = "";
                for ( int i=0; i<FixedString512Bytes.UTF8MaxLengthInBytes; ++i )
                {
                    if (i == FixedString512Bytes.UTF8MaxLengthInBytes - 1 )
                    {
                        largeString += "\0";
                    }
                    else
                    {
                        largeString += "a";
                    }
                }

                int SendCount = 1;
                var SendCmd = new VeryLargeRPC
                { value = new FixedString512Bytes(largeString),
                value1 = new FixedString512Bytes(largeString)};
                VeryLargeRcpSendSystem.SendCount = SendCount;
                VeryLargeRcpSendSystem.Cmd = SendCmd;

                VeryLargeRpcReceiveSystem.ReceivedCount = 0;

                // 建立连接并确认连接成功
                testWorld.Connect();

                for (int i = 0; i < 33; ++i)
                    testWorld.Tick();

                var regex = new Regex(@"Reduce the size of this RPC payload!");
                LogAssert.Expect(LogType.Exception, regex);
            }
        }

        [Test]
        public void Rpc_WarnsIfNotConsumedAfter4Frames([Values]bool enabled)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                // 在客户端和服务器直接创建未消费的 RPC，以隔离完整 RPC 流程的其他依赖
                var clientWorld = testWorld.ClientWorlds[0];
                var clientNetDebug = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>()).GetSingleton<NetDebug>();
                clientNetDebug.LogLevel = NetDebug.LogLevelType.Warning;
                testWorld.GetSingletonRW<NetDebug>(clientWorld).ValueRW.MaxRpcAgeFrames = (ushort) (enabled ? 4 : 0);
                clientWorld.EntityManager.CreateEntity(ComponentType.ReadWrite<ReceiveRpcCommandRequest>());

                var serverWorld = testWorld.ServerWorld;
                var serverNetDebug = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetDebug>()).GetSingleton<NetDebug>();
                serverNetDebug.LogLevel = NetDebug.LogLevelType.Warning;
                testWorld.GetSingletonRW<NetDebug>(serverWorld).ValueRW.MaxRpcAgeFrames = (ushort) (enabled ? 4 : 0);
                serverWorld.EntityManager.CreateEntity(ComponentType.ReadWrite<ReceiveRpcCommandRequest>());

                // 先推进三个 Tick，尚未达到警告阈值
                testWorld.Tick();
                testWorld.Tick();
                testWorld.Tick();

                // 随后验证客户端和服务器达到阈值时分别记录警告，服务器晚一帧
                var regex = new Regex(@"NetCode RPC Entity\(\d*\:\d*\) has not been consumed or destroyed for '4'");
                if(enabled) LogAssert.Expect(LogType.Warning, regex);
                testWorld.Tick();
                if(enabled) LogAssert.Expect(LogType.Warning, regex);
                testWorld.Tick();
                // 每个 RPC 只警告一次
                testWorld.Tick();
                testWorld.Tick();
            }
        }
#endif
    }
}
