using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class GhostGroupGhostConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            // 烘焙结果依赖对象名称
            baker.DependsOn(gameObject);
            if (gameObject.name == "ParentGhost")
            {
                baker.AddBuffer<GhostGroup>(entity);
                baker.AddComponent(entity, default(GhostGroupRoot));
            }
            else
                baker.AddComponent(entity, default(GhostChildEntity));
        }
    }
    internal class LargeDataSizeGroupGhostConverter : TestNetCodeAuthoring.IConverter
    {
        // 子节点需要使用不同的 Archetype
        public void Bake(GameObject gameObject, IBaker baker)
        {

            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            // 烘焙结果依赖对象名称
            baker.DependsOn(gameObject);
            if (gameObject.name == "ParentGhost")
            {
                baker.AddBuffer<GhostGroup>(entity);
                baker.AddComponent(entity, default(GhostGroupRoot));
                var buffer = baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                buffer.Length = 300;
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new GhostGenBuffer_ByteBuffer { Value = (byte)i };
            }
            else
            {
                var sub = gameObject.name.Substring(5, gameObject.name.Length - 5);
                int index = int.Parse(sub);
                baker.AddComponent(entity, default(GhostChildEntity));
                if ((index == 0))
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_3));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_4));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_5));
                }
                else if ((index == 1))
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                }
                else
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_3));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_4));
                }
                var buffer = baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                buffer.Length = 200;
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new GhostGenBuffer_ByteBuffer { Value = (byte)i };
            }
        }
    }
    internal struct GhostGroupRoot : IComponentData
    {}
    internal class GhostGroupTests
    {
        [Test]
        public void EntityMarkedAsChildIsNotSent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体及其数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
                Assert.AreEqual(Entity.Null, clientChildEnt);
            }
        }
        [Test]
        public void EntityMarkedAsChildIsSentAsPartOfGroup()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体及其数据是否正确
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
                Assert.AreNotEqual(Entity.Null, clientChildEnt);
                Assert.AreEqual(43, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientChildEnt).NetworkId);
            }
        }
        [Test]
        public void CanHaveManyGhostGroupGhostTypes()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[64];

                for (int i = 0; i < 32; ++i)
                {
                    var ghostGameObject = new GameObject();
                    ghostGameObject.name = "ParentGhost";
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = "ChildGhost";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                    ghostGameObjects[i] = ghostGameObject;
                    ghostGameObjects[i+32] = childGhostGameObject;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 32; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[i]);
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[i+32]);

                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和分组数量是否正确
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(64, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(32, groupQuery.CalculateEntityCount());
            }
        }
        [Test]
        public void CanHaveManyGhostGroupsOfSameType()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[2];

                for (int i = 0; i < 1; ++i)
                {
                    var ghostGameObject = new GameObject();
                    ghostGameObject.name = "ParentGhost";
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = "ChildGhost";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                    ghostGameObjects[i] = ghostGameObject;
                    ghostGameObjects[i+1] = childGhostGameObject;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 32; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[0]);
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[1]);

                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 检查客户端 World 中的实体数量和分组数量是否正确
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(64, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(32, groupQuery.CalculateEntityCount());
            }
        }

        [Test]
        [NUnit.Framework.Description("Test an edge case of ghost serialization, where we are unable to serializea group," +
                                     " therefore we reset the state and try again. The test is only meant to verify that exceptions aren't throwns and that data are serialized." +
                                     " We are not currently testing another issue that arise with large ghost, that is handled somewhat correctly, but that has not nice user error reported.")]
        public void GroupLargerThan1MTU_WorkCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.LogLevel = NetDebug.LogLevelType.Debug; // 需要此日志等级才能输出 PERFORMANCE 警告
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[4];
                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                // LargeDataSizeGroupGhostConverter 会为每个子节点创建不同的 Archetype
                // 这样才能正确覆盖分组序列化回滚，使用相同 Archetype 无法触发目标路径
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LargeDataSizeGroupGhostConverter();
                ghostGameObjects[0] = ghostGameObject;
                for (int i = 0; i < 3; ++i)
                {
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = $"Child{i}";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LargeDataSizeGroupGhostConverter();
                    ghostGameObjects[i+1] = childGhostGameObject;
                }

                // 此测试需要足够大的数据才能触发失败
                // 为方便构造场景，可以通过强制限制最大 Snapshot 大小来实现
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[0]);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                for (int i = 0; i < 3; ++i)
                {
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[i+1]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = i});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                {
                    ValidateComponentStatsLessThanTypeStats(testWorld);
                    testWorld.Tick();
                }

                // 检查客户端 World 中的分组、子节点及 Buffer 数据是否正确
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostChildEntity));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(3, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(1, groupQuery.CalculateEntityCount());
                var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(groupQuery.GetSingletonEntity());
                for (int i = 0; i < rootBuffer.Length; ++i)
                    Assert.AreEqual((byte)i, rootBuffer[i].Value);
                var entities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; ++i)
                {
                    var owner = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(entities[i]);
                    Assert.AreEqual(i, owner.NetworkId);
                    var childBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(entities[i]);
                    for (int j = 0; j < rootBuffer.Length; ++j)
                        Assert.AreEqual((byte)j, rootBuffer[j].Value);
                }

                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
            }
        }

        [Test]
        [NUnit.Framework.Description("Test an edge case of ghost serialization, where we are unable to serializea group that does not have" +
                                     " children because the bitstream is already full. Therefore we reset the state and try again and again.")]
        [TestCase(0)]
        [TestCase(1)]
        public void GroupWith0Children_WorkCorrectly(int failingEntity)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                var authoringComponent = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoringComponent.GhostGroup = true;
                authoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                // 建立连接并确认连接成功
                testWorld.Connect();
                // 进入游戏状态
                testWorld.GoInGame();

                for(int i=0;i<32;++i)
                    testWorld.Tick();

                var systemData = testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld);
                // 强制使用很小的包容量，使 Ghost Group 序列化触发 HasFailedWrites
                // 通过调整单个实体的编码大小分别构造两种失败位置
                // 预期测试环境如下
                // 首个实体失败时，应申请更大的容量并以两倍大小重试
                // 第二个实体失败时，首次只发送第一个实体，下次再发送另一个实体
                int baseSize;
                int inc;
                if (failingEntity == 0)
                {
                    baseSize = 101;
                    inc = 0;
                    systemData.ValueRW.DefaultSnapshotPacketSize = 122;
                }
                else
                {
                    baseSize = 55;
                    inc = 3;
                    systemData.ValueRW.DefaultSnapshotPacketSize = 146;
                }

                for (int ent = 0; ent < 2; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    // 该大小会使首个实体序列化失败，随后通过分片 Pipeline 重试
                    var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    // TODO：这里通过数据大小间接构造失败，更准确的做法是直接对 ChunkSerializer 编写单元测试
                    // 当前可以做到，但测试环境搭建会更繁琐
                    buffer.Resize(baseSize + inc*ent, NativeArrayOptions.UninitializedMemory);
                    for (int i = 0; i < buffer.Length; ++i)
                        buffer.ElementAt(i).Value = 7;
                }

                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                for(int i=0;i<3;++i)
                    testWorld.Tick();
                // 第三个 Tick 时，用例 0 应收到两个实体
                // 第三个 Tick 时，用例 1 应只收到第一个实体
                if(failingEntity == 0)
                    Assert.AreEqual(2, groupQuery.CalculateEntityCount());
                else
                    Assert.AreEqual(1, groupQuery.CalculateEntityCount());
                var clientEntities = groupQuery.ToEntityArray(Allocator.Temp);
                for (int ent = 0; ent < clientEntities.Length; ++ent)
                {
                    var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEntities[ent]);
                    for (int i = 0; i < rootBuffer.Length; ++i)
                        Assert.AreEqual(7, rootBuffer[i].Value);
                }
                // 再推进一个 Tick 后应收到两个实体
                testWorld.Tick();
                Assert.AreEqual(2, groupQuery.CalculateEntityCount());
                for (int ent = 0; ent < clientEntities.Length; ++ent)
                {
                    var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEntities[ent]);
                    for (int i = 0; i < rootBuffer.Length; ++i)
                        Assert.AreEqual(7, rootBuffer[i].Value);
                }
            }
        }

        static unsafe void ValidateComponentStatsLessThanTypeStats(NetCodeTestWorld testWorld)
        {
            var stats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);

            // TODO：验证已发送包的总大小大于统计信息记录的总大小

            var perTypeStatsList = stats.UnsafeMainStatsRead.PerGhostTypeStatsListRO;
            for (int i = 0; i < perTypeStatsList.Length; i++)
            {
                uint totalSize = perTypeStatsList[i].SizeInBits;
                uint componentSizesSum = 0;
                var perComponentStatsList = perTypeStatsList[i].PerComponentStatsList;
                for (int j = 0; j < perComponentStatsList.Length; j++)
                {
                    componentSizesSum  += perComponentStatsList[j].SizeInSnapshotInBits;
                }
                Assert.IsTrue(totalSize >= componentSizesSum, $"Sum of all component stats {componentSizesSum} is larger than actual total size {totalSize}, something went wrong in stats calculations. Normally, we can expect some metadata in total size that's not accounted for by component size. But component size should always remain smaller than total size.");
            }
        }

        [Test]
        public void GhostGroup_WorksWithRelevancy_AndStaticOptimization([Values]NetCodeTestLatencyProfile latencyProfile, [Values]GhostOptimizationMode rootMode, [Values]GhostOptimizationMode childMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.SetTestLatencyProfile(latencyProfile);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                var ghostAuthoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostAuthoring.DefaultGhostMode = GhostMode.Interpolated;
                ghostAuthoring.OptimizationMode = rootMode;
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                var childGhostAuthoring = childGhostGameObject.AddComponent<GhostAuthoringComponent>();
                childGhostAuthoring.DefaultGhostMode = GhostMode.Predicted;
                childGhostAuthoring.OptimizationMode = childMode;
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);
                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 1});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 1});
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                testWorld.Connect(maxSteps:16);
                testWorld.GoInGame();

                // GhostGroup 子节点的相关性存在以下特殊规则
                // 子节点忽略自身的相关性值
                // 子节点跟随根节点的相关性值，即根节点相关时子节点也相关
                // 但根节点变为不相关时，子节点不会随根节点一起销毁，只会停止接收更新并进入滞留状态
                // 子节点离开 GhostGroup 后，会重新遵循自身的相关性规则
                // TODO：补充 Ghost 进入和离开 GhostGroup 时的相关性测试
                const bool ghostGroupChildNuance = true;

                Assert.AreEqual(GhostRelevancyMode.Disabled, testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode);
                ExpectExist(testWorld, true, true, "relevancy is disabled by default, expect relevant");

                // 将根节点和子节点设为不相关
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                ExpectExist(testWorld, false, ghostGroupChildNuance, "forced irrelevant 1st");

                // 将根节点和子节点重新设为相关
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                ExpectExist(testWorld, true, ghostGroupChildNuance, "forced relevant 1st");

                // 再次设为不相关
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                ExpectExist(testWorld, false, ghostGroupChildNuance, "forced irrelevant 2nd");

                // 只将根节点设为相关
                var serverEntGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Clear();
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(1, serverEntGhostId), 1);
                ExpectExist(testWorld, true, ghostGroupChildNuance, "only root relevant (child not)");

                // 只将子节点设为相关
                var serverChildEntGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverChildEnt).ghostId;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Clear();
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(1, serverChildEntGhostId), 1);
                ExpectExist(testWorld, false, ghostGroupChildNuance, "only child relevant (root not)");
            }
        }

        private static void ExpectExist(NetCodeTestWorld testWorld, bool expectRoot, bool expectChild, string context)
        {
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
            var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]) != Entity.Null;
            var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]) != Entity.Null;
            Assert.AreEqual(expectRoot, clientEnt, "root failed:" + context);
            Assert.AreEqual(expectChild, clientChildEnt, "child failed:" + context);

            var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
            int expectedCount = (expectRoot ? 1 : 0) + (expectChild ? 1 : 0);
            var msg = ghostCount.ToString();
            Assert.AreEqual(expectedCount, ghostCount.GhostCountInstantiatedOnClient, msg);
            Assert.AreEqual(expectedCount, ghostCount.GhostCountReceivedOnClient, msg);
            Assert.AreEqual(expectedCount, ghostCount.GhostCountOnServer, msg);
        }
    }
}
