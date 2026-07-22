#pragma warning disable CS0618 // 禁用 Entities.ForEach 过时警告
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Tests
{
    internal struct GhostGenTest_Buffer : IBufferElementData
    {
        [GhostField] public int IntValue;
        [GhostField] public uint UIntValue;
        [GhostField] public bool BoolValue;
        [GhostField(Quantization = 10)] public float FloatValue;
    }

    [GhostEnabledBit]
    internal struct GhostGenTest_NoReplicatedFieldsBuffer : IBufferElementData, IEnableableComponent
    {
        public int IntValue;
        public uint UIntValue;
        public bool BoolValue;
    }

    internal struct GhostGen_InterpolatedStruct : IComponentData
    {
        [GhostField(Smoothing = SmoothingAction.Interpolate)] public float FloatValue;
    }

    internal struct GhostGen_IntStruct : IComponentData
    {
        [GhostField] public int IntValue;
    }

    internal struct GhostGen_CompositeStruct
    {
        [GhostField] public int IntValue1;
        [GhostField] public int IntValue2;
        [GhostField] public int IntValue3;
    }

    internal struct GhostGen_BufferInterpolated : IBufferElementData
    {
        // Buffer 会忽略自身字段及嵌套结构字段上的 Interpolate 设置
        [GhostField(Smoothing = SmoothingAction.Interpolate)] public float FloatValue;
        [GhostField] public GhostGen_InterpolatedStruct Vec;
    }

    internal struct GhostGenBuffer_BufferComposite : IBufferElementData
    {
        [GhostField(Composite = true)] public GhostGen_CompositeStruct Field1;
    }

    internal struct GhostGenBuffer_ByteBuffer : IBufferElementData
    {
        [GhostField] public byte Value;
    }

    internal class GhostByteBufferAuthoringComponent : MonoBehaviour
    {
        class Baker : Baker<GhostByteBufferAuthoringComponent>
        {
            public override void Bake(GhostByteBufferAuthoringComponent authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
            }
        }
    }

    internal class GhostGenBufferAuthoringComponent : MonoBehaviour
    {
        class Baker : Baker<GhostGenBufferAuthoringComponent>
        {
            public override void Bake(GhostGenBufferAuthoringComponent authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddBuffer<GhostGenTest_Buffer>(entity);
            }
        }
    }

    internal class GhostGenNoReplicatedFieldBuffer : MonoBehaviour
    {
        class Baker : Baker<GhostGenNoReplicatedFieldBuffer>
        {
            public override void Bake(GhostGenNoReplicatedFieldBuffer authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddBuffer<GhostGenTest_NoReplicatedFieldsBuffer>(entity);
            }
        }
    }


    static class BufferTestHelper
    {
        public static void SetBufferValues(World testWorld, Entity entity, int size, int baseBalue)
        {
            var serverBuffer = testWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(entity);
            serverBuffer.ResizeUninitialized(size);
            for (int i = 0; i < size; ++i)
            {
                int value = (i + 1) * 1000 + baseBalue;
                serverBuffer[i] = (new GhostGenTest_Buffer
                {
                    IntValue = value,
                    UIntValue = (uint) ++value,
                    BoolValue = true,
                    FloatValue = ++value
                });
            }
        }

        public static void SetByteBufferValues(World testWorld, Entity entity, int size, int baseBalue)
        {
            var buffer = testWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(entity);
            buffer.ResizeUninitialized(size);
            for (int i = 0; i < buffer.Length; ++i)
                buffer[i] = new GhostGenBuffer_ByteBuffer {Value = (byte) (baseBalue * (i + 1))};
        }

        public static void CheckBuffersValues(NetCodeTestWorld testWorld, Entity serverEntity, Entity clientEntity, bool shouldMatch)
        {
            Assert.AreNotEqual(Entity.Null, serverEntity);
            Assert.AreNotEqual(Entity.Null, clientEntity);
            var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEntity);
            var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEntity);

            if (shouldMatch)
            {
                Assert.AreEqual(serverBuffer.Length, clientBuffer.Length);
                for (int i = 0; i < serverBuffer.Length; ++i)
                {
                    var bs = serverBuffer[i];
                    var cs = clientBuffer[i];
                    Assert.AreEqual(bs.IntValue, cs.IntValue);
                    Assert.AreEqual(bs.UIntValue, cs.UIntValue);
                    Assert.AreEqual(bs.BoolValue, cs.BoolValue);
                    Assert.AreEqual(bs.FloatValue, cs.FloatValue);
                }
            }
            else
            {
                // TODO：Buffer 未发送时两端长度有时仍相同，因此不能用长度不等作为未复制断言
                for (int i = 0; i < serverBuffer.Length && i < clientBuffer.Length; ++i)
                {
                    var bs = serverBuffer[i];
                    var cs = clientBuffer[i];
                    Assert.AreNotEqual(bs.IntValue, cs.IntValue);
                    Assert.AreNotEqual(bs.UIntValue, cs.UIntValue);
                    Assert.AreNotEqual(bs.BoolValue, cs.BoolValue);
                    Assert.AreNotEqual(bs.FloatValue, cs.FloatValue);
                }
            }
        }

        public static void CheckByteBufferValues(NetCodeTestWorld testWorld, Entity serverEntity, Entity clientEntity)
        {
            var serverByteBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEntity);
            var clientByteBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEntity);
            Assert.AreEqual(serverByteBuffer.Length, clientByteBuffer.Length);
            for (int i = 0; i < serverByteBuffer.Length; ++i)
                Assert.AreEqual(serverByteBuffer[i].Value, clientByteBuffer[i].Value);
        }

        public static Entity[] GetClientEntities(NetCodeTestWorld testWorld, Entity[] entities)
        {
            var ghostMapSingleton = testWorld.TryGetSingletonEntity<SpawnedGhostEntityMap>(testWorld.ClientWorlds[0]);
            var entityMap = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SpawnedGhostEntityMap>(ghostMapSingleton).Value;
            var clientEntities = new Entity[entities.Length];
            for (int i = 0; i < entities.Length; ++i)
            {
                var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(entities[i]);
                Assert.IsTrue(entityMap.TryGetValue(
                    new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick}, out clientEntities[i]));
            }
            return clientEntities;
        }

        // 验证客户端动态 Snapshot 数据的内存布局符合预期
        public static void ValidateMultiBufferSnapshotDataContents(in DynamicBuffer<SnapshotDynamicDataBuffer> dynamicBuffer,
            int structBufLen, int b1, int byteBufLen, int b2)
        {
            Assert.IsTrue(structBufLen<32);
            Assert.IsTrue(byteBufLen<32);
            unsafe
            {
                var pointer = (uint*) dynamicBuffer.GetUnsafeReadOnlyPtr();
                var expectedSize = GhostComponentSerializer.SnapshotSizeAligned((structBufLen * 16 + 16) + (16 + 4 * byteBufLen));
                for (int i = 0; i < 32; ++i)
                {
                    var dataSize = pointer[i];
                    Assert.AreEqual(expectedSize, dataSize, $"DynamicBuffer<SnapshotDynamicDataBuffer>[{i}]");
                }

                pointer += 32;
                var stride = (dynamicBuffer.Length - 128) / 32;
                int maskUints1 = (((structBufLen * 4 + 31) & ~31) / 32 + 3) & ~3;
                int maskUints2 = (((byteBufLen + 31) & ~31) / 32 + 3) & ~3;

                void CheckByteBuffer(uint*ptr, int len)
                {
                    for (int k = 0; k < len; ++k)
                    {
                        Assert.AreEqual((byte) ((k + 1) * b2), *ptr);
                        ptr += 1;
                    }
                }
                void CheckStructBuffer(uint*ptr, int len)
                {
                    for (int k = 0; k < structBufLen; ++k)
                    {
                        Assert.AreEqual(1000 * (1 + k) + b1, ptr[0]);
                        Assert.AreEqual(1000 * (1 + k) + 1 + b1, ptr[1]);
                        Assert.AreEqual(1, ptr[2]);
                        Assert.AreEqual(10000 * (1 + k) + (b1 + 2) * 10, ptr[3]);
                        ptr += 4;
                    }
                }

                for (int i = 0; i < 32; ++i)
                {
                    var oldPtr = pointer;
                    pointer += maskUints2;
                    CheckByteBuffer(pointer, byteBufLen);
                    pointer += GhostComponentSerializer.SnapshotSizeAligned(4*byteBufLen)/4;
                    pointer += maskUints1;
                    CheckStructBuffer(pointer, structBufLen);
                    pointer += GhostComponentSerializer.SnapshotSizeAligned(16 * structBufLen)/4;
                    pointer = oldPtr + stride / 4;
                }
            }
        }
    }

    [TestFixture]
    internal partial class DynamicBufferSerializationTests
    {
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void BuffersAreSerialized()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostGenBufferAuthoringComponent>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 3, 6);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 3, 10);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
            }
        }

        [Test]
        public void BuffersWithoutReplicatedFieldsAreSerialized()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostGenNoReplicatedFieldBuffer>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_NoReplicatedFieldsBuffer>(serverEntity);
                serverBuffer.ResizeUninitialized(10);
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<GhostGenTest_NoReplicatedFieldsBuffer>(serverEntity, false);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenTest_NoReplicatedFieldsBuffer>(clientEntities[0]);
                Assert.AreEqual(0, clientBuffer.Length);
                Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<GhostGenTest_NoReplicatedFieldsBuffer>(clientEntities[0]));
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<GhostGenTest_NoReplicatedFieldsBuffer>(serverEntity, true);
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<GhostGenTest_NoReplicatedFieldsBuffer>(clientEntities[0]));
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void BuffersCanChangeSize()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostGenBufferAuthoringComponent>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                // 两端 Buffer 初始均为空
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
                // 写入 Buffer 数据
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 3, 10);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
                // 缩短 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 2, 20);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
                // 扩大 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 5, 30);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void MultipleBuffersCanChangeSize()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();
                ghostGameObject.AddComponent<GhostGenBufferAuthoringComponent>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});

                void Validate(int len1, int b1, int len2, int b2)
                {
                    var dynamicBuffer = testWorld.ClientWorlds[0].EntityManager
                        .GetBuffer<NetCode.SnapshotDynamicDataBuffer>(clientEntities[0]);
                    BufferTestHelper.ValidateMultiBufferSnapshotDataContents(dynamicBuffer, len1, b1, len2, b2);
                    BufferTestHelper.CheckBuffersValues(testWorld, serverEntity, clientEntities[0], true);
                }

                // 写入两个 Buffer 的数据
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntity, 10, 10);
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 3, 0);

                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                Validate(3, 0, 10, 10);
                // 缩短第二个 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 2, 20);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                Validate(2, 20, 10, 10);
                // 扩大第二个 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntity, 5, 30);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                Validate(5, 30, 10, 10);
                // 缩短第一个 Buffer
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntity, 5, 100);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                Validate(5, 30, 5, 100);
                // 扩大第一个 Buffer
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntity, 15, 1000);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                Validate(5, 30, 15, 1000);
            }
        }

        internal class GhostBufferMixedTypesConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent<GhostGen_IntStruct>(entity);
                baker.AddComponent<GhostGen_InterpolatedStruct>(entity);
                baker.AddBuffer<GhostGen_BufferInterpolated>(entity);
                baker.AddBuffer<GhostGenTest_Buffer>(entity);
                baker.AddBuffer<GhostGenBuffer_BufferComposite>(entity);
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void BuffersSupportMultipleBuffers()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostBufferMixedTypesConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);

                var serverEntity = testWorld.TryGetSingletonEntity<GhostGen_IntStruct>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGen_IntStruct
                {
                    IntValue = 10
                });
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGen_InterpolatedStruct
                {
                    FloatValue = 20.0f
                });
                var bufInterpolated =
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGen_BufferInterpolated>(serverEntity);
                bufInterpolated.ResizeUninitialized(2);
                for (int i = 0; i < 2; ++i)
                {
                    int value = (i + 1) * 10000;
                    bufInterpolated[i] = new GhostGen_BufferInterpolated
                    {
                        FloatValue = ++value
                    };
                }

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEntity);
                serverBuffer.ResizeUninitialized(2);
                for (int i = 0; i < 2; ++i)
                {
                    int value = (i + 1) * 1000;
                    serverBuffer[i] = (new GhostGenTest_Buffer
                    {
                        IntValue = ++value,
                        UIntValue = (uint) ++value,
                        BoolValue = true,
                        FloatValue = ++value
                    });
                }

                var bufComposite =
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_BufferComposite>(serverEntity);
                bufComposite.ResizeUninitialized(2);
                for (int i = 0; i < 2; ++i)
                {
                    int value = i;
                    bufComposite[i] = new GhostGenBuffer_BufferComposite
                    {
                        Field1 = new GhostGen_CompositeStruct
                        {
                            IntValue1 = ++value,
                            IntValue2 = ++value,
                            IntValue3 = ++value
                        }
                    };
                }

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                serverEntity = testWorld.TryGetSingletonEntity<GhostGen_IntStruct>(testWorld.ServerWorld);
                var clientEntity = testWorld.TryGetSingletonEntity<GhostGen_IntStruct>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(10,
                    testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGen_IntStruct>(clientEntity)
                        .IntValue);
                Assert.AreEqual(20.0f,
                    testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGen_InterpolatedStruct>(clientEntity)
                        .FloatValue);

                var clientBufInterpolated = testWorld.ClientWorlds[0].EntityManager
                    .GetBuffer<GhostGen_BufferInterpolated>(clientEntity);
                var serverBufInterpolated =
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGen_BufferInterpolated>(serverEntity);
                Assert.AreEqual(serverBufInterpolated.Length, clientBufInterpolated.Length);
                for (int i = 0; i < serverBufInterpolated.Length; ++i)
                {
                    var bs = serverBufInterpolated[i];
                    var cs = clientBufInterpolated[i];
                    Assert.AreEqual(bs.FloatValue, cs.FloatValue);
                }

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEntity);
                serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEntity);
                Assert.AreEqual(serverBuffer.Length, clientBuffer.Length);
                for (int i = 0; i < serverBuffer.Length; ++i)
                {
                    var bs = serverBuffer[i];
                    var cs = clientBuffer[i];
                    Assert.AreEqual(bs.IntValue, cs.IntValue);
                    Assert.AreEqual(bs.UIntValue, cs.UIntValue);
                    Assert.AreEqual(bs.BoolValue, cs.BoolValue);
                    Assert.AreEqual(bs.FloatValue, cs.FloatValue);
                }

                var clientBufComposite = testWorld.ClientWorlds[0].EntityManager
                    .GetBuffer<GhostGenBuffer_BufferComposite>(clientEntity);
                var serverBufComposite =
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_BufferComposite>(serverEntity);
                Assert.AreEqual(serverBufComposite.Length, clientBufComposite.Length);
                for (int i = 0; i < serverBufComposite.Length; ++i)
                {
                    var bs = serverBufComposite[i];
                    var cs = clientBufComposite[i];
                    Assert.AreEqual(bs.Field1.IntValue1, cs.Field1.IntValue1);
                    Assert.AreEqual(bs.Field1.IntValue2, cs.Field1.IntValue2);
                    Assert.AreEqual(bs.Field1.IntValue3, cs.Field1.IntValue3);
                }
            }
        }

        [Test]
        public void BuffersSentWithFragmentedPipelineAreReceivedCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                var serverEntity = testWorld.TryGetSingletonEntity<GhostGenBuffer_ByteBuffer>(testWorld.ServerWorld);
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntity, 800, 0);
                // 实体数据量较大，只能通过分片 Pipeline 发送
                // Buffer 不支持只发送部分内容
                // 建立连接并确认连接成功
                testWorld.Connect();
                // 进入游戏状态
                testWorld.GoInGame();
                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntity, clientEntities[0]);
            }
        }

        [Test]
        public void BuffersSentInPartialChunkAreReceivedCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                // 数据总量明显超过 300 字节的 Snapshot 包上限，并且还包含其他组件开销
                // 因此同一 Chunk 中的实体需要分多次发送
                testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW.DefaultSnapshotPacketSize = 300;
                var serverEntities = new Entity[20];
                for (int i = 0; i < 20; ++i)
                {
                    serverEntities[i] = testWorld.SpawnOnServer(ghostGameObject);
                    BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntities[i], 85, 10);
                }

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, serverEntities);
                for (int i = 0; i < serverEntities.Length; ++i)
                {
                    BufferTestHelper.CheckByteBufferValues(testWorld, serverEntities[i], clientEntities[i]);
                }
            }
        }

        [DisableAutoCreation]
        partial class ForceSerializeBufferSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(GhostGenTest_Buffer), Rule.ForAll(typeof(GhostGenTest_Buffer)));
            }
        }
        [DisableAutoCreation]
        partial class ForceSerializeOnlyChildBufferSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(GhostGenTest_Buffer), Rule.Unique(typeof(DontSerializeVariant), typeof(GhostGenTest_Buffer)));
            }
        }

        [DisableAutoCreation]
        partial class ForceDontSerializeBufferSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(GhostGenTest_Buffer), Rule.ForAll(typeof(DontSerializeVariant)));
            }
        }

        [Test]
        public void ChildEntitiesBuffersAreSerializedCorrectly([Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                switch (sendForChildrenTestCase)
                {
                    case SendForChildrenTestCase.YesViaExplicitVariantRule:
                        testWorld.Bootstrap(true, typeof(ForceSerializeBufferSystem));
                        break;
                    case SendForChildrenTestCase.YesViaExplicitVariantOnlyAllowChildrenToReplicateRule:
                        testWorld.Bootstrap(true, typeof(ForceSerializeOnlyChildBufferSystem));
                        break;
                    case SendForChildrenTestCase.NoViaExplicitDontSerializeVariantRule:
                        testWorld.Bootstrap(true, typeof(ForceDontSerializeBufferSystem));
                        break;
                    default:
                        testWorld.Bootstrap(true);
                        break;
                }

                var ghostGameObject = new GameObject();
                // 根实体与子实体可以使用相同或不同的 Buffer 类型
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();
                int numChild = 1;
                for (int i = 0; i < numChild; ++i)
                {
                    var childGo = new GameObject("child");
                    childGo.AddComponent<GhostGenBufferAuthoringComponent>();
                    childGo.transform.parent = ghostGameObject.transform;

                    // 通过 Inspector Override 确保子实体上的 Buffer 参与序列化
                    if (sendForChildrenTestCase == SendForChildrenTestCase.YesViaInspectionComponentOverride)
                    {
                        var childInspectionComponent = childGo.AddComponent<GhostAuthoringInspectionComponent>();
                        var fullTypeName = typeof(GhostGenTest_Buffer).FullName;
                        childInspectionComponent.ComponentOverrides = new[]
                        {
                            new GhostAuthoringInspectionComponent.ComponentOverride
                            {
                                FullTypeName = fullTypeName,
                                PrefabType = GhostPrefabType.All,
                                SendTypeOptimization = GhostSendType.AllClients,
                                VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(fullTypeName, fullTypeName),
                            },
                        };
                    }
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                Assert.AreEqual(2, serverEntityGroup.Length);
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntityGroup[0].Value, 10, 10);
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntityGroup[1].Value, 3, 0);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                // 需要发送 32 个状态，并额外预留数帧同步 Ghost 类型
                const int sendIterationCount = 32 + 4;
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntities[0]);
                Assert.AreEqual(2, clientEntityGroup.Length);
                Assert.AreEqual(2, serverEntityGroup.Length);

                // 验证客户端 Snapshot 中包含正确的根实体和子实体 Buffer 数据
                var shouldChildReceiveData = GhostSerializationTestsForEnableableBits.IsExpectedToReplicateBuffer<GhostGenTest_Buffer>(sendForChildrenTestCase, false);
                var dynamicBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCode.SnapshotDynamicDataBuffer>(clientEntities[0]);
                if(shouldChildReceiveData)
                    BufferTestHelper.ValidateMultiBufferSnapshotDataContents(dynamicBuffer, 3, 0, 10, 10);
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value,
                    clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
                // 修改根实体和子实体 Buffer 的数据
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntityGroup[0].Value, 10, 30);
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntityGroup[1].Value, 3, 5);
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value,
                    clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
                // 缩短子实体 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntityGroup[1].Value, 2, 20);
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value,
                    clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
                // 扩大子实体 Buffer
                BufferTestHelper.SetBufferValues(testWorld.ServerWorld, serverEntityGroup[1].Value, 5, 30);
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value,
                    clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
                // 缩短根实体 Buffer
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntityGroup[0].Value, 5, 50);
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value,
                    clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
                // 扩大根实体 Buffer
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverEntityGroup[0].Value, 15, 100);
                for (int i = 0; i < sendIterationCount; ++i)
                    testWorld.Tick();
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntityGroup[0].Value, clientEntityGroup[0].Value);
                BufferTestHelper.CheckBuffersValues(testWorld, serverEntityGroup[1].Value, clientEntityGroup[1].Value, shouldChildReceiveData);
            }
        }


        internal class GhostGroupGhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner());
                baker.DependsOn(gameObject);
                if (gameObject.name == "ParentGhost")
                {
                    baker.AddBuffer<GhostGroup>(entity);
                }
                else
                {
                    baker.AddComponent(entity, default(GhostChildEntity));
                    baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                }
            }
        }

        [Test]
        public void GhostGroupBuffersAreSerialized([Values]bool registerChildFirst)
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

                if(registerChildFirst)
                    Assert.IsTrue(testWorld.CreateGhostCollection(childGhostGameObject, ghostGameObject));
                else
                    Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);
                var serverRoot = testWorld.SpawnOnServer(ghostGameObject);
                var serverChild = testWorld.SpawnOnServer(childGhostGameObject);
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverRoot).Add(new GhostGroup {Value = serverChild});
                var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverChild);
                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverChild, 5, 10);

                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverRoot, serverChild});
                BufferTestHelper.CheckByteBufferValues(testWorld, serverChild, clientEntities[1]);

                BufferTestHelper.SetByteBufferValues(testWorld.ServerWorld, serverChild, 30, 10);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                BufferTestHelper.CheckByteBufferValues(testWorld, serverChild, clientEntities[1]);
            }
        }

        [GhostComponent(PrefabType = GhostPrefabType.Server)]
        internal struct GhostServerOnlyBuffer : IBufferElementData
        {
            [GhostField] public byte Value;
        }

        [GhostComponent(PrefabType = GhostPrefabType.Client)]
        internal struct GhostClientOnlyBuffer : IBufferElementData
        {
            [GhostField] public byte Value;
        }

        [GhostComponent(PrefabType = GhostPrefabType.AllPredicted, SendDataForChildEntity = true)]
        internal struct GhostPredictedOnlyBuffer : IBufferElementData
        {
            [GhostField] public float Value;
        }

        [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient, SendDataForChildEntity = true)]
        internal struct GhostInterpolatedOnlyBuffer : IBufferElementData
        {
            [GhostField] public byte Value;
        }

        unsafe struct GenericConverter<T>: TestNetCodeAuthoring.IConverter where T: unmanaged, IBufferElementData
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddBuffer<T>(entity);
            }
        }

        [Test]
        [TestCase(typeof(GhostServerOnlyBuffer), true, false, TestName = "ServerOnly")]
        [TestCase(typeof(GhostClientOnlyBuffer), false, true, TestName = "ClientOnly")]
        public void BuffersAreNotSerialized(Type bufferType, bool presentOnServer, bool presentOnClient)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var conv = typeof(GenericConverter<>);
                var args = new []{bufferType};
                var converterType = conv.MakeGenericType(args);
                var converter = Activator.CreateInstance(converterType) as TestNetCodeAuthoring.IConverter;

                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = converter;
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                ghostConfig.SupportedGhostModes = GhostModeMask.Interpolated;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreEqual(presentOnServer, testWorld.ServerWorld.EntityManager.HasComponent(serverEntity, bufferType));

                var serverCollectionEntity = testWorld.TryGetSingletonEntity<GhostCollectionPrefabSerializer>(testWorld.ServerWorld);
                var clientCollectionEntity = testWorld.TryGetSingletonEntity<GhostCollectionPrefabSerializer>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, serverCollectionEntity);
                Assert.AreNotEqual(Entity.Null, clientCollectionEntity);

                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                var ghostType = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity).ghostType;
                var serverCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(serverCollectionEntity);
                Assert.AreEqual(0, serverCollection[ghostType].NumBuffers);

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new []{serverEntity});
                Assert.AreEqual(presentOnClient, testWorld.ClientWorlds[0].EntityManager.HasComponent(clientEntities[0], bufferType));
                Assert.AreEqual(presentOnClient, testWorld.ClientWorlds[0].EntityManager.HasComponent<SnapshotDynamicDataBuffer>(clientEntities[0]));
                var clientCollection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(clientCollectionEntity);
                Assert.AreEqual(0, clientCollection[ghostType].NumBuffers);
            }
        }


        [DisableAutoCreation]
        [RequireMatchingQueriesForUpdate]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
        internal partial class BufferTestPredictionSystem : SystemBase
        {
            protected override void OnUpdate()
            {
                var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
                var deltaTime = SystemAPI.Time.DeltaTime;
                var bufferFromEntity = GetBufferLookup<GhostPredictedOnlyBuffer>();
                // FIXME：以这种方式更新子实体效率较低
                Entities.WithAll<Simulate, GhostInstance>().ForEach((in DynamicBuffer<LinkedEntityGroup> group) =>
                {
                    for (int i = 0; i < group.Length; ++i)
                    {
                        var e = group[i];
                        var buf = bufferFromEntity[e.Value];
                        var t = (int) (tick.TickIndexForValidTick % buf.Length);
                        var v = buf[t];
                        v.Value += deltaTime * 60.0f;
                        buf[t] = v;
                    }
                }).Run();
            }
        }

        [Test]
        public void PredictedGhostsBackupAndRestoreBufferCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,  typeof(BufferTestPredictionSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GenericConverter<GhostPredictedOnlyBuffer>();
                var child = new GameObject();
                child.AddComponent<TestNetCodeAuthoring>().Converter = new GenericConverter<GhostPredictedOnlyBuffer>();
                child.transform.parent = ghostGameObject.transform;
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                // 暂时禁用预测逻辑
                testWorld.ServerWorld.GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = false;
                testWorld.ClientWorlds[0].GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = false;

                // 生成实体并初始化根实体和子实体的 Buffer
                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                {
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostOwner {NetworkId = 0});
                    var group = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                    for(int e=0;e<2;++e)
                    {
                        var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(group[e].Value);
                        buffer.ResizeUninitialized(16);
                        for (int i = 0; i < 16; ++i)
                            buffer[i] = new GhostPredictedOnlyBuffer {Value = 10.0f * i};
                    }
                }

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntities[0]);
                Assert.AreEqual(2, clientEntityGroup.Length);
                Assert.AreEqual(2, serverEntityGroup.Length);

                var serverBuffers = new[]
                {
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(serverEntityGroup[0].Value),
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(serverEntityGroup[1].Value)
                };
                var clientBuffers = new[]
                {
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[0].Value),
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[1].Value)
                };

                Assert.AreEqual(serverBuffers[0].Length, clientBuffers[0].Length);
                Assert.AreEqual(serverBuffers[1].Length, clientBuffers[1].Length);

                testWorld.ServerWorld.GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = true;
                testWorld.ClientWorlds[0].GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = true;
                var firstPredTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                firstPredTick.Increment();
                for (int i = 0; i < 32; ++i)
                {
                    testWorld.Tick(1.0f / 60f / 4.0f);
                }
                testWorld.ServerWorld.GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = false;
                testWorld.ClientWorlds[0].GetExistingSystemManaged<BufferTestPredictionSystem>().Enabled = false;

                var networkTimeClient = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                var curTick = networkTimeClient.ServerTick;

                serverBuffers = new[]
                {
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(serverEntityGroup[0].Value),
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(serverEntityGroup[1].Value)
                };
                clientBuffers = new[]
                {
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[0].Value),
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[1].Value)
                };

                var networkTimeServer = testWorld.GetNetworkTime(testWorld.ServerWorld);
                for (var i = firstPredTick; i != curTick; i.Increment())
                {
                    var expected = ((int)i.TickIndexForValidTick % clientBuffers[0].Length)*10.0f + 1.0f;
                    Assert.AreEqual(expected, clientBuffers[0][(int)i.TickIndexForValidTick%clientBuffers[0].Length].Value);
                    Assert.AreEqual(expected, clientBuffers[1][(int)i.TickIndexForValidTick%clientBuffers[1].Length].Value);
                    if (networkTimeServer.ServerTick.IsNewerThan(curTick))
                    {
                        Assert.AreEqual(expected, serverBuffers[0][(int)i.TickIndexForValidTick%serverBuffers[0].Length].Value);
                        Assert.AreEqual(expected, serverBuffers[1][(int)i.TickIndexForValidTick%serverBuffers[1].Length].Value);
                    }
                }
                // 继续运行并检查两端数据同步
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                for (int i = 0; i < 16; ++i)
                {
                    Assert.AreEqual(serverBuffers[0][i].Value, clientBuffers[0][i].Value);
                    Assert.AreEqual(serverBuffers[1][i].Value, clientBuffers[1][i].Value);
                }

                // 修改 Buffer 长度，验证预测备份 Buffer 会同步调整且流程仍正常
                serverBuffers[0].ResizeUninitialized(22);
                serverBuffers[1].ResizeUninitialized(20);
                for (int i = 0; i < 22; ++i)
                    serverBuffers[0][i] = new GhostPredictedOnlyBuffer {Value = 10.0f * i};
                for (int i = 0; i < 20; ++i)
                    serverBuffers[1][i] = new GhostPredictedOnlyBuffer {Value = 20.0f * i};

                // 继续运行并检查变长后的 Buffer 同步
                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                clientBuffers = new[]
                {
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[0].Value),
                    testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostPredictedOnlyBuffer>(clientEntityGroup[1].Value)
                };
                for (int i = 0; i < 22; ++i)
                {
                    Assert.AreEqual(serverBuffers[0][i].Value, clientBuffers[0][i].Value);
                }
                for (int i = 0; i < 20; ++i)
                {
                    Assert.AreEqual(serverBuffers[1][i].Value, clientBuffers[1][i].Value);
                }


            }
        }

        [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
        [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
        [DisableAutoCreation]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        internal partial class TestSpawnClassificationSystem : SystemBase
        {
            public NativeList<Entity> m_PredictedEntities;
            protected override void OnCreate()
            {
                RequireForUpdate<GhostSpawnQueue>();
                RequireForUpdate<PredictedGhostSpawnList>();
                m_PredictedEntities = new NativeList<Entity>(5,Allocator.Persistent);
            }

            protected override void OnDestroy()
            {
                m_PredictedEntities.Dispose();
            }

            protected override void OnUpdate()
            {
                var spawnListEntity = SystemAPI.GetSingletonEntity<PredictedGhostSpawnList>();
                var spawnListFromEntity = GetBufferLookup<PredictedGhostSpawn>();
                var predictedEntities = m_PredictedEntities;
                Entities
                    .WithAll<GhostSpawnQueue>()
                    .ForEach((DynamicBuffer<GhostSpawnBuffer> ghosts) =>
                    {
                        var spawnList = spawnListFromEntity[spawnListEntity];
                        for (int i = 0; i < ghosts.Length; ++i)
                        {
                            var ghost = ghosts[i];
                            if (ghost.SpawnType != GhostSpawnBuffer.Type.Predicted)
                                continue;
                            for (int j = 0; j < spawnList.Length; ++j)
                            {
                                if (ghost.GhostType == spawnList[j].ghostType &&
                                    math.abs(ghost.ServerSpawnTick.TicksSince(spawnList[j].spawnTick)) < 5)
                                {
                                    ghost.PredictedSpawnEntity = spawnList[j].entity;
                                    spawnList.RemoveAtSwapBack(j);
                                    predictedEntities.Add(ghost.PredictedSpawnEntity);
                                    break;
                                }
                            }
                            ghosts[i] = ghost;
                        }
                    }).Run();
            }
        }

        [DisableAutoCreation]
        [RequireMatchingQueriesForUpdate]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
        internal partial class SpawnPredictedGhost : SystemBase
        {
            public NetworkTick spawnAtTick = NetworkTick.Invalid;
            public Entity spawnedEntity;
            Entity SpawnPredictedEntity(int baseValue)
            {
                var prefabsList = SystemAPI.GetSingletonEntity<NetCodeTestPrefabCollection>();
                var prefabs = EntityManager.GetBuffer<NetCodeTestPrefab>(prefabsList);
                var entity = EntityManager.Instantiate(prefabs[0].Value);
                BufferTestHelper.SetByteBufferValues(World, entity, 5, baseValue);
                return entity;
            }
            protected override void OnUpdate()
            {
                var netTime = SystemAPI.GetSingleton<NetworkTime>();
                if (spawnAtTick.IsValid && !spawnAtTick.IsNewerThan(NetworkTimeHelper.LastFullServerTick(netTime)))
                {
                    if(SystemAPI.HasSingleton<UnscaledClientTime>())
                        spawnedEntity = SpawnPredictedEntity(10);
                    else
                        spawnedEntity = SpawnPredictedEntity(100);
                    spawnAtTick = NetworkTick.Invalid;
                }
            }
        }

        [Test]
        public void PredictedSpawnedGhostSerializeBufferCorrectly()
        {

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(TestSpawnClassificationSystem), typeof(SpawnPredictedGhost));

                var ghostGameObject = new GameObject("PredictedSpawnedTestGhost");
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GenericConverter<GhostGenBuffer_ByteBuffer>();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();

                // 运行若干 Tick，同步服务端时间与 Ghost Collection
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                // 在客户端与服务端的同一目标 Tick 预测生成实体
                Entity clientEntity = Entity.Null;
                Entity serverEntity = Entity.Null;
                var networkTimeServer = testWorld.GetNetworkTime(testWorld.ServerWorld);
                var clientWorld = testWorld.ClientWorlds[0];
                var networkTimeClient = testWorld.GetNetworkTime(clientWorld);
                var spawnTick = networkTimeServer.ServerTick;
                spawnTick.Add(5);
                var clientSpawnTick = networkTimeClient.ServerTick;
                clientSpawnTick.Increment();
                if (clientSpawnTick.IsNewerThan(spawnTick))
                    spawnTick = clientSpawnTick;
                testWorld.ServerWorld.GetExistingSystemManaged<SpawnPredictedGhost>().spawnAtTick = spawnTick;
                clientWorld.GetExistingSystemManaged<SpawnPredictedGhost>().spawnAtTick = spawnTick;
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                serverEntity = testWorld.ServerWorld.GetExistingSystemManaged<SpawnPredictedGhost>().spawnedEntity;
                clientEntity = clientWorld.GetExistingSystemManaged<SpawnPredictedGhost>().spawnedEntity;
                // 检查预测生成实体与权威 Ghost 是否匹配
                Assert.AreNotEqual(serverEntity, Entity.Null);
                Assert.AreNotEqual(clientEntity, Entity.Null);
                var clientEntities = BufferTestHelper.GetClientEntities(testWorld, new [] {serverEntity});
                Assert.AreEqual(clientEntity, clientEntities[0]);
                // 检查预测生成分类结果
                var classificationSystem = clientWorld.GetExistingSystemManaged<TestSpawnClassificationSystem>();
                Assert.AreEqual(1, classificationSystem.m_PredictedEntities.Length);
                Assert.AreEqual(clientEntity, classificationSystem.m_PredictedEntities[0]);
                // 检查两端 Buffer 数据一致
                BufferTestHelper.CheckByteBufferValues(testWorld, serverEntity, clientEntity);
            }
        }
    }
}
