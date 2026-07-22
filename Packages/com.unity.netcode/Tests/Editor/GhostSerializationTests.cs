using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode.Tests
{
    internal class GhostValueSerializerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostValueSerializer {});
            baker.AddBuffer<GhostValueBufferSerializer>(entity);
        }
    }

    internal enum EnumUntyped
    {
        Value0 = 255,
    }
    internal enum EnumS8 : sbyte
    {
        Value0 = 126,
    }
    internal enum EnumU8 : byte
    {
        Value0 = 253,
    }
    internal enum EnumS16 : short
    {
        Value0 = 0x7AAB
    }
    internal enum EnumU16 : ushort
    {
        Value0 = 0xF00D,
    }
    internal enum EnumS32
    {
        Value0 = 0x007AD0BE,
    }
    internal enum EnumU32 : uint
    {
        Value0 = 0xBAADF00D
    }
    internal enum EnumS64 : long
    {
        Value0 = 0x791BBCDC0CCAEDD1,
    }
    internal enum EnumU64 : ulong
    {
        Value0 = 0xABBA1970F1809FE2,
    }

    internal struct GhostValueBufferSerializer : IBufferElementData
    {
        [GhostField] public GhostValueSerializer Values;
        public override string ToString() => $"BUF[{Values}]";
    }

    internal struct GhostValueSerializer : IComponentData
    {
        [GhostField] public bool BoolValue;
        [GhostField] public int IntValue;
        [GhostField] public uint UIntValue;
        [GhostField] public long LongValue;
        [GhostField] public ulong ULongValue;

        [GhostField] public EnumUntyped EnumUntyped;
        [GhostField] public EnumS8   EnumS08;
        [GhostField] public EnumU8   EnumU08;
        [GhostField] public EnumS16  EnumS16;
        [GhostField] public EnumU16  EnumU16;
        [GhostField] public EnumS32  EnumS32;
        [GhostField] public EnumU32  EnumU32;
        [GhostField] public EnumS64  EnumS64;
        [GhostField] public EnumU64  EnumU64;

        [GhostField(Quantization=10)] public float FloatValue;
        [GhostField(Quantization=0)] public float UnquantizedFloatValue;
        [GhostField(Quantization=1000)] public double DoubleValue;
        [GhostField(Quantization=0)] public double UnquantizedDoubleValue;
        [GhostField(Quantization=10)] public float2 Float2Value;
        [GhostField(Quantization=0)] public float2 UnquantizedFloat2Value;
        [GhostField(Quantization=10)] public float3 Float3Value;
        [GhostField(Quantization=0)] public float3 UnquantizedFloat3Value;
        [GhostField(Quantization=10)] public float4 Float4Value;
        [GhostField(Quantization=0)] public float4 UnquantizedFloat4Value;
        [GhostField(Quantization=1000)] public quaternion QuaternionValue;
        [GhostField(Quantization=0)] public quaternion UnquantizedQuaternionValue;
        [GhostField] public FixedString32Bytes StringValue32;
        [GhostField] public FixedString64Bytes StringValue64;
        [GhostField] public FixedString128Bytes StringValue128;
        [GhostField] public FixedString512Bytes StringValue512;
        [GhostField] public FixedString4096Bytes StringValue4096;
        [GhostField] public NetworkTick InvalidTickValue;
        [GhostField] public NetworkTick TickValue;
        [GhostField] public Entity EntityValue;

        public override string ToString()
        {
            return $"{nameof(BoolValue)}: {BoolValue}, {nameof(IntValue)}: {IntValue}, {nameof(UIntValue)}: {UIntValue}, {nameof(LongValue)}: {LongValue}, {nameof(ULongValue)}: {ULongValue}, {nameof(EnumUntyped)}: {EnumUntyped}, {nameof(EnumS08)}: {EnumS08}, {nameof(EnumU08)}: {EnumU08}, {nameof(EnumS16)}: {EnumS16}, {nameof(EnumU16)}: {EnumU16}, {nameof(EnumS32)}: {EnumS32}, {nameof(EnumU32)}: {EnumU32}, {nameof(EnumS64)}: {EnumS64},\n{nameof(EnumU64)}: {EnumU64}, {nameof(FloatValue)}: {FloatValue}, {nameof(UnquantizedFloatValue)}: {UnquantizedFloatValue}, {nameof(DoubleValue)}: {DoubleValue}, {nameof(UnquantizedDoubleValue)}: {UnquantizedDoubleValue}, {nameof(Float2Value)}: {Float2Value}, {nameof(UnquantizedFloat2Value)}: {UnquantizedFloat2Value}, {nameof(Float3Value)}: {Float3Value}, {nameof(UnquantizedFloat3Value)}: {UnquantizedFloat3Value}, {nameof(Float4Value)}: {Float4Value},\n{nameof(UnquantizedFloat4Value)}: {UnquantizedFloat4Value}, {nameof(QuaternionValue)}: {QuaternionValue}, {nameof(UnquantizedQuaternionValue)}: {UnquantizedQuaternionValue}, {nameof(StringValue32)}: L{StringValue32.Length}, {nameof(StringValue64)}: L{StringValue64.Length}, {nameof(StringValue128)}: L{StringValue128.Length}, {nameof(StringValue512)}: L{StringValue512.Length}, {nameof(StringValue4096)}: L{StringValue4096.Length},\n {nameof(InvalidTickValue)}: {InvalidTickValue.SerializedData}, {nameof(TickValue)}: {TickValue.SerializedData}, {nameof(EntityValue)}: {EntityValue}";
        }

        [GhostField(Composite = true)] public Union UnionValue;
        [StructLayout(LayoutKind.Explicit)]
        internal struct Union
        {
            [FieldOffset(0)] [GhostField(SendData = false)] public StructA State1;
            [FieldOffset(0)] [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, Composite = true)] public StructB State2;
            [FieldOffset(0)] [GhostField(SendData = false)] public StructC State3;
            internal struct StructA
            {
                public int A, B;
                public float C;
            }
            internal struct StructB
            {
                public ulong A, B, C, D;
            }
            internal struct StructC
            {
                public double A, B;
            }
            public static void Assertions()
            {
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructA>());
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructC>());
            }
        }
    }

    internal class GhostSerializationTests
    {
        static void VerifyGhostValues(NetCodeTestWorld testWorld)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);

            Assert.AreNotEqual(Entity.Null, serverEntity);
            Assert.AreNotEqual(Entity.Null, clientEntity);

            var serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostValueSerializer>(serverEntity);
            var clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity);
            Assert.AreEqual(serverEntity, serverValues.EntityValue);
            Assert.AreEqual(clientEntity, clientValues.EntityValue);
            VerifyGhostValues(serverValues, clientValues);

            var serverBufferValues = testWorld.ServerWorld.EntityManager.GetBuffer<GhostValueBufferSerializer>(serverEntity);
            var clientBufferValues = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostValueBufferSerializer>(clientEntity);
            Assert.AreEqual(serverBufferValues.Length, clientBufferValues.Length);

            for (int i = 0; i < serverBufferValues.Length; i++)
            {
                VerifyGhostValues(serverBufferValues[i].Values, clientBufferValues[i].Values);
            }
        }

        static void VerifyGhostValues(GhostValueSerializer serverValues, GhostValueSerializer clientValues)
        {
            //Debug.Log($"VerifyGhostValues | ServerValues:{serverValues.ToString()}\nClientValues:{clientValues.ToString()}");
            Assert.AreEqual(serverValues.BoolValue, clientValues.BoolValue);
            Assert.AreEqual(serverValues.IntValue, clientValues.IntValue);
            Assert.AreEqual(serverValues.UIntValue, clientValues.UIntValue);
            Assert.AreEqual(serverValues.LongValue, clientValues.LongValue);
            Assert.AreEqual(serverValues.ULongValue, clientValues.ULongValue);
            Assert.AreEqual(serverValues.FloatValue, clientValues.FloatValue);
            Assert.AreEqual(serverValues.UnquantizedFloatValue, clientValues.UnquantizedFloatValue);
            Assert.AreEqual(serverValues.UnquantizedDoubleValue, clientValues.UnquantizedDoubleValue);
            Assert.LessOrEqual(math.distance(serverValues.DoubleValue, clientValues.DoubleValue), 1e-3);

            Assert.AreEqual(serverValues.EnumUntyped,clientValues.EnumUntyped);
            Assert.AreEqual(serverValues.EnumS08,clientValues.EnumS08);
            Assert.AreEqual(serverValues.EnumU08,clientValues.EnumU08);
            Assert.AreEqual(serverValues.EnumS16,clientValues.EnumS16);
            Assert.AreEqual(serverValues.EnumU16,clientValues.EnumU16);
            Assert.AreEqual(serverValues.EnumS32,clientValues.EnumS32);
            Assert.AreEqual(serverValues.EnumU32,clientValues.EnumU32);
            Assert.AreEqual(serverValues.EnumS64,clientValues.EnumS64);
            Assert.AreEqual(serverValues.EnumU64,clientValues.EnumU64);

            Assert.AreEqual(serverValues.Float2Value, clientValues.Float2Value);
            Assert.AreEqual(serverValues.UnquantizedFloat2Value, clientValues.UnquantizedFloat2Value);
            Assert.AreEqual(serverValues.Float3Value, clientValues.Float3Value);
            Assert.AreEqual(serverValues.UnquantizedFloat3Value, clientValues.UnquantizedFloat3Value);
            Assert.AreEqual(serverValues.Float4Value, clientValues.Float4Value);
            Assert.AreEqual(serverValues.UnquantizedFloat4Value, clientValues.UnquantizedFloat4Value);
            Assert.Less(math.distance(serverValues.QuaternionValue.value, clientValues.QuaternionValue.value), 0.001f);
            Assert.AreEqual(serverValues.UnquantizedQuaternionValue, clientValues.UnquantizedQuaternionValue);

            Assert.AreEqual(serverValues.StringValue32, clientValues.StringValue32);
            Assert.AreEqual(serverValues.StringValue64, clientValues.StringValue64);
            Assert.AreEqual(serverValues.StringValue128, clientValues.StringValue128);
            Assert.AreEqual(serverValues.StringValue512, clientValues.StringValue512);
            Assert.AreEqual(serverValues.StringValue4096, clientValues.StringValue4096);
            Assert.AreEqual(serverValues.InvalidTickValue, clientValues.InvalidTickValue, $"{serverValues.InvalidTickValue.SerializedData} vs {clientValues.InvalidTickValue.SerializedData}");
            Assert.AreEqual(serverValues.TickValue, clientValues.TickValue);

            GhostValueSerializer.Union.Assertions();
            Assert.AreEqual(serverValues.UnionValue.State1.A,clientValues.UnionValue.State1.A);
            Assert.AreEqual(serverValues.UnionValue.State1.B,clientValues.UnionValue.State1.B);
            Assert.AreEqual(serverValues.UnionValue.State1.C,clientValues.UnionValue.State1.C);
            Assert.AreEqual(serverValues.UnionValue.State2.A,clientValues.UnionValue.State2.A);
            Assert.AreEqual(serverValues.UnionValue.State2.B,clientValues.UnionValue.State2.B);
            Assert.AreEqual(serverValues.UnionValue.State2.C,clientValues.UnionValue.State2.C);
            Assert.AreEqual(serverValues.UnionValue.State2.D,clientValues.UnionValue.State2.D);
            Assert.AreEqual(serverValues.UnionValue.State3.A,clientValues.UnionValue.State3.A);
            Assert.AreEqual(serverValues.UnionValue.State3.B,clientValues.UnionValue.State3.B);
        }
        void SetGhostValuesOnServer(NetCodeTestWorld testWorld, int baseValue, int length = 2)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, CreateGhostValues(baseValue, serverEntity));
            var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostValueBufferSerializer>(serverEntity);
            buffer.Length = length;
            for (int i = 0; i < length; i++)
            {
                buffer.ElementAt(i) = new GhostValueBufferSerializer {Values = CreateGhostValues(baseValue + i, serverEntity),};
            }
        }

        private static GhostValueSerializer CreateGhostValues(int baseValue, Entity serverEntity)
        {
            return new GhostValueSerializer
            {
                BoolValue = (baseValue&1) != 0,
                IntValue = baseValue,
                UIntValue = (uint)baseValue + 1u,
                LongValue = baseValue + 0x1234567898763210L,
                ULongValue = ((ulong)baseValue) + 0x8234567898763210UL,
                FloatValue = baseValue + 2,
                UnquantizedFloatValue = baseValue + 3,
                DoubleValue = 1234.456 + baseValue,
                UnquantizedDoubleValue = 123456789.123456789 + baseValue,

                EnumUntyped = EnumUntyped.Value0,
                EnumS08 = EnumS8.Value0,
                EnumU08 = EnumU8.Value0,
                EnumS16 = EnumS16.Value0,
                EnumU16 = EnumU16.Value0,
                EnumS32 = EnumS32.Value0,
                EnumU32 = EnumU32.Value0,
                EnumS64 = EnumS64.Value0,
                EnumU64 = EnumU64.Value0,

                Float2Value = new float2(baseValue + 4, baseValue + 5),
                UnquantizedFloat2Value = new float2(baseValue + 6, baseValue + 7),
                Float3Value = new float3(baseValue + 8, baseValue + 9, baseValue + 10),
                UnquantizedFloat3Value = new float3(baseValue + 11, baseValue + 12, baseValue + 13),
                Float4Value = new float4(baseValue + 14, baseValue + 15, baseValue + 16, baseValue + 17),
                UnquantizedFloat4Value = new float4(baseValue + 18, baseValue + 19, baseValue + 20, baseValue + 21),
                QuaternionValue = math.normalize(new quaternion(baseValue + 22, baseValue + 23, baseValue + 24, baseValue + 25)),
                UnquantizedQuaternionValue = math.normalize(new quaternion(baseValue + 26, baseValue + 27, baseValue + 28, baseValue + 29)),

                StringValue32 = new FixedString32Bytes($"baseValue = {baseValue}"),
                StringValue64 = new FixedString64Bytes($"baseValue = {baseValue*2}"),
                StringValue128 = new FixedString128Bytes($"baseValue = {baseValue*3}"),
                StringValue512 = new FixedString512Bytes($"baseValue = {baseValue*4}"),
                StringValue4096 = new FixedString4096Bytes($"baseValue = {baseValue*5}"),
                InvalidTickValue = NetworkTick.Invalid,
                TickValue = new NetworkTick((uint) baseValue),
                EntityValue = serverEntity,

                UnionValue = new GhostValueSerializer.Union
                {
                    // 不直接写入 Union 的 State1 或 State2
                    State3 =
                    {
                        A = baseValue * 11.5,
                        B = baseValue * 12.5,
                    },
                },
            };
        }

        void SetLargeGhostValues(NetCodeTestWorld testWorld, string baseValue, int size)
        {
            FixedString4096Bytes largeString = "";
            for (int i = 0; i <size; ++i)
            {
                largeString += baseValue;
            }

            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostValueSerializer
            {
                StringValue4096 = largeString,
                EntityValue = serverEntity
            });
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void ChangeMaskUtilitiesWorks()
        {
            // 使用 256 位有效 Mask，额外分配的位用于检查是否发生越界写入
            NativeArray<uint> mask = new NativeArray<uint>(9, Allocator.Temp);
            IntPtr maskPtr;
            unsafe { maskPtr = (IntPtr)mask.GetUnsafePtr(); }

            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.ResetChangeMask(maskPtr, 10, -1);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyFromChangeMask(maskPtr, -1, 0);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyFromChangeMask(maskPtr, 0, -1);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyToChangeMask(maskPtr, 10, -1, 0);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyToChangeMask(maskPtr, 10, 0, -1);});
            // 以下操作会跨越 32 位边界并一次设置多个位
            // 这些方法要求源值只设置目标范围所需的位，否则会覆盖 Mask 中的相邻位
            // 当前调用方式满足该前提，因此能够正常工作
            // 如后续需要接受更宽松的输入，可用少量 CPU 开销换取更健壮的边界屏蔽
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1, 10, 1);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x7, 14, 3);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1ff, 20, 9);
            // 预期结果为 0b0001_1111_1111_0001_1100_0100_0000_0000
            var maskValue = GhostComponentSerializer.CopyFromChangeMask(maskPtr, 0, 31);
            Assert.AreEqual(0b0001_1111_1111_0001_1100_0100_0000_0000, maskValue);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 1023, 60, 10);
            maskValue = GhostComponentSerializer.CopyFromChangeMask(maskPtr, 60, 10);
            Assert.AreEqual(1023, maskValue);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1, 255, 1);
            // 不应写入有效 Mask 之外的额外空间
            Assert.AreEqual(0, mask[8]);
            // 将有效 Mask 全部填充为 1
            for (int i = 0; i < 8; ++i)
                mask[i] = ~0u;
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0, 60, 9);
            Assert.AreEqual((1u<<(60-32)) -1, mask[1]);
            Assert.AreEqual(~((1u<<5) -1), mask[2]);
            mask[1] = ~0u;
            mask[2] = ~0u;
            GhostComponentSerializer.ResetChangeMask(maskPtr, 60, 9);
            Assert.AreEqual((1u<<(60-32)) -1, mask[1]);
            Assert.AreEqual(~((1u<<5) -1), mask[2]);
            mask[1] = ~0u;
            mask[2] = ~0u;
            GhostComponentSerializer.ResetChangeMask(maskPtr, 10, 73);
            // 验证 Mask 内容，目标范围内应有 73 个连续的 0
            Assert.AreEqual((1<<10) -1, mask[0]);
            Assert.AreEqual(0, mask[1]);
            Assert.AreEqual((~((1u << 19)-1)), mask[2]);
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void GhostValuesAreSerialized()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                SetGhostValuesOnServer(testWorld, 42);
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickUntilClientsHaveAllGhosts();

                VerifyGhostValues(testWorld);
                SetGhostValuesOnServer(testWorld, 43);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // 验证复制后的数据正确
                VerifyGhostValues(testWorld);
            }
        }

        internal enum SetMode
        {
            ConstantChanges,
            OnlyOneChange,
        }

#if !NETCODE_SNAPSHOT_HISTORY_SIZE_6
        // TODO：应补充测试以确认确实命中 GhostSendSystem.GatherGhostChunks 的 MaxSendRate 分支
        // 这需要更完善的统计信息支持
        [Test]
        public void GhostValuesAreSerialized_RespectsMaxSendRate([Values]SetMode setMode, [Values]GhostOptimizationMode optMode,
            [Values(1, 20, 100, 0)]int maxSendRate)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.SetTestLatencyProfile(NetCodeTestLatencyProfile.RTT60ms);
            const int snapshotAckLatencyInTicks = 2;

            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject($"Ghost_MaxSendRate_{maxSendRate}");
            var config = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            config.MaxSendRate = (byte)maxSendRate;
            // 使用预测模式，确保客户端始终应用最新数据
            config.SupportedGhostModes = GhostModeMask.Predicted;
            config.OptimizationMode = optMode;
            config.HasOwner = true;
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            var serverGhost = testWorld.SpawnOnServer(ghostGameObject);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, new GhostOwner { NetworkId = 1,});
            SetGhostValuesOnServer(testWorld, 0);
            testWorld.Connect(maxSteps:16);
            testWorld.GoInGame();
            testWorld.TickUntilClientsHaveAllGhosts();
            var firstSpawn = NetCodeTestWorld.TickIndex;

            // 在多个 Tick 中持续复制变更
            var serverValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            var clientValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            var clientEnt = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
            NetworkTick lastSnapshotTick = NetworkTick.Invalid;
            int numSnapshotsArrivedForGhost = 0;
            const int numTicksInTest = 25;
            for (int tick = 0; tick < numTicksInTest; ++tick)
            {
                if(setMode == SetMode.ConstantChanges || tick == 0) // OnlyOneChange 用例只在首个 Tick 修改一次
                    SetGhostValuesOnServer(testWorld, tick);
                testWorld.Tick();
                AddIfChanged(serverValues, tick, testWorld.ServerWorld);
                AddIfChanged(clientValues, tick - snapshotAckLatencyInTicks, testWorld.ClientWorlds[0]);

                var clientSnapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(clientEnt);
                var snapshotTick = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                if (snapshotTick != lastSnapshotTick)
                {
                    lastSnapshotTick = snapshotTick;
                    numSnapshotsArrivedForGhost++;
                }
            }
            Debug.Log($"firstSpawn:{firstSpawn} ticks, serverValues.Length:{serverValues.Length} vs clientValues.Length:{clientValues.Length}, numSnapshotsArrivedForGhost:{numSnapshotsArrivedForGhost}!");
            if(setMode == SetMode.ConstantChanges)
                Assert.That(serverValues.Length, Is.EqualTo(numTicksInTest), "Sanity!");
            else Assert.That(serverValues.Length, Is.GreaterThan(0), "Sanity!");

            var expectedNumChanges = maxSendRate switch
            {
                20 => 9,
                1 => 1,
                0 or 100 => numTicksInTest,
                _ => throw new ArgumentOutOfRangeException(nameof(maxSendRate), maxSendRate, null),
            };

            // 此 Ghost 收到的 Snapshot 数量允许小幅波动
            // 静态优化需要等待数个 Tick 才能收到 ACK，等待期间会按 MaxSendRate 限制继续尝试重发
            var (expectedMinSnapshots, expectedMaxSnapshots) = setMode == SetMode.ConstantChanges || optMode == GhostOptimizationMode.Dynamic
                ? (expectedNumChanges, expectedNumChanges)
                : (1, 3); // 服务端仍在等待 Ghost Spawn 的 ACK，因此最多可能收到 3 份 Snapshot
            Assert.That(numSnapshotsArrivedForGhost, Is.InRange(expectedMinSnapshots, expectedMaxSnapshots), nameof(numSnapshotsArrivedForGhost));

            var (expectedMinNumDistinct, expectedMaxNumDistinct) = (setMode, optMode, sendRate: maxSendRate) switch
            {
                (_, _, 1) or (SetMode.OnlyOneChange, _, _) => (1, 1),
                (SetMode.ConstantChanges, _, _) => (expectedNumChanges - snapshotAckLatencyInTicks, expectedNumChanges),
                _ => throw new ArgumentOutOfRangeException(),
            };
            var numClientValues = clientValues.Length;
            Assert.That(numClientValues, Is.InRange(expectedMinNumDistinct, expectedMaxNumDistinct));

            // 逐项验证客户端实际观察到的变更
            for (int i = 0; i < clientValues.Length; i++)
            {
                var (tick, val) = clientValues[i];
                if(tick >= 0 && tick < serverValues.Length)
                    VerifyGhostValues(serverValues[tick].val, val);
            }

            unsafe bool AddIfChanged(NativeList<(int tick, GhostValueSerializer val)> list, int tick, World world)
            {
                var previous = list.IsEmpty ? default : list[list.Length - 1];
                var current = testWorld.GetSingleton<GhostValueSerializer>(world);
                var memCmp = UnsafeUtility.MemCmp(&current, &previous.val, UnsafeUtility.SizeOf<GhostValueSerializer>());
                //UnityEngine.Debug.Log($"  - TestWorld[{NetCodeTestWorld.TickIndex}]  iteration:{tick} = previous:{previous.val.IntValue}, current:{current.IntValue} = memCmp:{memCmp} ");
                if (list.IsEmpty || memCmp != 0)
                {
                    list.Add((tick, current));
                    return true;
                }
                return false;
            }
        }
#endif

        [Test]
        public void GhostValuesAreSerialized_WithPacketDumpsEnabled()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DebugPackets = true;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                SetGhostValuesOnServer(testWorld, 42);
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickUntilClientsHaveAllGhosts();
                VerifyGhostValues(testWorld);
                SetGhostValuesOnServer(testWorld, 43);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // 验证复制后的数据正确
                VerifyGhostValues(testWorld);
            }
        }
        [Test]
        public void EntityReferenceSetAtSpawnIsResolved()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                var referencedGameObject = new GameObject();
                var ghostConfig = referencedGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick();
                }

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // 只要引用方 Ghost 已存在，其 Entity 引用就必须有效
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // 验证客户端最终确实收到被引用实体
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));
            }
        }
        [Test]
        public void EntityReferenceUnavailableGhostIsResolved()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                var referencedGameObject = new GameObject();
                var ghostConfig = referencedGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;

                // 建立连接并确认连接成功
                testWorld.Connect();
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                // 进入游戏状态
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick();
                }

                var con = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, con);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(con).Value;

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                testWorld.Tick();

                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                var serverRefGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverRefEntity).ghostId;

                // 先只将引用方实体设为相关，使其在被引用实体之前到达客户端
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // 运行若干 Tick，让引用方 Ghost 在客户端生成
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // 被引用 Ghost 尚未生成时，引用应保持为 Entity.Null
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // 被引用实体仍不相关，因此客户端不应收到它
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));

                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverRefGhostId), 1);
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // 被引用 Ghost 到达后，引用应解析为对应客户端实体
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));

                // 删除被引用实体，并验证 Entity 引用随之更新
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverRefEntity);
                int mismatchFrames = 0;
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // 客户端与服务端的 Despawn 顺序可能不同
                        // 服务端销毁后引用会立即失效，而客户端在帧末销毁实体，因此最多允许一帧状态不一致
                        Assert.IsFalse(clientRefEntity == Entity.Null && testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue != Entity.Null);
                        if (clientRefEntity != testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue)
                            ++mismatchFrames;
                    }
                }
                Assert.LessOrEqual(mismatchFrames, 1);
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));
            }
        }
        [Test]
        public void ManyEntitiesCanBeDespawnedSameTick([Values(NetCodeTestLatencyProfile.PL33, NetCodeTestLatencyProfile.RTT16ms_PL5)]NetCodeTestLatencyProfile profile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.SetTestLatencyProfile(profile);
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
                using (var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, 10000, Allocator.Persistent))
                {
                    testWorld.Connect(maxSteps:32);
                    testWorld.GoInGame();

                    // 运行若干 Tick，让全部 Ghost 在客户端生成
                    for (int i = 0; i < 200; ++i)
                        testWorld.Tick();

                    var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                    Assert.AreEqual(10000, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(10000, ghostCount.GhostCountReceivedOnClient);

                    testWorld.ServerWorld.EntityManager.DestroyEntity(entities);

                    for (int i = 0; i < 12; ++i)
                        testWorld.Tick();

                    // 验证同一 Tick 批量销毁的结果已正确复制
                    Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);
                }
            }
        }
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void SnapshotAckMaskIsReportedCorrectlyByTheClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var ghost = new GameObject("Ghost");
                ghost.AddComponent<GhostAuthoringComponent>();
                testWorld.CreateGhostCollection(new[] {ghost});
                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.SpawnOnServer(0);
                var lastReceivedFromClient = default(NetworkTick);
                uint currentMask = 0x1;
                for (int i = 0; i < 64; ++i)
                {
                    // 需要至少推进两个 Tick，服务端收到的始终是客户端上一 Tick 发出的数据
                    // 第一个 Tick（5）：服务端发送首个 Snapshot，客户端接收并更新本地 ACK，但尚未回传
                    // 第二个 Tick（6）：客户端更新时间至 Tick 9，接收 Tick 6 的 Snapshot，并发送 Tick 9 的 Command
                    // 第三个 Tick（7）：服务端开始观察到客户端发来的 Command 和 ACK
                    if (i > 2)
                    {
                        var currentServerTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                        var lastTickClientAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                        var serverAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                        Assert.That(serverAck.LastReceivedSnapshotByLocal.TickIndexForValidTick, Is.GreaterThanOrEqualTo(currentServerTick.TickIndexForValidTick + 2));
                        // 客户端已经从服务端收到 Snapshot 时
                        if (lastTickClientAck.LastReceivedSnapshotByLocal.IsValid)
                        {
                            // 同时检查服务端是否收到客户端返回的新 ACK 数据
                            if (!lastReceivedFromClient.IsValid || !lastReceivedFromClient.IsNewerThan(serverAck.LastReceivedSnapshotByLocal))
                                currentServerTick.Decrement();
                            var tickSince = currentServerTick.TicksSince(serverAck.LastReceivedSnapshotByRemote);
                            for (int tick = 0; tick < tickSince; ++tick)
                            {
                                currentServerTick.Decrement();
                                Assert.AreEqual(currentServerTick.TickIndexForValidTick, serverAck.LastReceivedSnapshotByRemote.TickIndexForValidTick);
                                Assert.IsTrue(serverAck.IsReceivedByRemote(currentServerTick));
                            }
                        }
                        lastReceivedFromClient = serverAck.LastReceivedSnapshotByLocal;
                    }
                    testWorld.Tick();
                    {
                        // 客户端记录首个已接收 Tick 后，还要延迟一帧才会把 ACK 回传给服务端
                        // 因此 i == 0 时客户端本地 ACK 已更新，但服务端尚未收到该 ACK
                        var currentServerTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                        var clientAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                        if (i == 0)
                        {
                            Assert.AreEqual(currentServerTick, clientAck.LastReceivedSnapshotByLocal);
                            Assert.AreEqual(1, clientAck.ReceivedSnapshotByLocalMask);
                            currentMask = 1;
                        }
                        else
                        {
                            currentMask <<= 1;
                            currentMask |= 0x1;
                            Assert.AreEqual(currentServerTick, clientAck.LastReceivedSnapshotByLocal);
                            Assert.AreEqual(currentMask, clientAck.ReceivedSnapshotByLocalMask);
                        }
                    }
                }
                // 包乱序或丢失时 ACK Mask 中应出现空洞，并与缺失 Tick 对应
                // 以下通过让客户端在同一帧收到多个递增 ID 的有效包来构造空洞
                // 同一帧只处理最后一个包，因此中间包不会被标记为已接收
                // 当前 Mask 为 1111 1111 1111 1111 1111 1111 1111 1111  1111 1111 1111 1111  1111 1111 1111 1111
                testWorld.TickServerWorld();
                testWorld.TickServerWorld();
                testWorld.TickClientWorld();
                // 客户端此时会用最新 Snapshot 覆盖上一份，并只报告最新一份的 ACK
                // Mask 应变为 1111 1111 1111 1111 1111 1111 1111 1111  1111 1111 1111 1111  1111 1111 1111 1101
                currentMask <<= 2;
                currentMask |= 0x1;
                var mask = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).ReceivedSnapshotByLocalMask;
                Assert.AreEqual(currentMask,mask);
                testWorld.TickServerWorld();
                testWorld.ServerWorld.EntityManager.CompleteDependencyBeforeRO<NetworkSnapshotAck>();
                var ack = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                var cur = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsFalse(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                // 验证 Mask 范围内最早的包仍被视为已确认
                for (int i = 4; i < 66; ++i)
                {
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                }
                // 验证更早且超出 Mask 范围的包均视为未确认
                for (int i = 66; i < 256; ++i)
                {
                    cur.Subtract(1);
                    Assert.IsFalse(ack.IsReceivedByRemote(cur));
                }
                testWorld.TickClientWorld();
                mask = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).ReceivedSnapshotByLocalMask;
                currentMask <<= 1;
                currentMask |= 0x1;
                Assert.AreEqual(currentMask,mask);
                cur = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                for (int i = 4; i < 256; ++i)
                {
                    testWorld.Tick();
                    // 验证最早几份包的 ACK 状态随窗口推进仍然正确
                    ack = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsFalse(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Add(3);
                }
            }
        }
        [Test]
        public void GhostValuesAreSerializedWhenLargerThanMaxMessageSize()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.LogLevel = NetDebug.LogLevelType.Debug; // 需要此日志等级才能输出 PERFORMANCE 警告
                testWorld.DriverMaxMessageSize = 548;
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                SetLargeGhostValues(testWorld, "a", testWorld.DriverMaxMessageSize * 2);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                VerifyGhostValues(testWorld);
                SetLargeGhostValues(testWorld, "b", testWorld.DriverMaxMessageSize * 2);

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // 验证超出单包大小的数据仍能正确复制
                VerifyGhostValues(testWorld);

                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
            }
        }

        [Test]
        public void TooSmall_SnapshotPacketSize_FailsGracefully_ViaMaxSnapshotSendAttempts([Values]bool useNetworkStreamSnapshotTargetSize)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.LogLevel = NetDebug.LogLevelType.Debug; // 需要此日志等级才能输出 PERFORMANCE 警告
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            const int maxMessageSize = GhostSystemConstants.MinSnapshotPacketSize;
            testWorld.SpawnOnServer(ghostGameObject);
            var maxTheoreticalSizeGhostSendSystemCanSend = (int)(maxMessageSize * math.pow(2, GhostSystemConstants.MaxSnapshotSendAttempts-1)); // 忽略包头等额外开销
            SetGhostValuesOnServer(testWorld, 43, (int) (maxTheoreticalSizeGhostSendSystemCanSend * 0.01f)); // Buffer 内包含体积很大的结构

            testWorld.Connect();
            testWorld.GoInGame();

            // 配置 Snapshot 包大小上限
            ref var ghostSendSystemData = ref testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW;
            ghostSendSystemData.TempStreamInitialSize *= 32; // 避免临时 Stream 容量不足触发另一类溢出错误
            if (useNetworkStreamSnapshotTargetSize)
            {
                var ent = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new NetworkStreamSnapshotTargetSize
                {
                    Value = maxMessageSize,
                });
            }
            else
            {
                ghostSendSystemData.DefaultSnapshotPacketSize = maxMessageSize;
            }

            testWorld.Tick();
            testWorld.Tick();
            testWorld.Tick();
            for(int i = 0; i < GhostSystemConstants.MaxSnapshotSendAttempts - 1; i++)
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
            LogAssert.Expect(LogType.Error, new Regex(@$"FATAL(.*){nameof(GhostSystemConstants.MaxSnapshotSendAttempts)}(.*)NID\[1\]"));
        }

#if NETCODE_SNAPSHOT_HISTORY_SIZE_6
        [Test(Description = "When the snapshot history is small, users can fill up the snapshot history buffer with in-flight snapshot packets. This test ensures we gracefully process this case.")]
        public void SnapshotHistorySize6_TriggersHistoryBufferSaturation_Gracefully()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 100; // 让 Snapshot 在更多 Tick 内保持传输中状态
            testWorld.LogLevel = NetDebug.LogLevelType.Debug; // 需要此日志等级才能输出 PERFORMANCE 警告
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.SpawnOnServer(ghostGameObject);
            testWorld.Connect(maxSteps:32);
            testWorld.GoInGame();

            for(int i = 0; i < 24; i++)
                testWorld.Tick();
            LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE\: Snapshot history is saturated for ghost chunk:(\d*), ghostType\:0, 4\/6 in\-flight \(TSLR\:15\<\=16\), sent anyway\:(true|false)\!"));
        }
#endif
    }
}
