#pragma warning disable CS0618 // 禁用 Entities.ForEach 过时警告
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostExtrapolationConverter : TestNetCodeAuthoring.IConverter
    {
        public TestExtrapolated TestExtrapolated;

        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, in TestExtrapolated);
        }
    }

    internal struct TestExtrapolated : IComponentData
    {
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=5)]
        public float ReceivedValueIaE;
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=0.1f)]
        public float ReceivedValueIaEWithMaxSmoothingDistance; // 特殊情况
        [GhostField(Smoothing=SmoothingAction.Interpolate, MaxSmoothingDistance=5)]
        public float ReceivedValueInterp;
        [GhostField(Smoothing=SmoothingAction.Clamp)]
        public float ReceivedValueClamp;
        [GhostField(Smoothing=SmoothingAction.InterpolateAndExtrapolate, MaxSmoothingDistance=5)]
        public float PredictedValueIaE;
        [GhostField(Smoothing=SmoothingAction.Interpolate, MaxSmoothingDistance=5)]
        public float PredictedValueInterp;
        [GhostField(Smoothing=SmoothingAction.Clamp)]
        public float PredictedValueClamp;
        public int? TicksSinceClampedValueChanged;
        public GhostMode GhostMode;
        public GhostOptimizationMode OptimizationMode;
    }

    internal struct ExtrapolateBackup : IComponentData
    {
        public NetworkTick Tick;
        public float Fraction;
        public float ReceivedValueIaE;
        public float ReceivedValueIaEWithMaxSmoothingDistance;
        public float ReceivedValueInterp;
        public float ReceivedValueClamp;
        public float PredictedValueIaE;
        public float PredictedValueInterp;
        public float PredictedValueClamp;
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class MoveExtrapolated : SystemBase
    {
        protected override void OnUpdate()
        {
            var dt = SystemAPI.Time.DeltaTime;
            var isServer = World.IsServer();
            foreach (var valRef in SystemAPI.Query<RefRW<TestExtrapolated>>().WithAll<Simulate>())
            {
                ref var val = ref valRef.ValueRW;
                if (isServer)
                {
                    val.ReceivedValueIaE += dt;
                    val.ReceivedValueClamp += dt;
                    val.ReceivedValueInterp += dt;
                    val.ReceivedValueIaEWithMaxSmoothingDistance += dt;
                }
                val.PredictedValueIaE += dt;
                val.PredictedValueClamp += dt;
                val.PredictedValueInterp += dt;
            }
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    internal partial class CheckExtrapolate : SystemBase
    {
        private const float DrawDurationSeconds = 180;
        public static uint NumStepsTested;

        protected override void OnUpdate()
        {
            var nTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!nTime.ServerTick.IsValid || !SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate)) return;
            var white = new Color(1f, 1f, 1f, 0.9f);
            var pink = new Color(1f, 0.16f, 0.98f, 0.9f);
            var black = new Color(0f, 0f, 0f, 0.9f);

            foreach (var (currentRef, backupRef, clientEntity) in SystemAPI.Query<RefRW<TestExtrapolated>, RefRW<ExtrapolateBackup>>().WithEntityAccess())
            {
                ref var current = ref currentRef.ValueRW;
                ref var backup = ref backupRef.ValueRW;
                var hasNewSnapshotContainingThisGhost = current.ReceivedValueClamp != backup.ReceivedValueClamp;

                // 忽略最开始的若干次更新
                if (backup.ReceivedValueIaE != default && backup.Tick.IsValid)
                {
                    // 绘制柱状曲线，其中 X 表示时间，Y 表示字段值
                    const float barScale = 0.01f;
                    var length = (nTime.InterpolationTick.TickIndexForValidTick + nTime.InterpolationTickFraction) * barScale;
                    var backupLength = (backup.Tick.TickIndexForValidTick + backup.Fraction) * barScale;

                    // 用于辅助可视化调试
                    const float xOffset = 4f;
                    var (color, _, x) = (current.OptimizationMode, current.GhostMode) switch
                    {
                        (GhostOptimizationMode.Dynamic, GhostMode.Interpolated) => (Color.green, "green", -3 * xOffset),
                        (GhostOptimizationMode.Dynamic, GhostMode.Predicted) => (Color.cyan, "cyan", -2 * xOffset),
                        (GhostOptimizationMode.Dynamic, GhostMode.OwnerPredicted) => (Color.blue, "blue", -1 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.Interpolated) => (Color.yellow, "yellow", +0 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.Predicted) => (new Color(1f, 0.5f, 0f), "orange", +1 * xOffset),
                        (GhostOptimizationMode.Static, GhostMode.OwnerPredicted) => (Color.magenta, "magenta", +2 * xOffset),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    color.a = 0.5f; // 增加半透明效果，便于观察两条线是否重叠
                    x += ExtrapolationTests.TMode switch
                    {
                        ExtrapolationTests.NetcodeSetupMode.OnlyInterpolate100ms => -40,
                        ExtrapolationTests.NetcodeSetupMode.SmallestInterpolationWindowAndExtrapolate100ms => 0,
                        ExtrapolationTests.NetcodeSetupMode.Interpolate50msThenExtrapolate50ms => 40,
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    Debug.DrawLine(new Vector3(x + length, 0, 0), new Vector3(x + length, 0 + current.ReceivedValueIaE, 0), color, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueInterp, 0), new Vector3(x + length, current.ReceivedValueInterp, 0), white, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueIaEWithMaxSmoothingDistance, 0), new Vector3(x + length, current.ReceivedValueIaEWithMaxSmoothingDistance, 0), pink, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, backup.ReceivedValueClamp, 0), new Vector3(x + length, current.ReceivedValueClamp, 0), black, DrawDurationSeconds);

                    Debug.DrawLine(new Vector3(x + length, 0, 0), new Vector3(x + length, 0 + -current.PredictedValueIaE, 0), color, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.PredictedValueInterp, 0), new Vector3(x + length, -current.PredictedValueInterp, 0), white, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.ReceivedValueIaEWithMaxSmoothingDistance, 0), new Vector3(x + length, -current.ReceivedValueIaEWithMaxSmoothingDistance, 0), pink, DrawDurationSeconds);
                    Debug.DrawLine(new Vector3(x + backupLength, -backup.PredictedValueClamp, 0), new Vector3(x + length, -current.PredictedValueClamp, 0), black, DrawDurationSeconds);

                    // 每次收到 Snapshot 时绘制标记
                    const float markerLength = 0.3f;
                    var expectedDeltaStep = tickRate.SimulationFixedTimeStep;
                    var numPredictedTicks = nTime.ServerTick.TicksSince(nTime.InterpolationTick);

                    var log = ExtrapolationTests.TestLog[clientEntity];
                    if (hasNewSnapshotContainingThisGhost)
                    {
                        Debug.DrawRay(new Vector3(x + backupLength, backup.ReceivedValueIaE, 0), new Vector3(-markerLength * 0.5f, markerLength, 0), Color.green, DrawDurationSeconds);
                        log += ($"\n\n-- New Snapshot! ?:{current.TicksSinceClampedValueChanged} ticks");

                        var isReceivingSnapshotsTooFrequently = current.TicksSinceClampedValueChanged < (tickRate.SimulationTickRate / 2) - 2;
                        if (isReceivingSnapshotsTooFrequently)
                            log += ($"\nFATAL! isReceivingSnapshotsTooFrequently:{current.TicksSinceClampedValueChanged}");
                        var interpolationBufferTooBig = numPredictedTicks > 12;
                        if (interpolationBufferTooBig)
                            log += ($"\nFATAL! interpolationBufferTooBig:{numPredictedTicks}");

                        current.TicksSinceClampedValueChanged = 0;
                    }
                    else if(current.TicksSinceClampedValueChanged.HasValue) current.TicksSinceClampedValueChanged++;

                    log += $"\nST:{nTime.ServerTick.ToFixedString()} IT:{nTime.InterpolationTick.ToFixedString()} ?:{numPredictedTicks} TSCVC:{current.TicksSinceClampedValueChanged} --";
                    NumStepsTested++;

                    // 预期行为
                    var exp = ExtrapolationTests.GetExpectedResults(in current);
                    TestValue(1, exp.ExpectedRIaE, current.ReceivedValueIaE, backup.ReceivedValueIaE, ref log, "RIaE", current.TicksSinceClampedValueChanged, true);
                    TestValue(1, exp.ExpectedRIaEWithMaxSmoothingDistance, current.ReceivedValueIaEWithMaxSmoothingDistance, backup.ReceivedValueIaEWithMaxSmoothingDistance, ref log, "RInterp-MSD", current.TicksSinceClampedValueChanged, false);
                    TestValue(1, exp.ExpectedRInterp, current.ReceivedValueInterp, backup.ReceivedValueInterp, ref log, "RInterp", current.TicksSinceClampedValueChanged, false);
                    TestValue(1, exp.ExpectedRClamp, current.ReceivedValueClamp, backup.ReceivedValueClamp, ref log, "RClamp", current.TicksSinceClampedValueChanged, false);
                    TestValue(-1, exp.ExpectedPIaE, current.PredictedValueIaE, backup.PredictedValueIaE, ref log, "PIaE", current.TicksSinceClampedValueChanged, true);
                    TestValue(-1, exp.ExpectedPInterp, current.PredictedValueInterp, backup.PredictedValueInterp, ref log, "PInterp", current.TicksSinceClampedValueChanged, false);
                    TestValue(-1, exp.ExpectedPClamp, current.PredictedValueClamp, backup.PredictedValueClamp, ref log, "PClamp", current.TicksSinceClampedValueChanged, false);

                    void TestValue(float yMul, Result expectedResult, float currentVal, float previousVal, ref string log2, string name, int? ticksSinceClampedValueChangedLocal, bool isExtrapolating)
                    {
                        var result = Result.Unknown;
                        const float clampTolerance = 0.005f;
                        var delta = currentVal - previousVal;
                        var deltaToDelta = math.abs(expectedDeltaStep - delta);
                        var isSmooth = deltaToDelta <= expectedDeltaStep * ExtrapolationTests.k_StepTolerance;
                        if(isSmooth) result = Result.Smooth;
                        var modDelta = math.abs(delta) % expectedDeltaStep;
                        var deltaToModDelta = math.min(modDelta, math.abs(expectedDeltaStep - modDelta));
                        if (!isSmooth && deltaToModDelta <= clampTolerance) result = Result.Clamp;
                        if (delta < 0) result = Result.Negative;
                        log2 += $"\n\t{name}    \t >> {currentVal:0.000} {result.ToString()} ";
                        //log2 += $"%?:{(delta % expectedDeltaStep):0.000} {1f-(delta/expectedDeltaStep):p0}";
                        if (result != expectedResult && expectedResult != Result.Any)
                        {
                            log2 += $" < EXPECTED {expectedResult}";
                            Debug.DrawRay(new Vector3(x + length, yMul * currentVal, 0), new Vector3(-markerLength, yMul * markerLength, 0), Color.red, DrawDurationSeconds);
                        }
                    }
                    ExtrapolationTests.TestLog[clientEntity] = log;
                }

                // 更新备份
                backup.ReceivedValueIaE = current.ReceivedValueIaE;
                backup.ReceivedValueInterp = current.ReceivedValueInterp;
                backup.ReceivedValueIaEWithMaxSmoothingDistance = current.ReceivedValueIaEWithMaxSmoothingDistance;
                backup.ReceivedValueClamp = current.ReceivedValueClamp;
                backup.PredictedValueIaE = current.PredictedValueIaE;
                backup.PredictedValueInterp = current.PredictedValueInterp;
                backup.PredictedValueClamp = current.PredictedValueClamp;

                backup.Tick = nTime.InterpolationTick;
                backup.Fraction = nTime.InterpolationTickFraction;
            }
        }
    }

    internal enum Result
    {
        Unknown,
        /// <summary>
        /// 表示值已经或应当按 DeltaTime 沿正方向平滑增长，不出现明显跳变或负值
        /// </summary>
        Smooth,
        /// <summary>
        /// 表示值已经或应当 Clamp 到最新值，形成通常不变、偶尔跨多个 Tick 跳变的阶梯形态
        /// </summary>
        Clamp,
        /// <summary>
        /// 表示值已经或应当变为负数
        /// </summary>
        Negative,
        /// <summary>
        /// 表示允许任意值或跳过检查
        /// </summary>
        Any,
    }

    internal class ExtrapolationTests
    {
        public static NetcodeSetupMode TMode;
        internal enum NetcodeSetupMode
        {
            OnlyInterpolate100ms,
            /// <summary>
            /// 无法完全禁用插值，即使将窗口设为 0 ms
            /// NetCode 仍需要并会使用若干帧进行插值
            /// </summary>
            SmallestInterpolationWindowAndExtrapolate100ms,
            Interpolate50msThenExtrapolate50ms,
        }

        public const float k_StepTolerance = 0.001f;
        public static Dictionary<Entity,string> TestLog;
        /// <summary>
        /// 测试最终用户借助 GhostField 获得平滑一致游戏体验所依赖的三个子系统
        /// <list type="bullet">
        /// <item>客户端预测</item>
        /// <item>客户端插值 Buffer 与窗口</item>
        /// <item>通过 SmoothingAction 启用的客户端外推</item>
        /// </list>
        /// 测试使用最简单的形式，即客户端严格以 60 Hz Tick 且数值按固定 <c>dt</c> 变化
        /// 并覆盖 <see cref="NetcodeSetupMode"/> 列出的多种场景
        /// 同时验证 <see cref="GhostFieldAttribute.Smoothing"/> 与 <see cref="GhostFieldAttribute.MaxSmoothingDistance"/> 的正确性
        /// </summary>
        /// <remarks>
        /// 后续可增加以下覆盖以强化测试
        /// <list type="bullet">
        /// <item>启用与禁用预测平滑</item>
        /// <item>用于客户端预测的部分 Snapshot</item>
        /// <item>物理交互，尤其是客户端预测中的交互</item>
        /// <item>加入加速、传送和大幅方向变化</item>
        /// <item>不同 Tick Rate，例如 30 Hz、90 Hz 和可变频率</item>
        /// <item>当前测试使用部分 Tick，需比较强制完整 Tick 时能否获得相同平滑度</item>
        /// </list>
        /// </remarks>
        /// <param name="mode"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        [Test]
        public void NetcodeProducesSmoothValues([Values]NetcodeSetupMode mode)
        {
            // 初始化测试
            TMode = mode;
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(MoveExtrapolated), typeof(CheckExtrapolate));

            var optimizationModes = (GhostOptimizationMode[])Enum.GetValues(typeof(GhostOptimizationMode));
            var ghostModes = (GhostMode[])Enum.GetValues(typeof(GhostMode));
            var authoringGhostPrefabs = new List<GameObject>(32);
            foreach (var optimizationMode in optimizationModes)
            {
                foreach (var ghostMode in ghostModes)
                {
                    var ghostGameObject = new GameObject($"Ghost_{optimizationMode}_{ghostMode}");
                    authoringGhostPrefabs.Add(ghostGameObject);
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostExtrapolationConverter
                    {
                        TestExtrapolated = new TestExtrapolated
                        {
                            GhostMode = ghostMode, OptimizationMode = optimizationMode,
                        },
                    };
                    var ghostAuthoringComponent = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                    ghostAuthoringComponent.DefaultGhostMode = ghostMode;
                    ghostAuthoringComponent.SupportedGhostModes = GhostModeMask.All;
                    ghostAuthoringComponent.OptimizationMode = optimizationMode;
                    ghostAuthoringComponent.HasOwner = true;
                    ghostAuthoringComponent.MaxSendRate = 2; // 使用较低发送频率，确保触发插值与外推
                                                             // 不要通过 NetworkTickRate 达到该效果，因为它会强制插值窗口至少为 1 个 Network Tick
                }
            }
            Assert.IsTrue(testWorld.CreateGhostCollection(authoringGhostPrefabs.ToArray()));
            testWorld.CreateWorlds(true, 1);

            // 禁止 Tick 合并
            var tickRate = new ClientServerTickRate {MaxSimulationStepBatchSize = 1, MaxSimulationStepsPerFrame = 1};
            tickRate.ResolveDefaults();
            testWorld.ServerWorld.EntityManager.CreateSingleton(tickRate);

            // 按测试模式配置插值与外推窗口
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            var (interpMs, extrapMs) = mode switch
            {
                NetcodeSetupMode.OnlyInterpolate100ms => (100u, 0u),
                NetcodeSetupMode.SmallestInterpolationWindowAndExtrapolate100ms => (0u, 100u),
                NetcodeSetupMode.Interpolate50msThenExtrapolate50ms => (50u, 50u),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            clientTickRate.InterpolationTimeNetTicks = 0;
            clientTickRate.InterpolationTimeMS = interpMs;
            clientTickRate.MaxExtrapolationTimeSimTicks = (uint) (extrapMs / 1000f * tickRate.SimulationTickRate);
            testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

            // Spawn 并设置 Owner，以覆盖 Owner Predicted
            var serverEntitites = new FixedList4096Bytes<Entity>();
            foreach (var ghostPrefab in authoringGhostPrefabs)
            {
                var serverEnt = testWorld.SpawnOnServer(ghostPrefab);
                serverEntitites.Add(serverEnt);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{ NetworkId = 1, });
            }

            // 先运行一段时间以越过启动期波动，因为测试关注稳定连接下的行为
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 256; ++i)
                testWorld.Tick();

            // 在正式测试开始前重置数值
            using var clientEntityQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(TestExtrapolated));
            using var clientEntities = clientEntityQuery.ToEntityArray(Allocator.Persistent);
            foreach (var serverEntity in serverEntitites)
                ResetComp(testWorld.ServerWorld.EntityManager, serverEntity);
            TestLog = new Dictionary<Entity, string>(clientEntities.Length);
            foreach (var clientEntity in clientEntities)
                TestLog.Add(clientEntity, "");
            static void ResetComp(EntityManager em, Entity entity)
            {
                var comp = em.GetComponentData<TestExtrapolated>(entity);
                em.SetComponentData(entity, new TestExtrapolated
                {
                    GhostMode = comp.GhostMode,
                    OptimizationMode = comp.OptimizationMode,
                });
            }
            for (int i = 0; i < 128; ++i)
                testWorld.Tick();

            // 通过添加组件启用查询检查
            Assert.AreEqual(serverEntitites.Length, clientEntities.Length, "Sanity");
            CheckExtrapolate.NumStepsTested = default;
            foreach (var clientEntity in clientEntities)
                testWorld.ClientWorlds[0].EntityManager.AddComponent<ExtrapolateBackup>(clientEntity);

            // 在指定数量的 Tick 内运行测试
            for (int i = 0; i < 200; ++i)
                testWorld.Tick();

            Assert.IsTrue(CheckExtrapolate.NumStepsTested > 180, $"We need to make sure the test has actually run! {CheckExtrapolate.NumStepsTested}");

            // 截至 2025 年 1 月，低发送频率会导致 Snapshot 到达时偶发平滑问题，Interpolated Ghost 除外
            // 因此允许一定比例的错误
            foreach (var clientEntity in clientEntities)
            {
                var current = testWorld.ClientWorlds[0].EntityManager.GetComponentData<TestExtrapolated>(clientEntity);
                //var backup = testWorld.ClientWorlds[0].EntityManager.GetComponentData<ExtrapolateBackup>(clientEntity);
                LogEach(ref current, TestLog[clientEntity]);
            }

            void LogEach(ref TestExtrapolated current, string logFs)
            {
                Assert.That(logFs.Length > 1000, "Sanity");
                var title = $"({mode},{current.OptimizationMode},{current.GhostMode}) - InterpolationBufferWindow:{clientTickRate.CalculateInterpolationBufferTimeInMs(in tickRate)}ms, ExtrapolationBufferWindow:{clientTickRate.MaxExtrapolationTimeSimTicks}ticks!";
                var log = $"{title} {logFs}";
                var foundErrors = new Regex(Regex.Escape("EXPECTED")).Matches(log).Count;
                var foundFatal = new Regex(Regex.Escape("FATAL")).Matches(log).Count;
                if (foundErrors > 0 || foundFatal > 0)
                {
                    // 错误过多时测试失败
                    if (foundErrors > 0 || foundFatal > 0)
                    {
                        var error = $"FAIL: Found {foundErrors} errors ({foundFatal} fatal) with (stepTolerance:{k_StepTolerance:0.000}) on {log}";
                        Debug.LogError(error);
                        return;
                    }
                }
                Debug.Log($"PASS: Found {foundErrors} errors with (stepTolerance:{k_StepTolerance:0.000}) on {log}");
            }
        }

        internal struct ResultGroup
        {
            public Result ExpectedRIaE;
            public Result ExpectedRInterp;
            public Result ExpectedRIaEWithMaxSmoothingDistance;
            public Result ExpectedRClamp;
            public Result ExpectedPIaE;
            public Result ExpectedPInterp;
            public Result ExpectedPClamp;
        }
        public static ResultGroup GetExpectedResults(in TestExtrapolated current)
        {
            // 细节 1：插值会在下一个 Clamp 值之前，对 (SNAPSHOT-N) 到 SNAPSHOT 的 Tick 平滑数值，并包含 Snapshot 到达的 Tick
            // 细节 2：外推会在 Snapshot 到达之后，对 SNAPSHOT 到 (SNAPSHOT+N) 的 Tick 平滑数值，并包含 Snapshot 到达的 Tick
            // 细节 3：处于外推模式时，SmoothingAction.Interpolate 与 SmoothingAction.InterpolateAndExtrapolate 在第 0 个和最后一个 Tick，也就是下一 Snapshot 到达前，仍会出现约 2 个 Tick 的插值，这是正确且符合预期的
            // 细节 4：Static Optimization 会禁用外推
            // 细节 5：50 ms 插值加 50 ms 外推会产生不同于前述模式的 Smooth 与 Clamp 节奏，此列用于 Dynamic Ghost
            // 细节 6：与细节 5 相同，但用于 Static Optimization Ghost
            Span<(Result n1, Result n2, Result n3, Result n4, Result n5, Result n6)> nuances = stackalloc (Result, Result, Result, Result, Result, Result)[]
            {
                // 细节 1           细节 2          细节 3          细节 4          细节 5          细节 6
                (Result.Smooth,     Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth), // 0
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Smooth,  Result.Clamp), // 3 - 外推在 50 ms 结束
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Smooth,  Result.Clamp,   Result.Clamp,   Result.Clamp), // 6 - 外推在 100 ms 结束
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 10
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 20
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Clamp,      Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp), // 24 - 插值从 100 ms 开始
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Clamp),
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Smooth,  Result.Smooth), // 27 - 插值从 50 ms 开始
                (Result.Smooth,     Result.Clamp,   Result.Clamp,   Result.Clamp,   Result.Smooth,  Result.Smooth),
                (Result.Smooth,     Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth,  Result.Smooth), // 29
            };
            switch (TMode, current.OptimizationMode, current.GhostMode)
            {
                case (_, GhostOptimizationMode.Dynamic or GhostOptimizationMode.Static, GhostMode.Predicted or GhostMode.OwnerPredicted):
                    return new ResultGroup
                    {
                        ExpectedRIaE = Result.Clamp,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = Result.Clamp,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = Result.Smooth,
                        ExpectedPInterp = Result.Smooth,
                        ExpectedPClamp = Result.Smooth,
                    };
                case (NetcodeSetupMode.OnlyInterpolate100ms, _, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n1 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (NetcodeSetupMode.Interpolate50msThenExtrapolate50ms, GhostOptimizationMode.Dynamic, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n5 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n5 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (NetcodeSetupMode.Interpolate50msThenExtrapolate50ms, GhostOptimizationMode.Static, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n6 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (_, GhostOptimizationMode.Dynamic, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n3 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n3 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                case (_, GhostOptimizationMode.Static, GhostMode.Interpolated):
                    return new ResultGroup
                    {
                        ExpectedRIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n4 : Result.Any,
                        ExpectedRIaEWithMaxSmoothingDistance = Result.Clamp,
                        ExpectedRInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedRClamp = Result.Clamp,
                        ExpectedPIaE = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n4 : Result.Any,
                        ExpectedPInterp = current.TicksSinceClampedValueChanged.HasValue ? nuances[current.TicksSinceClampedValueChanged.Value].n2 : Result.Any,
                        ExpectedPClamp = Result.Clamp,
                    };
                default: return default;
            }
        }
    }
}
