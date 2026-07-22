using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Tests
{
    class SendToOwnerTests
    {
        internal class TestComponentConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent<GhostOwner>(entity);
                baker.AddComponent<GhostPredictedOnly>(entity);
                baker.AddComponent<GhostInterpolatedOnly>(entity);
                baker.AddComponent<GhostGen_IntStruct>(entity);
                baker.AddComponent<GhostTypeIndex>(entity);
                baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                baker.AddBuffer<GhostGenTest_Buffer>(entity);
            }
        }

        void ChangeSendToOwnerOption(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var entity = query.GetSingletonEntity();
            var collection = world.EntityManager.GetBuffer<GhostComponentSerializer.State>(entity);
            for (int i = 0; i < collection.Length; ++i)
            {
                var c = collection[i];
                if (c.ComponentType.GetManagedType() == typeof(GhostGen_IntStruct))
                {
                    c.SendToOwner = SendToOwnerType.SendToOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostTypeIndex))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostPredictedOnly))
                {
                    c.SendToOwner = SendToOwnerType.SendToOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostInterpolatedOnly))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostGenTest_Buffer))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
            }
        }

        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Interpolated, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void SendToOwner_Clients_ReceiveTheCorrectData(GhostModeMask modeMask, GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestComponentConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                // 以下说明不同 Ghost 模式中 Owner 的含义
                // 插值 Ghost 也可以有 Owner，通常由服务器持有，但也可以归属于玩家
                // 玩家仍可通过 Command 控制它，但客户端不会预测其移动
                // 只有服务器计算权威位置，客户端始终看到延迟且经过插值的副本
                // 预测 Ghost 使用 Owner 具有明确意义
                // OwnerPredicted 模式则按定义依赖 Owner
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 2);
                // 等待 CollectionSystem 运行并完成组件集合构建
                // 随后修改序列化器标志以构造测试所需的发送行为
                // 这是临时处理，支持按 Prefab 覆盖配置后即可移除
                using var queryServer = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient0 = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient1 = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                while (true)
                {
                    testWorld.Tick();
                    if (queryServer.IsEmptyIgnoreFilter || queryClient0.IsEmptyIgnoreFilter || queryClient1.IsEmptyIgnoreFilter)
                        continue;
                    if (testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0)
                        continue;
                    // intStruct 仅发送给 Owner
                    // GhostTypeIndex 仅发送给 NonOwner
                    // GhostPredictedOnly 仅发送给 Owner
                    // GhostInterpolatedOnly 仅发送给 NonOwner
                    // GhostGenBuffer_ByteBuffer 仅发送给 NonOwner
                    ChangeSendToOwnerOption(testWorld.ServerWorld);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[0]);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[1]);
                    break;
                }

                testWorld.Connect();
                testWorld.GoInGame();
                var serverEntities = new NativeArray<Entity>(10, Allocator.Temp);

                for (int ent = 0; ent < 10; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    serverEntities[ent] = serverEnt;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 10000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostTypeIndex {Value = 20000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostPredictedOnly {Value = 30000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostInterpolatedOnly {Value = 40000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = ent/5 + 1});
                    var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    serverBuffer1.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer1.Add(new GhostGenBuffer_ByteBuffer{Value = (byte)(10 + i)});
                    var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                    serverBuffer2.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer2.Add(new GhostGenTest_Buffer());
                }

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                for (int i = 0; i < 2; ++i)
                {
                    var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                    for (int ent = 0; ent < 10; ++ent)
                    {
                        var serverEnt = serverEntities[ent];
                        var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                        var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                        var serverComp1 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGen_IntStruct>(serverEnt);
                        var serverComp2 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostTypeIndex>(serverEnt);
                        var predictedOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostPredictedOnly>(serverEnt);
                        var interpOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInterpolatedOnly>(serverEnt);


                        var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                        spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId,ghost.spawnTick), out var clientEnt);
                        var clientComp1_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostGen_IntStruct>(clientEnt);
                        var clientComp2_NonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostTypeIndex>(clientEnt);
                        var clientPredOnly_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostPredictedOnly>(clientEnt);
                        var clientInterpOnly_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostInterpolatedOnly>(clientEnt);

                        var clientBuffer1 = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEnt);
                        var clientBuffer2_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEnt);

                        Assert.AreEqual(ent/5==i,serverComp1.IntValue == clientComp1_ToOwner.IntValue,$"Client {i}");
                        Assert.AreEqual(ent/5!=i,serverComp2.Value == clientComp2_NonOwner.Value,$"Client {i}");

                        // 这些组件由 SendToOwner 决定每个客户端是否接收有效数据
                        if (mode == GhostMode.Predicted)
                        {
                            Assert.AreEqual(ent/5==i,predictedOnly.Value == clientPredOnly_ToOwner.Value, $"Client {i}");
                            Assert.AreEqual(false,interpOnly.Value == clientInterpOnly_ToNonOwner.Value,  $"Client {i}");
                        }
                        else if (mode == GhostMode.Interpolated)
                        {
                            Assert.AreEqual(false,predictedOnly.Value == clientPredOnly_ToOwner.Value, $"Client {i}");
                            Assert.AreEqual(ent/5!=i,interpOnly.Value == clientInterpOnly_ToNonOwner.Value, $"Client {i}");
                        }
                        else if(mode == GhostMode.OwnerPredicted)
                        {
                            Assert.AreEqual(ent/5==i,predictedOnly.Value == clientPredOnly_ToOwner.Value,$"Client {i}");
                            Assert.AreEqual(ent/5!=i,interpOnly.Value == clientInterpOnly_ToNonOwner.Value,$"Client {i}");
                        }
                        Assert.AreEqual(true, 10 ==clientBuffer1.Length);
                        Assert.AreEqual(ent/5!=i,10 ==clientBuffer2_ToNonOwner.Length);
                        Assert.AreEqual(ent/5==i,0 ==clientBuffer2_ToNonOwner.Length);
                        for (int k = 0; k < clientBuffer1.Length; ++k)
                            Assert.AreEqual(serverBuffer1[k].Value, clientBuffer1[k].Value,$"Client {i}");
                        for (int k = 0; k < clientBuffer2_ToNonOwner.Length; ++k)
                            Assert.AreEqual(serverBuffer2[k].IntValue, clientBuffer2_ToNonOwner[k].IntValue,$"Client {i}");
                    }
                }
            }
        }

        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void HistoryBackup_RespectSendToOwnerSemantic(GhostModeMask modeMask, GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestComponentConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 2);
                // 等待 CollectionSystem 运行并完成组件集合构建
                // 随后修改序列化器标志以构造测试所需的发送行为
                // 这是临时处理，支持按 Prefab 覆盖配置后即可移除
                using var queryServer = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient0 = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient1 = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                while (true)
                {
                    testWorld.Tick();
                    if (queryServer.IsEmptyIgnoreFilter || queryClient0.IsEmptyIgnoreFilter || queryClient1.IsEmptyIgnoreFilter)
                        continue;
                    if (testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0)
                        continue;
                    // intStruct 仅发送给 Owner
                    // GhostTypeIndex 仅发送给 NonOwner
                    // GhostPredictedOnly 仅发送给 Owner
                    // GhostInterpolatedOnly 仅发送给 NonOwner
                    // GhostGenBuffer_ByteBuffer 仅发送给 NonOwner
                    ChangeSendToOwnerOption(testWorld.ServerWorld);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[0]);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[1]);
                    break;
                }

                testWorld.Connect();
                testWorld.GoInGame();
                var serverEntities = new NativeArray<Entity>(10, Allocator.Temp);

                for (int ent = 0; ent < 10; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    serverEntities[ent] = serverEnt;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 10000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostTypeIndex {Value = 20000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostPredictedOnly {Value = 30000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostInterpolatedOnly {Value = 40000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = ent/5 + 1});
                    var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    serverBuffer1.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer1.Add(new GhostGenBuffer_ByteBuffer{Value = (byte)(10 + i)});
                    var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                    serverBuffer2.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer2.Add(new GhostGenTest_Buffer());
                }

                // 生成测试实体
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                // 执行部分 Tick，确保最近一次预测 Tick 不完整，从而强制后续 Tick 尝试从备份恢复
                // 如果最近一次预测 Tick 完整，且假定组件值没有变化，那么由于这里在预测循环外修改数据
                // Ghost 更新系统不会尝试从备份恢复，这种行为容易造成误解
                testWorld.Tick((1f/60)/2f);

                // 验证数据已经同步且 Owner 标志生效，从而避免不应同步的组件被服务器权威值覆盖
                for (int tick = 0; tick < 4; ++tick)
                {
                    // 在部分 Tick 前覆盖所有组件值并验证以下行为
                    // 符合 Owner 或 NonOwner 发送条件的数据会恢复为权威值
                    // 发送给 Owner 的预测数据同样会恢复为权威值
                    for (int i = 0; i < 2; ++i)
                    {
                        var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                        for (int ent = 0; ent < 10; ++ent)
                        {
                            var serverEnt = serverEntities[ent];
                            var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                            spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId, ghost.spawnTick),
                                out var clientEnt);
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostGen_IntStruct
                            {
                                IntValue = 1 + tick * 1000
                            });
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostTypeIndex
                            {
                                Value = 1 + tick * 1000
                            });
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostPredictedOnly
                            {
                                Value = 1 + tick * 1000
                            });
                        }
                    }
                    // 修改未同步给对应 Owner 或 NonOwner 的组件数据，并验证部分 Tick 不会回滚这些数据
                    testWorld.Tick((1f/60)/4f);
                    // 仅向 Owner 或 NonOwner 同步的数据不会为不符合发送条件的实体建立备份
                    // 因此这些数据不受部分 Tick 恢复影响
                    for (int i = 0; i < 2; ++i)
                    {
                        var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                        // 实体 0-4 归客户端 1 所有
                        // 实体 5-9 归客户端 2 所有
                        for (int ent = i*5; ent < (i+1)*5; ++ent)
                        {
                            var serverEnt = serverEntities[ent];
                            var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                            var serverComp1 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGen_IntStruct>(serverEnt);
                            var predictedOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostPredictedOnly>(serverEnt);

                            var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                            spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId,ghost.spawnTick), out var clientEnt);
                            var intStruct_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostGen_IntStruct>(clientEnt);
                            var typeIndex_NonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostTypeIndex>(clientEnt);
                            var clientPredOnly_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostPredictedOnly>(clientEnt);

                            var clientBuffer1 = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEnt);
                            var clientBuffer2_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEnt);

                            Assert.AreEqual(serverComp1.IntValue, intStruct_ToOwner.IntValue,$"Client {i}");
                            Assert.AreEqual(predictedOnly.Value, clientPredOnly_ToOwner.Value,$"Client {i}");
                            Assert.AreEqual(1 + tick*1000, typeIndex_NonOwner.Value,$"Client {i}");
                            Assert.AreEqual(true, 10 ==clientBuffer1.Length);
                            for (int k = 0; k < clientBuffer1.Length; ++k)
                                Assert.AreEqual(serverBuffer1[k].Value, clientBuffer1[k].Value,$"Client {i}");
                            Assert.AreEqual(0, clientBuffer2_ToNonOwner.Length);
                        }
                    }
                }
            }
        }
    }
}
