using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.Tests;
using UnityEngine;
using UnityEngine.TestTools;
using static Unity.NetCode.ClientServerTickRate.FrameRateMode;

namespace Tests.Editor
{
    [DisableAutoCreation]
    internal abstract partial class BaseCallbackSystem : SystemBase
    {
        public delegate void OnUpdateDelegate(World world);
        public OnUpdateDelegate OnUpdateCallback;

        protected override void OnUpdate()
        {
            OnUpdateCallback?.Invoke(this.World);
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    internal partial class BeforePredictionSystem : BaseCallbackSystem
    {
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    internal partial class AfterPredictionSystem : BaseCallbackSystem
    {

    }

    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    internal partial class UpdateInPredictionSystem : BaseCallbackSystem
    {

    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(UpdateNetworkTimeSystem))]
    internal partial class BeforeSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    internal partial class AfterSimulationSystemGroup : BaseCallbackSystem
    {

    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class RateManagerTests
    {
        [Test]
        public void TestElapsedTimeNonNegativeAtStart()
        {
            const float tickDt = 1f / 60f;
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true, initialElapsedTime: 0);
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = ClientServerTickRate.FrameRateMode.BusyWait;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = 4;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = 4;

            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: true, numClients: 1, tickWorldAfterCreation: false);

            bool didRun = false;
            testWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += world =>
            {
                didRun = true;
                Assert.That(world.Time.ElapsedTime, Is.GreaterThanOrEqualTo(0), "time should always be positive");
            };
            testWorld.Tick(tickDt * 4f); // 使用会执行多个 Tick 的较大 dt，检查每个 Tick 的 ElapsedTime 都非负
            Assert.IsTrue(didRun, "didRun");
        }

        [Test]
        public void RateManagerTest([Values(BusyWait, Sleep)] ClientServerTickRate.FrameRateMode frameRateMode, [Values] NetCodeConfig.HostWorldMode hostMode, [Values(1, 4)] int maxBatchSize, [Values(1, 4)] int maxStepsPerFrame)
        {
            // 初始化测试
            if (hostMode == NetCodeConfig.HostWorldMode.SingleWorld && frameRateMode == Sleep)
            {
                Assert.Ignore("Not implemented for now, ignoring");
            }

            var useSingleWorld = hostMode == NetCodeConfig.HostWorldMode.SingleWorld;
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = frameRateMode;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = maxBatchSize;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = maxStepsPerFrame;
            testWorld.Bootstrap(includeNetCodeSystems: true,
                typeof(BeforePredictionSystem),
                typeof(AfterPredictionSystem),
                typeof(UpdateInPredictionSystem)
            );

            testWorld.CreateWorlds(server: !useSingleWorld, numClients: useSingleWorld ? 0 : 1, numHostWorlds: useSingleWorld ? 1 : 0);
            testWorld.Connect(enableGhostReplication: true);

            int beforePredictionCount = 0;
            int duringPredictionCount = 0;
            int afterPredictionCount = 0;
            int initializationCount = 0;

            // 通过回调记录各执行阶段的数据
            TimeData beforeTime = default;
            NetworkTime beforeNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforePredictionSystem>().OnUpdateCallback += world =>
            {
                beforePredictionCount++;
                beforeTime = world.Time;
                beforeNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, before prediction");
            };
            TimeData duringTime = default;
            NetworkTime duringNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<UpdateInPredictionSystem>().OnUpdateCallback += world =>
            {
                duringPredictionCount++;
                duringTime = world.Time;
                duringNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.True, "network time flag fail, during prediction");
            };
            TimeData afterTime = default;
            NetworkTime afterNetTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<AfterPredictionSystem>().OnUpdateCallback += world =>
            {
                afterPredictionCount++;
                afterTime = world.Time;
                afterNetTime = testWorld.GetNetworkTime(world);
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, after prediction");
            };
            TimeData initializationTime = default;
            testWorld.ServerWorld.GetExistingSystemManaged<BeforeSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                initializationCount++;
                initializationTime = world.Time;
                Assert.That(testWorld.GetNetworkTime(world).IsInPredictionLoop, Is.Not.True, "network time flag fail, in initialization group");
            };

            // 每帧为 1/4 Tick，预期帧与 Tick 比例为 4:1，即连续 3 帧不执行 Tick，第 4 帧执行一次并循环
            const float kTickDt = 1f / 60f;
            var frameCountPerTick = 4;
            if (frameRateMode == Sleep)
            {
                // DGS 上的 Rate Manager 会自动调整 Application.targetFrameRate，使帧率与 Tick Rate 变为 1:1
                // 此处模拟该行为
                frameCountPerTick = 1;
            }

            var frameDt = 1f / frameCountPerTick * kTickDt;

            int frameCount = 8;
            float expectedTick = 2;

            void ResetTime()
            {
                beforeTime = default;
                afterTime = default;
                duringTime = default;
                initializationTime = default;
                beforeNetTime = default;
                afterNetTime = default;
                duringNetTime = default;
            }

            // 使用较小 Frame DT 测试，Tick 应在适当时跳过帧
            {
                if (useSingleWorld)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        testWorld.Tick(frameDt);
                        Assert.That(beforeTime.DeltaTime, Is.EqualTo(frameDt), $"beforeTime, iteration {i}. We need deltaTime to be the frame time so things like interpolation can run with the appropriate deltaTimes");
                        Assert.That(afterTime.DeltaTime, Is.EqualTo(frameDt), $"afterTime, iteration {i}. We need deltaTime to be the frame time so things like interpolation can run with the appropriate deltaTimes");
                        var expectedTickCountSum = math.floor((i + 1f) / frameCountPerTick);
                        var tickCountThisFrame = (i + 1f) % frameCountPerTick == 0 ? 1 : 0;
                        Assert.That(duringPredictionCount, Is.EqualTo(expectedTickCountSum), $"wrong tick count for duringCount, iteration {i}");
                        Assert.That(beforePredictionCount, Is.EqualTo(i + 1), $"wrong tick count for beforeCount, iteration {i}");
                        Assert.That(afterPredictionCount, Is.EqualTo(i + 1), $"wrong tick count for afterCount, iteration {i}");
                        Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        if (tickCountThisFrame > 0) // 未执行 Prediction Tick 时，该测试值没有定义
                            Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(tickCountThisFrame));
                        Assert.That(beforeNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        if (tickCountThisFrame > 0) // 未执行 Prediction Tick 时，该测试值没有定义
                            Assert.That(duringNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        Assert.That(afterNetTime.IsOffFrame, Is.EqualTo(tickCountThisFrame == 0));
                        if (i % frameCountPerTick == frameCountPerTick - 1)
                        {
                            // 每组最后一次循环，例如 Frame 0、1、2、3 在 i == 3 时执行 Tick，Frame 4、5、6、7 在 i == 7 时执行 Tick
                            Assert.That(duringTime.DeltaTime, Is.EqualTo(kTickDt), $"duringTime, iteration {i}");
                            Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the prediction group");
                            Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(afterTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the prediction group");
                            Assert.That(duringTime.ElapsedTime, Is.GreaterThan(0));
                        }
                        else
                        {
                            Assert.That(duringTime.DeltaTime, Is.EqualTo(0), $"duringTime should be 0 outside of ticks, iteration {i}");
                        }

                        ResetTime(); // 避免影响下一次循环的结果
                    }
                }
                else // 双 World
                {
                    void ValidateZeroCountAndDT()
                    {
                        Assert.That(beforePredictionCount, Is.EqualTo(0), $"small dt, beforeCount, validating nothing ran");
                        Assert.That(afterPredictionCount, Is.EqualTo(0), $"small dt, afterCount, validating nothing ran");
                        Assert.That(duringPredictionCount, Is.EqualTo(0), $"small dt, duringCount, validating nothing ran");
                        Assert.That(beforeTime.DeltaTime, Is.EqualTo(0), $"beforeTime nothing, server, validating nothing ran");
                        Assert.That(afterTime.DeltaTime, Is.EqualTo(0), $"afterTime nothing, server, validating nothing ran");
                        Assert.That(duringTime.DeltaTime, Is.EqualTo(0), $"duringTime nothing, server, validating nothing ran");
                        Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                        Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                        Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(0));
                        ResetTime();
                    }

                    // Sleep 模式会自动调整 Application.targetFrameRate，并跳过中间帧
                    // BusyWait 模式在没有 Tick 可执行时跳过整个 SimulationSystemGroup，以保持当前行为
                    if (frameRateMode == BusyWait)
                    {
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                        testWorld.Tick(frameDt);
                        ValidateZeroCountAndDT();
                    }

                    // frameDt 会随 BusyWait 或 Sleep 模式变化，Sleep 模式还会调整 Application.targetFrameRate，因此此处模拟对应 frameDt
                    testWorld.Tick(frameDt);
                    Assert.That(beforeTime.DeltaTime, Is.EqualTo(kTickDt), $"beforeTime, server");
                    Assert.That(afterTime.DeltaTime, Is.EqualTo(kTickDt), $"afterTime, server");
                    Assert.That(duringTime.DeltaTime, Is.EqualTo(kTickDt), $"duringTime, server");
                    Assert.That(initializationTime.DeltaTime, Is.EqualTo(frameDt), $"initialization group dt, validating everything is normal");
                    Assert.That(duringTime.ElapsedTime, Is.LessThanOrEqualTo(initializationTime.ElapsedTime), "elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                    Assert.That(duringTime.ElapsedTime, Is.GreaterThan(0));
                    Assert.That(beforeNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    Assert.That(duringNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    Assert.That(afterNetTime.NumPredictedTicksExpected, Is.EqualTo(1));
                    ResetTime();
                    testWorld.TickMultiple(frameCountPerTick, frameDt);
                }

                Assert.That(beforePredictionCount, Is.EqualTo(useSingleWorld ? frameCount : expectedTick), "small dt, beforeCount");
                Assert.That(afterPredictionCount, Is.EqualTo(useSingleWorld ? frameCount : expectedTick), "small dt, afterCount");
                Assert.That(duringPredictionCount, Is.EqualTo(expectedTick), "small dt, duringCount");

                beforePredictionCount = 0;
                afterPredictionCount = 0;
                duringPredictionCount = 0;

                ResetTime();
            }

            // 使用较大 Frame DT 测试，必要时应合并 Tick 或执行多个 Tick
            {
                // 若最大执行步数为 4 且最大批大小为 1，使用 2 倍 dt 时应执行 2 个步骤
                testWorld.Tick(2f * kTickDt);
                int expectedPredictionCount = maxStepsPerFrame > 1 ? 2 : 1;
                float expectedTickDt = kTickDt;
                if (maxBatchSize > 1 && maxStepsPerFrame == 1)

                    // 条件允许时 NetCode 优先执行更多 Tick，因为比合并 Tick 更准确；只有最大步数不足且允许更大批次时才会合并 Tick
                    expectedTickDt = 2f * kTickDt;
                var expectedFrameDt = 2f * kTickDt; // 同时存在帧时间与 Tick 时间，Simulation Group 位于帧层级，Prediction Group 位于 Tick 层级
                var expectedFrameCount = 1f;

                if (!useSingleWorld)
                {
                    // TODO 2.0 在修复 DGS 的 Tick 合并行为前暂时使用该方案
                    // DGS 在 Simulation Group 层级压入帧时间，Host 则在 Prediction Group 层级压入帧时间
                    // 这是为了避免破坏性变更，但应考虑在 N4E 2.0 修复
                    // 两者的批次数量行为应一致，只有 PushTime 不同；现有测试未失败，可能只是因为对 Tick 合并覆盖不足
                    expectedFrameDt = expectedTickDt;
                }

                Assert.That(beforePredictionCount, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedPredictionCount : expectedFrameCount), "big dt, beforeCount");
                Assert.That(duringPredictionCount, Is.EqualTo(expectedPredictionCount), "big dt, duringCount, expecting multiple prediction iterations");
                Assert.That(afterPredictionCount, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedPredictionCount : expectedFrameCount), "big dt, afterCount");
                Assert.That(beforeTime.DeltaTime, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedTickDt : expectedFrameDt), "batched dt, before");
                Assert.That(duringTime.DeltaTime, Is.EqualTo(expectedTickDt), "batched dt, during");
                Assert.That(afterTime.DeltaTime, Is.EqualTo(hostMode == NetCodeConfig.HostWorldMode.BinaryWorlds ? expectedTickDt : expectedFrameDt), "batched dt, after");
                ResetTime();
            }

            // 推进到稳定状态
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick(kTickDt);
            }

            var epsillon = 0.0001f;

            // 确保帧率随时间变化时 ElapsedTime 仍能正确更新
            {
                if (maxBatchSize == 1 && maxStepsPerFrame == 1)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    // 两个最大值都为 1 时较难追赶，验证恢复较小 Frame DT 后仍能追上
                    var bigDt = 2 * kTickDt;
                    var smallDt = 0.5f * kTickDt;

                    // 让模拟落后
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(bigDt);
                    }

                    // 让模拟追赶
                    for (int i = 0; i < 200; i++)
                    {
                        testWorld.Tick(smallDt);
                    }

                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                }
                else
                {
                    // 验证连续多个 Tick 后不会产生时间偏离
                    var batchDt = 3f * kTickDt; // 小于 maxCount = 4 的配置，因此不应落后
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(batchDt);
                        Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - batchDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group, iteration {i}");
                        ResetTime();
                    }

                    // 让模拟落后
                    batchDt = 6 * kTickDt; // 大于 maxCount = 4 的配置，因此会落后
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(batchDt);
                        Assert.That(duringTime.ElapsedTime, Is.LessThan(initializationTime.ElapsedTime));
                    }

                    // 确保模拟能够追赶
                    for (int i = 0; i < 100; i++)
                    {
                        testWorld.Tick(kTickDt);
                    }

                    Assert.That(duringTime.ElapsedTime, Is.InRange(initializationTime.ElapsedTime - kTickDt - epsillon, initializationTime.ElapsedTime), $"elapsed time, prediction should always follow, but be behind elapsed time outside the simulation group");
                }
            }
        }

        [Test]
        public void TestCanDetectIfServerWillUpdate([Values(Sleep, BusyWait)] ClientServerTickRate.FrameRateMode mode, [Values] bool singleWorldHost)
        {
            if (mode == Sleep && singleWorldHost) Assert.Ignore("TODO-release not supported right now");
            using var testWorld = new NetCodeTestWorld(useGlobalConfig: true);
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepBatchSize = 1;
            NetCodeConfig.Global.ClientServerTickRate.MaxSimulationStepsPerFrame = 1; // 虽然默认值已为 1，仍显式设置以确保测试前提不被破坏
            NetCodeConfig.Global.ClientServerTickRate.TargetFrameRateMode = mode;

            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BeforeSimulationSystemGroup), typeof(AfterSimulationSystemGroup));
            testWorld.CreateWorlds(server: !singleWorldHost, numHostWorlds: singleWorldHost ? 1 : 0, numClients: 1);
            testWorld.Connect();
            testWorld.GoInGame();

            // bool willUpdate = false;
            bool expectedWillUpdate = false;

            void ValidateWillUpdate(bool isBefore)
            {
                // 检查服务端 Simulation Group 是否即将运行
                var networkTime = testWorld.GetSingleton<NetworkTime>(testWorld.ServerWorld);
                bool willUpdate;
                if (singleWorldHost)
                {
                    var hostRateManager = testWorld.ServerWorld.GetExistingSystemManaged<SimulationSystemGroup>().RateManager as NetcodeHostRateManager;
                    willUpdate = hostRateManager.WillUpdateInternal();
                }
                else
                {
                    var serverRateManager = testWorld.ServerWorld.GetExistingSystemManaged<SimulationSystemGroup>().RateManager as NetcodeServerRateManager;
#pragma warning disable CS0618 // 类型或成员已过时
                    willUpdate = serverRateManager.WillUpdate();
#pragma warning restore CS0618 // 类型或成员已过时
                }
                Assert.AreEqual(expectedWillUpdate, isBefore ? willUpdate : !willUpdate);
                Assert.AreEqual(expectedWillUpdate, !networkTime.IsOffFrame);
            }
            testWorld.ServerWorld.GetExistingSystemManaged<BeforeSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                ValidateWillUpdate(isBefore: true);
            };
            testWorld.ServerWorld.GetExistingSystemManaged<AfterSimulationSystemGroup>().OnUpdateCallback += world =>
            {
                ValidateWillUpdate(isBefore: false);
            };
            if (mode == Sleep)
            {
                expectedWillUpdate = true;
                testWorld.Tick();
                LogAssert.Expect(LogType.Warning, "Testing if will update when TargetFrameRateMode is set to Sleep. This will always return true.");
            }
            else
            {
                // 当前处于 BusyWait 模式，因此应每两帧跳过一帧
                var halfDt = 0.5f / 60f;
                expectedWillUpdate = false;
                testWorld.Tick(halfDt);
                expectedWillUpdate = true;
                testWorld.Tick(halfDt);
                expectedWillUpdate = false;
                testWorld.Tick(halfDt);
                expectedWillUpdate = true;
                testWorld.Tick(halfDt);
            }
        }
    }
}
