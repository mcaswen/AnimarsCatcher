using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.NetCode.Tests
{
    internal struct PredictionSwitchComponent : IComponentData { } // 标识本测试使用的 Ghost

    internal class PredictionSwitchTestConverter : TestNetCodeAuthoring.IConverter
    {
        internal const int bufferElementCount = 100;

        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new PredictionSwitchComponent());
            baker.AddComponent(entity, new PredictedOnlyTestComponent{Value = 42});
            baker.AddComponent(entity, new InterpolatedOnlyTestComponent{Value = 43});
            var buffer = baker.AddBuffer<BufferInterpolatedOnlyTestComponent>(entity);
            for (int i = 0; i < bufferElementCount; i++)
            {
                buffer.Add(new BufferInterpolatedOnlyTestComponent() { Value = i });
            }
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    internal struct PredictedOnlyTestComponent : IComponentData
    {
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    internal struct InterpolatedOnlyTestComponent : IComponentData
    {
        public int Value;
    }

    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    [InternalBufferCapacity(0)] // 让错误内存访问立即暴露，避免内部容量掩盖越界覆盖问题
    internal struct BufferInterpolatedOnlyTestComponent : IBufferElementData
    {
        [GhostField] public int Value;
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class PredictionSwitchMoveTestSystem : SystemBase
    {
        public static NetworkTick TickFreeze;
        public static bool SkipOneOfTwo;
        protected override void OnCreate()
        {
            TickFreeze = NetworkTick.Invalid;
            SkipOneOfTwo = false;
        }

        public const float k_valueIncrease = 5f;

        protected override void OnUpdate()
        {
            var currentTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if (TickFreeze != NetworkTick.Invalid && currentTick.IsNewerThan(TickFreeze)) return;
            // 启用跳帧时只在奇数 Tick 更新 Transform
            if (SkipOneOfTwo && (currentTick.TickIndexForValidTick&1u) == 0)
                return;
            foreach (var trans in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<PredictionSwitchComponent>().WithAll<Simulate>())
            {
                trans.ValueRW.Position += new float3(k_valueIncrease, 0, 0);
                trans.ValueRW = trans.ValueRO.RotateX(math.radians(k_valueIncrease));
            }
        }
    }

    internal class PredictionSwitchTests
    {
        [Test]
        public void SwitchingPredictionAddsAndRemovesComponent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionSwitchTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                // Ghost 默认使用插值模式

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);

                // 建立连接并确认连接成功
                testWorld.Connect();

                // 进入游戏状态
                testWorld.GoInGame();

                // 运行若干 Tick，让客户端生成 Ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var firstClientWorld = testWorld.ClientWorlds[0];
                var clientEnt = testWorld.TryGetSingletonEntity<PredictionSwitchComponent>(firstClientWorld);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                // 验证实体初始为插值模式，并仅包含插值模式组件
                var entityManager = firstClientWorld.EntityManager;
                ref var ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;

                Assert.IsFalse(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);
                var buffer = entityManager.GetBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt);
                for (int i = 0; i < PredictionSwitchTestConverter.bufferElementCount; i++)
                {
                    Assert.AreEqual(buffer[i].Value, i);
                }

                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 0f,
                });
                testWorld.Tick();
                Assert.IsTrue(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(42, entityManager.GetComponentData<PredictedOnlyTestComponent>(clientEnt).Value);

                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 2f,
                });
                testWorld.Tick();
                Assert.IsFalse(entityManager.HasComponent<PredictedGhost>(clientEnt));
                Assert.IsFalse(entityManager.HasComponent<PredictedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<InterpolatedOnlyTestComponent>(clientEnt));
                Assert.IsTrue(entityManager.HasComponent<SwitchPredictionSmoothing>(clientEnt));
                Assert.AreEqual(43, entityManager.GetComponentData<InterpolatedOnlyTestComponent>(clientEnt).Value);
                buffer = entityManager.GetBuffer<BufferInterpolatedOnlyTestComponent>(clientEnt);
                for (int i = 0; i < PredictionSwitchTestConverter.bufferElementCount; i++)
                {
                    Assert.AreEqual(buffer[i].Value, i);
                }
            }
        }

        // 使用 Clamp 且不量化，尽量消除插值噪声并提高验证精度
        [GhostComponentVariation(typeof(Transforms.LocalTransform), nameof(ClampedTransformVariant))]
        [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
        internal struct ClampedTransformVariant
        {
            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public float3 Position;

            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public float Scale;

            [GhostField(Quantization=0, Smoothing=SmoothingAction.Clamp)]
            public quaternion Rotation;
        }

        [DisableAutoCreation]
        [CreateBefore(typeof(Unity.NetCode.TransformDefaultVariantSystem))]
        sealed partial class ClampedTransformVariantRegisterSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(LocalTransform), Rule.ForAll(typeof(ClampedTransformVariant)));
            }
        }

        static ref GhostPredictionSwitchingQueues InitTest(NetCodeTestWorld testWorld, bool UseOwnerPredicted, out Vector3 originalPosParent, out World firstClientWorld, out EntityManager entityManager, out EntityQuery timeQuery, out Entity clientEnt, out float originalRotation)
        {
            var ghostGameObject = new GameObject();
            var childGameObject = new GameObject();

            childGameObject.transform.parent = ghostGameObject.transform;

            childGameObject.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionSwitchTestConverter();
            originalRotation = 45f;
            ghostGameObject.transform.Rotate(new Vector3(0, originalRotation, 0)); // 使用非零初始旋转，验证矩阵运算正确
            originalPosParent = new Vector3(10, 20, 30);
            ghostGameObject.transform.position = originalPosParent;
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
            ghostConfig.HasOwner = UseOwnerPredicted;
            ghostConfig.SupportAutoCommandTarget = UseOwnerPredicted;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

            testWorld.CreateWorlds(true, 1);

            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            // 建立连接并确认连接成功
            testWorld.Connect();

            // 进入游戏状态
            testWorld.GoInGame();

            firstClientWorld = testWorld.ClientWorlds[0];
            entityManager = firstClientWorld.EntityManager;
            timeQuery = entityManager.CreateEntityQuery(typeof(NetworkTime));
            PredictionSwitchMoveTestSystem.SkipOneOfTwo = false;
            // 等待客户端时间同步并生成 Ghost
            for (int i = 0; i < 60; ++i)
                testWorld.Tick();

            clientEnt = testWorld.TryGetSingletonEntity<PredictionSwitchComponent>(firstClientWorld);
            Assert.AreNotEqual(Entity.Null, clientEnt);

            // 验证实体初始处于插值模式
            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");
            return ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
        }

        [Test]
        public void SwitchingPredictionSmoothChildEntities()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var fuzzyEqual = 0.0001f;

                testWorld.Bootstrap(true, typeof(PredictionSwitchMoveTestSystem), typeof(ClampedTransformVariantRegisterSystem));

                ref var ghostPredictionSwitchingQueues = ref InitTest(testWorld, false, out var originalPosParent, out var firstClientWorld, out var entityManager, out var timeQuery, out var clientEnt, out var originalRotation);

                var childEnt = entityManager.GetBuffer<LinkedEntityGroup>(clientEnt)[1].Value;
                Assert.AreNotEqual(Entity.Null, childEnt);
                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEnt,
                    TransitionDurationSeconds = 1f,
                });

                var originalLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                testWorld.Tick(); // 执行一次预测迭代，使各 Transform 进入目标状态
                PredictionSwitchMoveTestSystem.TickFreeze = timeQuery.GetSingleton<NetworkTime>().ServerTick; // 冻结预测目标位置，便于验证平滑过程

                Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), "Sanity check failed, the entity should be marked as predicted now");
                var networkTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                // 预测 Tick 与插值 Tick 的最小差值由 2 个插值延迟、2 个 TargetCommandSlack、2 个同步 Tick 及小数部分组成
                var currentDeltaTickBetweenInterpAndPredictTick = networkTime.ServerTick.TicksSince(networkTime.InterpolationTick);
                Assert.GreaterOrEqual(currentDeltaTickBetweenInterpAndPredictTick, 6);
                currentDeltaTickBetweenInterpAndPredictTick += 1; // 复制 originalLocalToWorld 后又推进了一个 Tick
                var expectedIncrementPerTick = (currentDeltaTickBetweenInterpAndPredictTick * PredictionSwitchMoveTestSystem.k_valueIncrease) / 60f; // 每帧补偿预测与插值位置差值的六十分之一
                // 平滑持续 1 秒且以 60 Hz 运行，因此需要 60 帧到达预测目标
                {
                    var localToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                    var predictedTargetTransform = entityManager.GetComponentData<LocalTransform>(clientEnt);

                    Assert.That(math.distance(localToWorld.Position, predictedTargetTransform.Position), Is.Not.InRange(-fuzzyEqual, fuzzyEqual), "Sanity check failed, current value shouldn't be equal to predicted value");
                    Assert.That(math.degrees(math.angle(localToWorld.Rotation, predictedTargetTransform.Rotation)), Is.Not.InRange(-fuzzyEqual, fuzzyEqual), "Sanity check failed, current value shouldn't be equal to predicted value");

                    // 验证平滑首帧仍接近切换前的插值 Transform，防止 MTT-8430 回归
                    Assert.That(math.distance(localToWorld.Position, originalLocalToWorld.Position), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual), "Wrong expected first tick value for pos after switch smoothing lerp");
                    Assert.That(math.degrees(math.angle(localToWorld.Rotation, originalLocalToWorld.Rotation)), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual), "Wrong expected first tick value for rot after switch smoothing lerp");
                    Assert.That((localToWorld.Position - originalLocalToWorld.Position).x, Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual));
                    Assert.That(localToWorld.Position.y, Is.EqualTo(originalPosParent.y));
                    Assert.That(localToWorld.Position.z, Is.EqualTo(originalPosParent.z));
                    Assert.That(localToWorld.Position, Is.Not.EqualTo(Vector3.zero));
                    Assert.That(math.degrees(math.Euler(localToWorld.Rotation).x) - math.degrees(math.Euler(originalLocalToWorld.Rotation).x), Is.InRange(expectedIncrementPerTick - fuzzyEqual, expectedIncrementPerTick + fuzzyEqual));
                    Assert.That(math.degrees(math.Euler(localToWorld.Rotation)).y, Is.InRange(originalRotation - fuzzyEqual, originalRotation + fuzzyEqual));
                    Assert.That(math.Euler(localToWorld.Rotation).z, Is.InRange(-fuzzyEqual, +fuzzyEqual));

                    for (int i = 0; i < 60; i++)
                    {
                        testWorld.Tick();
                    }

                    localToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                    // 确认 60 帧后已到达预测目标 Transform
                    Assert.That(localToWorld.Position, Is.EqualTo(predictedTargetTransform.Position));
                    Assert.That(math.angle(localToWorld.Rotation, predictedTargetTransform.Rotation), Is.InRange(-fuzzyEqual, +fuzzyEqual));
                }

                {
                    // 验证移动中的预测 Ghost 每帧更新位置
                    // 同时确保父实体与子实体的 LocalToWorld 始终一致

                    // 准备测试状态
                    {
                        // 先切回插值模式
                        ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                        Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.True, "Sanity check failed, the entity should be marked as interpolated now");
                        ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0f,
                        });
                        for (int i = 0; i < 16; i++)
                        {
                            testWorld.Tick();
                        }
                    }
                    {
                        // 再切到预测模式，执行后续测试
                        ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                        Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");

                        ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 1f,
                        });
                    }

                    // 恢复移动，并启用隔 Tick 更新
                    PredictionSwitchMoveTestSystem.SkipOneOfTwo = true;
                    PredictionSwitchMoveTestSystem.TickFreeze = NetworkTick.Invalid;

                    testWorld.Tick(); // 执行模式转换与预测

                    var oldLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);

                    // 验证平滑期间的逐帧更新

                    for (int i = 0; i < 60; ++i)
                    {
                        testWorld.Tick();
                        var nextLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                        Assert.AreNotEqual(oldLocalToWorld.Value, nextLocalToWorld.Value, $"i is {i}");
                        var childLocalToWorld = entityManager.GetComponentData<LocalToWorld>(childEnt);
                        Assert.AreEqual(nextLocalToWorld.Value, childLocalToWorld.Value, $"i is {i}");

                        oldLocalToWorld = nextLocalToWorld;
                    }
                    PredictionSwitchMoveTestSystem.TickFreeze = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]).ServerTick;

                    testWorld.Tick(); // 再推进一个 Tick，确认状态稳定

                    Assert.That(math.distance(oldLocalToWorld.Position, entityManager.GetComponentData<LocalToWorld>(clientEnt).Position), Is.InRange(-fuzzyEqual, fuzzyEqual));
                    Assert.That(math.angle(oldLocalToWorld.Rotation, entityManager.GetComponentData<LocalToWorld>(clientEnt).Rotation), Is.InRange(-fuzzyEqual, +fuzzyEqual));
                }
            }
        }


        [Test]
        public void TestSwitchAndInterpolation([Values] bool UseOwnerPredicted, [Values] bool testInterruptSwitch)
        {
            using var testWorld = new NetCodeTestWorld();
            PredictionSwitchMoveTestSystem.SkipOneOfTwo = false;
            PredictionSwitchMoveTestSystem.TickFreeze = NetworkTick.Invalid;

            testWorld.Bootstrap(true, typeof(PredictionSwitchMoveTestSystem));

            ref var ghostPredictionSwitchingQueues = ref InitTest(testWorld, UseOwnerPredicted, out var originalPosParent, out var firstClientWorld, out var entityManager, out var timeQuery, out var clientEnt, out var originalRotation);

            var oldLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
            // 切到预测模式，执行后续平滑测试
            ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.Not.True, "Sanity check failed, the entity should be marked as interpolated now");

            ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
            {
                TargetEntity = clientEnt,
                TransitionDurationSeconds = 1f,
            });

            testWorld.Tick();

            Assert.That(entityManager.HasComponent<PredictedGhost>(clientEnt), Is.True, "Sanity check failed, the entity should be marked as interpolated now");

            var predictedTickDiff = 7; // 预测 Tick 与插值 Tick 的差值
            var valueIncreasePerTick = PredictionSwitchMoveTestSystem.k_valueIncrease;
            var distancePredictedToInterpolated = valueIncreasePerTick * predictedTickDiff;
            var incrementApproximation = distancePredictedToInterpolated / 60f + valueIncreasePerTick;
            var veryFuzzyEqual = incrementApproximation * 0.5f; // 双重插值只验证移动方向与大致幅度，允许正负 50% 误差

            for (int i = 0; i < 59; ++i)
            {
                testWorld.Tick();
                var nextLocalToWorld = entityManager.GetComponentData<LocalToWorld>(clientEnt);
                if (testInterruptSwitch)
                {
                    // 中途反复切换的数值结果未定义，此处只验证流程不报错
                    ghostPredictionSwitchingQueues = ref testWorld.GetSingletonRW<GhostPredictionSwitchingQueues>(firstClientWorld).ValueRW;
                    if (i == 20)
                    {
                        ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry()
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0.1f,
                        });
                    }
                    else if (i == 30)
                    {
                        ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry()
                        {
                            TargetEntity = clientEnt,
                            TransitionDurationSeconds = 0.1f,
                        });
                    }
                    Assert.AreNotEqual(oldLocalToWorld.Value, nextLocalToWorld.Value, $"i is {i}");
                }
                else
                {
                    // 每帧包含正常预测位移 k_valueIncrease，以及为追上预测目标而分摊的平滑补偿
                    Assert.That((nextLocalToWorld.Position - oldLocalToWorld.Position).x, Is.InRange(incrementApproximation - veryFuzzyEqual, incrementApproximation + veryFuzzyEqual), $"i is {i}");
                }

                oldLocalToWorld = nextLocalToWorld;
            }

            testWorld.Tick();
            // 切换完成后，每 Tick 位移应恢复为固定的 k_valueIncrease
            Assert.That((entityManager.GetComponentData<LocalToWorld>(clientEnt).Position - oldLocalToWorld.Position).x, Is.EqualTo(valueIncreasePerTick));
        }

        // 如果先存在一个预测 Ghost，随后一段时间没有预测 Ghost，之后再次切回预测模式
        // 系统不应回滚到上一次存在预测 Ghost 的旧 Tick
        [Test]
        public void DoesNotRollbackAfterPredictionSwitching()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.SupportedGhostModes = GhostModeMask.All;
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                testWorld.Connect();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();

                var clientQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(PredictedGhost));
                int i = 0;
                while (clientQuery.IsEmpty)
                {
                    testWorld.Tick();
                    i++;
                    if (i > 16)
                    {
                        Assert.Fail("Timed out waiting for predicted ghost to spawn");
                        return;
                    }
                }

                var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.Greater(clientTime.PredictedTickIndex, 0);

                // 切换到非预测模式
                var clientEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                var ghostPredictionSwitchingQueues = testWorld.GetSingleton<GhostPredictionSwitchingQueues>(testWorld.ClientWorlds[0]);
                ghostPredictionSwitchingQueues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEntity,
                });
                testWorld.Tick();

                clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.AreEqual(0, clientTime.PredictedTickIndex);

                // 运行到 Command 历史上限，减去预测提前的 2 个 Tick
                for (i = 0; i < CommandDataUtility.k_CommandDataMaxSize - 2; ++i)
                {
                    testWorld.Tick();
                }

                // 再切回预测模式
                ghostPredictionSwitchingQueues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                {
                    TargetEntity = clientEntity,
                });

                i = 0;
                while (clientQuery.IsEmpty)
                {
                    testWorld.Tick();
                    i++;
                    if (i > 16)
                    {
                        Assert.Fail("Timed out waiting for predicted ghost to spawn");
                        return;
                    }
                }

                for (i = 0; i < 3; i++)
                {
                    clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    Assert.IsTrue(clientTime.PredictedTickIndex > 0 && clientTime.PredictedTickIndex < 5);
                    testWorld.Tick();
                }
            }
        }
    }
}
