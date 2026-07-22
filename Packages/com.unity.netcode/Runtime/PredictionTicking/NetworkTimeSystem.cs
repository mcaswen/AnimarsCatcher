using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Burst;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
#if UNITY_EDITOR || NETCODE_DEBUG
    internal struct NetworkTimeSystemStats : IComponentData
    {
        public float timeScale;
        public float interpTimeScale;
        private float averageTimeScale;
        private float averageInterpTimeScale;
        public float currentInterpolationFrames;
        public int timeScaleSamples;
        public int interpTimeScaleSamples;

        public void UpdateStats(float predictionTimeScale, float interpolationTimeScale, float interpolationFrames)
        {
            timeScale += predictionTimeScale;
            ++timeScaleSamples;
            interpTimeScale += interpolationTimeScale;
            ++interpTimeScaleSamples;
            currentInterpolationFrames = interpolationFrames;
        }

        public float GetAverageTimeScale()
        {
            if (timeScaleSamples > 0)
            {
                averageTimeScale = timeScale / timeScaleSamples;
                timeScale = 0;
                timeScaleSamples = 0;
            }

            return averageTimeScale;
        }

        public float GetAverageIterpTimeScale()
        {
            if (interpTimeScaleSamples > 0)
            {
                averageInterpTimeScale = interpTimeScale / interpTimeScaleSamples;
                interpTimeScale = 0;
                interpTimeScaleSamples = 0;
            }
            return averageInterpTimeScale;
        }
    }
#endif

    /// <summary>
    /// 存储 NetworkTimeSystem 的内部状态
    /// 此组件只应用于检查或备份数据，请勿直接修改状态值
    /// </summary>
    public struct NetworkTimeSystemData : IComponentData
    {
        /// <summary>
        /// 计算得到的插值 Tick，用于显示插值 Ghost
        /// </summary>
        public NetworkTick interpolateTargetTick;
        /// <summary>
        /// interpolateTargetTick 的剩余 Tick 小数部分
        /// </summary>
        public float subInterpolateTargetTick;
        /// <summary>
        /// 预计服务器收到客户端 Command 时所处的 Tick
        /// </summary>
        public NetworkTick predictTargetTick;
        /// <summary>
        /// 客户端预测本地输入前，有意忽略这些输入的 Tick 数量
        /// </summary>
        public uint effectiveForcedInputLatencyTicks;
        /// <summary>
        /// predictTargetTick 的剩余 Tick 小数部分
        /// </summary>
        public float subPredictTargetTick;
        /// <summary>
        /// 当前插值延迟 Tick 数，用于将最近估计的服务器 Tick 向过去偏移
        /// </summary>
        public float currentInterpolationFrames;
        /// <summary>
        /// 从服务器收到的最新 Snapshot Tick，用于计算 Snapshot 之间的 Tick 差值
        /// </summary>
        public NetworkTick latestSnapshot;
        /// <summary>
        /// 内部估计的下一个应从服务器收到的 Tick
        /// PredictedTick 和 InterpolatedTick 都由此推算
        /// </summary>
        public NetworkTick latestSnapshotEstimate;
        /// <summary>
        /// 估计 Tick 与服务器实际发来 Snapshot Tick 之间差值的定点指数平均值
        /// 用于调整 <see cref="latestSnapshotEstimate"/>
        /// </summary>
        public int latestSnapshotAge;
        /// <summary>
        /// Snapshot 之间 Tick 差值的平均值，也是当前感知到的 SimulationTickRate/SnapshotTickRate 估计值
        /// 例如服务器以 30Hz 发送而模拟频率为 60Hz 时，平均比值应为 2
        /// </summary>
        public float avgDeltaSimTicks;
        /// <summary>
        /// 感知 NetTickRate 的标准差或抖动，实际为近似值
        /// </summary>
        public float devDeltaSimTicks;
        /// <summary>
        /// 收到上一数据包时的本地时间戳，用于计算感知到的数据包到达频率
        /// </summary>
        public uint lastTimeStamp;
        /// <summary>
        /// 数据包到达间隔的指数平均值
        /// </summary>
        public float avgPacketInterArrival;

        /// <summary>
        /// 收到服务器第一份 Snapshot 数据时初始化内部状态
        /// </summary>
        /// <param name="snapshot">从服务器收到的 Snapshot Tick</param>
        /// <param name="currentTs">当前本地时间戳，单位为毫秒</param>
        /// <param name="commandSlack">目标命令裕量 <see cref="ClientTickRate.TargetCommandSlack"/></param>
        /// <param name="predictAheadByTicks">客户端应提前预测的 Tick 数量，可以为负值</param>
        /// <param name="devRtt">当前计算得到的往返抖动</param>
        /// <param name="interpolationDelay">期望的插值延迟，单位为 Tick</param>
        /// <param name="simTickRate">模拟 Tick 率 <see cref="ClientServerTickRate.SimulationTickRate"/></param>
        /// <param name="netTickRate">以模拟 Tick 表示的数据包到达间隔</param>
        internal void InitWithFirstSnapshot(NetworkTick snapshot, uint currentTs, uint commandSlack,
            int predictAheadByTicks, float devRtt, float interpolationDelay, int simTickRate, int netTickRate)
        {
            latestSnapshot = snapshot;
            latestSnapshotEstimate = snapshot;
            latestSnapshotAge = 0;
            predictTargetTick = snapshot;
            if(predictAheadByTicks >= 0)
                predictTargetTick.Add((uint) predictAheadByTicks);
            else predictTargetTick.Subtract((uint) -predictAheadByTicks);

            // 插值帧的初始估计值，使用 DeviationRTT 衡量 Snapshot 频率的抖动
            avgDeltaSimTicks = netTickRate;
            devDeltaSimTicks = (devRtt * netTickRate / 1000f);
            avgPacketInterArrival = ((float)1000)/(netTickRate*simTickRate);
            // 插值延迟，即期望落后的 Tick 数，需要乘以 NetworkRate 与 SimTickRate 的频率比
            // 例如服务器以 20Hz 发送而模拟频率为 60Hz 时，两份 Snapshot 之间相隔 3 个模拟 Tick
            // 因此若希望落后 3 份 Snapshot，约等于落后最近收到的 Snapshot 9 个模拟 Tick
            currentInterpolationFrames = interpolationDelay*netTickRate + 2f*devDeltaSimTicks;
            interpolateTargetTick = snapshot;
            interpolateTargetTick.Subtract((uint)currentInterpolationFrames);
            subPredictTargetTick = 0f;
            lastTimeStamp = currentTs;
        }

        /// <summary>
        /// 从服务器收到新的 Snapshot 数据时更新内部状态
        /// </summary>
        internal void UpdateWithLastSnapshot(uint currentTimeTs, NetworkTick snapshotTick)
        {
            int snapshotAge = latestSnapshotEstimate.TicksSince(snapshotTick);
            int snapshotDeltaSimTicks = snapshotTick.TicksSince(latestSnapshot);
            float deltaTimestamp = currentTimeTs - lastTimeStamp;
            lastTimeStamp = currentTimeTs;
            latestSnapshotAge = (latestSnapshotAge * 7 + (snapshotAge << 8)) / 8;
            latestSnapshot = snapshotTick;
            // 感知 Tick Rate 的移动平均值应比 Snapshot Age 更快响应变化
            // 这样可以避免服务器低帧率运行时，客户端以服务器两倍频率消耗 Snapshot 数据包
            // 此处使用 TCP 规范系数 0.125 的两倍作为调整系数
            // TODO：增加峰值检测，以便按差值变化选择更快或更慢的响应速度
            avgDeltaSimTicks = math.lerp(avgDeltaSimTicks, snapshotDeltaSimTicks, 0.25f);
            devDeltaSimTicks = math.lerp(devDeltaSimTicks, math.abs(snapshotDeltaSimTicks - avgDeltaSimTicks), 0.25f);
            avgPacketInterArrival = math.lerp(avgPacketInterArrival, deltaTimestamp, 0.25f);
        }
    }

    /// <summary>
    /// <para>使用当前往返时间，参见 <see cref="NetworkSnapshotAck"/>，以及服务器反馈，
    /// 参见 <see cref="NetworkSnapshotAck.ServerCommandAge"/>，估算 <see cref="NetworkTime.ServerTick"/>
    /// 和 <see cref="NetworkTime.InterpolationTick"/> 的系统</para>
    /// <para>此系统会尽量让客户端上的 Server Tick 领先真实服务器，
    /// 使输入 Command，参见 <see cref="ICommandData"/> 和 <see cref="IInputComponentData"/>，
    /// 能在服务器进行模拟所需时间<i>之前</i>到达
    /// 系统会加快或减慢客户端模拟经过的 Delta Time，以补偿网络状况变化，
    /// 并使服务器报告的 <see cref="NetworkSnapshotAck.ServerCommandAge"/> 接近 <see cref="ClientTickRate.TargetCommandSlack"/></para>
    /// <para>客户端收到第一份 Snapshot 后立即开始时间同步
    /// 因此在客户端 <see cref="NetworkStreamConnection"/> 进入游戏前，参见 <see cref="NetworkStreamInGame"/>，
    /// 计算得到的服务器 Tick 和插值 Tick 始终为 0</para>
    /// <para>当客户端与服务器 World 位于同一进程并使用 IPC 连接时，参见 <see cref="TransportType.IPC"/>，
    /// 可以应用特殊优化，例如此时客户端应始终每帧运行 1 个 Tick，因为服务器和客户端同步更新</para>
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(UpdateWorldTimeSystem))]
    public partial struct NetworkTimeSystem : ISystem, ISystemStartStop
    {
        /// <summary>
        /// 用于存储部分补偿量的数组长度
        /// </summary>
        private const int CommandAgeAdjustmentLength = 64;
        /// <summary>
        /// 当前 Command Age 调整槽位
        /// </summary>
        private int commandAgeAdjustmentSlot;
        /// <summary>
        /// 已应用到服务器 Tick 预测的部分调整量，用于避免重复补偿服务器的延迟反馈
        /// </summary>
        private FixedList512Bytes<float> commandAgeAdjustment;

        /// <summary>
        /// 使用合理默认值初始化的新 <see cref="ClientTickRate"/> 实例
        /// </summary>
        public static ClientTickRate DefaultClientTickRate => new ClientTickRate
        {
            InterpolationTimeNetTicks = 2,
            MaxExtrapolationTimeSimTicks = 20,
            MaxPredictAheadTimeMS = 500,
            NumAdditionalClientPredictedGhostLifetimeTicks = 0,
            ForcedInputLatencyTicks = 0,
            TargetCommandSlack = 2,
            DefaultClassificationAllowableTickPeriod = 5,
            NumAdditionalCommandsToSend = 2,
            CommandAgeCorrectionFraction = 0.1f,
            PredictionTimeScaleMin = 0.9f,
            PredictionTimeScaleMax = 1.1f,
            InterpolationDelayJitterScale = 1.25f,
            InterpolationDelayMaxDeltaTicksFraction = 0.1f,
            InterpolationDelayCorrectionFraction = 0.1f,
            InterpolationTimeScaleMin = 0.85f,
            InterpolationTimeScaleMax = 1.1f
        };

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void ValidateClientTickRate(in ClientTickRate tickRate, in NetDebug netDebug)
        {
            if(tickRate.MaxPredictAheadTimeMS > 500f)
                netDebug.LogError("MaxPredictAheadTimeMS must be less than 500ms");
            if(tickRate.PredictionTimeScaleMin < 0.01f || tickRate.PredictionTimeScaleMin >= 1.0f)
                netDebug.LogError("PredictionTimeScaleMin must be in range [0.01, 1.0)");
            if(tickRate.PredictionTimeScaleMax < 1f || tickRate.PredictionTimeScaleMax > 2f)
                netDebug.LogError("PredictionTimeScaleMin must be in range (1.00, 2.0]");
            if(tickRate.InterpolationTimeScaleMin < 0.01f || tickRate.InterpolationTimeScaleMin > 1f)
                netDebug.LogError("InterpolationTimeScaleMin must be in range [0.01, 1.0)");
            if(tickRate.InterpolationTimeScaleMax < 0.01f || tickRate.InterpolationTimeScaleMax > 2f)
                netDebug.LogError("InterpolationTimeScaleMax must be in range (1.00, 2.0]");
            if(tickRate.InterpolationDelayJitterScale < 0f || tickRate.InterpolationDelayJitterScale > 3f)
                netDebug.LogError("InterpolationDelayJitterScale must be in range (0, 3]");
            if(tickRate.InterpolationDelayMaxDeltaTicksFraction < 0f || tickRate.InterpolationDelayMaxDeltaTicksFraction > 1f)
                netDebug.LogError("InterpolationDelayMaxDeltaTicksFraction must be in range (0, 1)");
            if(tickRate.InterpolationDelayCorrectionFraction < 0f || tickRate.InterpolationDelayCorrectionFraction > 1f)
                netDebug.LogError("InterpolationDelayCorrectionFraction must be in range (0, 1)");
        }


#if UNITY_EDITOR || NETCODE_DEBUG || UNITY_INCLUDE_TESTS
        internal static uint s_FixedTimestampMS{get{return s_FixedTime.Data.FixedTimestampMS;} set{s_FixedTime.Data.FixedTimestampMS = value;}}
        private struct FixedTime
        {
            public uint FixedTimestampMS;
            internal uint PrevTimestampMS;
            internal uint TimestampAdjustMS;
        }
        private static readonly SharedStatic<FixedTime> s_FixedTime = SharedStatic<FixedTime>.GetOrCreate<FixedTime>();


        /// <summary>
        /// 在禁用 Domain Reload 时清理残留的 FixedTime 值
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetFixedTime()
        {
            s_FixedTime.Data.FixedTimestampMS = default;
            s_FixedTime.Data.PrevTimestampMS = default;
            s_FixedTime.Data.TimestampAdjustMS = default;
        }

        /// <summary>
        /// 返回表示进程启动后经过毫秒数的低精度实时时间戳
        /// 在 Development Build 和 Editor 中，两次调用 TimestampMS 报告的最大差值限制为 100 毫秒
        /// </summary>
        /// <remarks>
        /// TimestampMS 主要用于时间同步，例如计算 RTT
        /// </remarks>
        public static uint TimestampMS
        {
            get
            {
                // 如果设置了固定时间戳，则使用该值
                if (s_FixedTime.Data.FixedTimestampMS != 0)
                    return s_FixedTime.Data.FixedTimestampMS;
                // FIXME：如果 Stopwatch 不是高精度计时器，则它基于精度约为 10ms 的系统计时器
                // 这可能影响时间戳计算的准确性
                var cur = (uint)TimerHelpers.GetCurrentTimestampMS();
                // 距离上次时间戳检查超过 100ms 时增加调整量，使报告的时间差保持为 100ms
                if (s_FixedTime.Data.PrevTimestampMS != 0 && (cur - s_FixedTime.Data.PrevTimestampMS) > 100)
                {
                    s_FixedTime.Data.TimestampAdjustMS += (cur - s_FixedTime.Data.PrevTimestampMS) - 100;
                }
                s_FixedTime.Data.PrevTimestampMS = cur;
                return cur - s_FixedTime.Data.TimestampAdjustMS;
            }
        }
#else
        /// <summary>
        /// 返回表示进程启动后经过毫秒数的低精度实时时间戳
        /// 在 Development Build 和 Editor 中，两次调用 TimestampMS 报告的最大差值限制为 100 毫秒
        /// </summary>
        /// <remarks>
        /// TimestampMS 主要用于时间同步，例如计算 RTT
        /// </remarks>
        public static uint TimestampMS =>
            (uint)TimerHelpers.GetCurrentTimestampMS();
#endif



        /// <summary>
        /// 创建 <see cref="NetworkTimeSystemData"/> Singleton 并重置系统初始状态
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

#if UNITY_EDITOR || NETCODE_DEBUG
            var types = new NativeArray<ComponentType>(2, Allocator.Temp);
            types[0] = ComponentType.ReadWrite<NetworkTimeSystemData>();
            types[1] = ComponentType.ReadWrite<NetworkTimeSystemStats>();
#else
            var types = new NativeArray<ComponentType>(1, Allocator.Temp);
            types[0] = ComponentType.ReadWrite<NetworkTimeSystemData>();
#endif
            var netTimeStatEntity = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(types));
            FixedString64Bytes singletonName = "NetworkTimeSystemData";
            state.EntityManager.SetName(netTimeStatEntity, singletonName);
            state.RequireForUpdate<NetworkSnapshotAck>();
        }

        /// <summary>
        /// 实现 <see cref="ISystem"/> 接口的空方法
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnStartRunning(ref SystemState state)
        {
        }

        /// <summary>
        /// 重置 <see cref="NetworkTimeSystemData"/> 数据和部分内部变量
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnStopRunning(ref SystemState state)
        {
            SystemAPI.SetSingleton(new NetworkTimeSystemData());
        }

        /// <summary>
        /// 在主线程上执行全部时间同步逻辑
        /// </summary>
        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();

            if(!SystemAPI.TryGetSingleton<ClientTickRate>(out var clientTickRate))
                clientTickRate = DefaultClientTickRate;

            ValidateClientTickRate(clientTickRate, SystemAPI.GetSingleton<NetDebug>());

            state.CompleteDependency(); // 需要完成依赖，因为 NetworkSnapshotAck 由 NetworkStreamReceiveSystem 中的 Job 写入

            var ack = SystemAPI.GetSingleton<NetworkSnapshotAck>();
            bool isInGame = SystemAPI.HasSingleton<NetworkStreamInGame>();

            float deltaTime = SystemAPI.Time.DeltaTime;
            if(isInGame && ClientServerBootstrap.HasServerWorld)
            {
                var maxDeltaTicks = (uint)tickRate.MaxSimulationStepsPerFrame * (uint)tickRate.MaxSimulationStepBatchSize;
                if (deltaTime > (float) maxDeltaTicks / (float) tickRate.SimulationTickRate)
                    deltaTime = (float) maxDeltaTicks / (float) tickRate.SimulationTickRate;
            }
            float deltaTicks = deltaTime * tickRate.SimulationTickRate;
            // 客户端通过 IPC 连接同一进程内的服务器时，可以确定
            // 延迟为 0
            // 抖动为 0
            // 没有丢包
            //
            // 这意味着平均或期望 Command Slack 为 0，只预测下一 Tick
            // 理想输出如下
            // predictTargetTick = latestSnapshot + 1
            // interpolationTicks = max(SimulationRate/NetworkTickRate, clientTickRate.InterpolationTimeNetTicks)，也可使用等价的毫秒版本
            // interpolateTargetTick = latestSnapshot - interpolationTicks
            //
            // 但客户端使用可变帧率运行，并未与服务器同步，因此
            // - 会出现部分 Tick
            // - 插值 Tick 会产生少量小数变化
            //
            // 可以强制 InterpolationFrames 保持常量，但当前更倾向于尽可能共享全部代码路径，避免特殊逻辑
            // 后续可进一步优化

            var driverType = SystemAPI.GetSingleton<NetworkStreamDriver>().DriverStore.GetDriverType(NetworkDriverStore.FirstDriverId);
            if (driverType == TransportType.IPC)
            {
                // 覆盖此参数，使预测目标 Tick 等于最近收到的 Snapshot Tick 加 1，即下一服务器 Tick
                // 但由于客户端会发送部分 Tick，需要确保服务器始终收到客户端的下一个部分 Tick
                // 因此 Command Age 应接近 -1，该行为由服务器使用最近完整 Tick 作为依据进行控制
                clientTickRate.TargetCommandSlack = 0;
                // 这些值理论上为 0，此处强制设置
                ack.DeviationRTT = 0f;
                ack.EstimatedRTT = 1000f/tickRate.SimulationTickRate;
            }

            // 计算客户端时间线需要领先服务器时间线多少
            ref var netTimeData = ref SystemAPI.GetSingletonRW<NetworkTimeSystemData>().ValueRW;
            uint rttInTicks = ((uint)(ack.EstimatedRTT * tickRate.SimulationTickRate) + 999) / 1000;
            var inputTargetTicks = rttInTicks + clientTickRate.TargetCommandSlack;
            // 注意：`EstimatedRTT` 达到 `MaxPredictAheadTimeMS` 时会引入强制输入延迟
            uint maxAllowedPredictionTicks = ((uint)(clientTickRate.MaxPredictAheadTimeMS * tickRate.SimulationTickRate) + 999) / 1000;
            uint minForcedInputLatencyTicksFromMaxPredictAheadTime = (uint)math.max(0, (int)inputTargetTicks - (int)maxAllowedPredictionTicks);
            netTimeData.effectiveForcedInputLatencyTicks = math.max(minForcedInputLatencyTicksFromMaxPredictAheadTime, clientTickRate.ForcedInputLatencyTicks);
            int predictAheadByTicks = (int)inputTargetTicks - (int)netTimeData.effectiveForcedInputLatencyTicks; // 可以为负值

            var netTickRateInterval = tickRate.CalculateNetworkSendRateInterval();
            // 期望的插值帧数取决于模拟频率与网络 Tick Rate 的比值
            // 例如服务器以 60Hz 模拟但以 20Hz 发送时，至少需要落后 3 个 Tick，或其任意整数倍
            var interpolationTimeTicks = clientTickRate.CalculateInterpolationBufferTimeInTicks(tickRate);
            // 未进入游戏时重置 latestSnapshotEstimate
#if UNITY_EDITOR || NETCODE_DEBUG
            ref var  netTimeDataStats = ref SystemAPI.GetSingletonRW<NetworkTimeSystemStats>().ValueRW;
#endif
            if (netTimeData.latestSnapshotEstimate.IsValid && !isInGame)
                netTimeData.latestSnapshotEstimate = NetworkTick.Invalid;
            if (!netTimeData.latestSnapshotEstimate.IsValid)
            {
                if (!ack.LastReceivedSnapshotByLocal.IsValid)
                {
                    netTimeData = default(NetworkTimeSystemData);
                    return;
                }
                netTimeData.InitWithFirstSnapshot(ack.LastReceivedSnapshotByLocal, TimestampMS, clientTickRate.TargetCommandSlack,
                    predictAheadByTicks, ack.DeviationRTT, interpolationTimeTicks, tickRate.SimulationTickRate, netTickRateInterval);

                commandAgeAdjustment.Length = CommandAgeAdjustmentLength;
                for (int i = 0; i < CommandAgeAdjustmentLength; ++i)
                    commandAgeAdjustment[i] = 0;

#if UNITY_EDITOR || NETCODE_DEBUG
                netTimeDataStats = default(NetworkTimeSystemStats);
#endif
            }
            else
            {
                // ack.LastReceivedSnapshotByLocal 为 0 表示检测到不同步
                // 此时使用差值更新估计结果完全错误
                if (netTimeData.latestSnapshot != ack.LastReceivedSnapshotByLocal && ack.LastReceivedSnapshotByLocal.IsValid)
                    netTimeData.UpdateWithLastSnapshot(TimestampMS, ack.LastReceivedSnapshotByLocal);

                // 根据 Delta Time 增加 Tick 数量
                // netTimeData.latestSnapshotEstimate.Add((uint) deltaTicks);
                // 原则上 Snapshot Age 通常应为负值，并表示 latestSnapshotEstimate 的小数部分
                // 实际上 UpdateWithLastSnapshot 会用上次计算的 Age 通过指数移动平均更新 `latestSnapshotAge`
                // 因此它同时承担估计补偿量和小数部分的作用
                netTimeData.latestSnapshotAge -= (int)(deltaTicks * 256f);
                int delta = netTimeData.latestSnapshotAge / 256;
                if (delta < 0)
                {
                    netTimeData.latestSnapshotEstimate.Add((uint)-delta);
                    netTimeData.latestSnapshotAge -= delta << 8;
                }
                else if (delta > 0)
                {
                    // 例如
                    // 10（估计值），1.35（Age），delta = 1
                    // 10 - 1.35 = 8.65 => 8（估计值），-0.65（Age）
                    // 此处增加 delta 等同于对小数部分应用正确运算
                    ++delta;
                    netTimeData.latestSnapshotEstimate.Subtract((uint)delta);
                    netTimeData.latestSnapshotAge -= delta << 8;
                }
            }
            float predictionTimeScale = 1f;
            float commandAge = ack.ServerCommandAge / 256.0f + clientTickRate.TargetCommandSlack;
            // 检查当前数据应写入 Command Age 调整环形缓冲区的哪个槽位
            // 使用 latestSnapshot 而不是 LastReceivedSnapshotByLocal，因为后者可能重置为 0，导致错误地重置调整量
            commandAge = AdjustCommandAge(netTimeData.latestSnapshot, commandAge, rttInTicks);
            if (math.abs(commandAge) < 10)
            {
                predictionTimeScale = math.clamp(1.0f + clientTickRate.CommandAgeCorrectionFraction * commandAge, clientTickRate.PredictionTimeScaleMin, clientTickRate.PredictionTimeScaleMax);
                netTimeData.subPredictTargetTick += deltaTicks * predictionTimeScale;
                uint pdiff = (uint) netTimeData.subPredictTargetTick;
                netTimeData.subPredictTargetTick -= pdiff;
                netTimeData.predictTargetTick.Add(pdiff);
            }
            else
            {
                var curPredict = netTimeData.latestSnapshotEstimate;
                if(predictAheadByTicks >= 0)
                    curPredict.Add((uint) predictAheadByTicks);
                else curPredict.Subtract((uint) -predictAheadByTicks);
                float predictDelta = (float)(curPredict.TicksSince(netTimeData.predictTargetTick)) - deltaTicks;
                if (math.abs(predictDelta) > 10)
                {
                    // 注意：当估计差值很大，约超过 10 个 Tick，且 predictDelta 为负，即客户端领先过多时，可能发生回滚
                    if (predictDelta < 0.0f)
                    {
                        SystemAPI.GetSingleton<NetDebug>().LogError($"Large serverTick prediction error encountered! The serverTick rolled back to {curPredict.ToFixedString()} (a delta of {predictDelta} ticks)! Common causes: a) Poor client and / or server performance, b) network instability, c) Application.runInBackground is not correctly set (to true).");
                    }
                    netTimeData.predictTargetTick = curPredict;
                    netTimeData.subPredictTargetTick = 0;
                    for (int i = 0; i < CommandAgeAdjustmentLength; ++i)
                        commandAgeAdjustment[i] = 0;
                }
                else
                {
                    predictionTimeScale = math.clamp(1.0f + clientTickRate.CommandAgeCorrectionFraction * predictDelta, clientTickRate.PredictionTimeScaleMin, clientTickRate.PredictionTimeScaleMax);
                    netTimeData.subPredictTargetTick += deltaTicks * predictionTimeScale;
                    uint pdiff = (uint) netTimeData.subPredictTargetTick;
                    netTimeData.subPredictTargetTick -= pdiff;
                    netTimeData.predictTargetTick.Add(pdiff);
                }
            }

            commandAgeAdjustment[commandAgeAdjustmentSlot] += deltaTicks * (predictionTimeScale - 1.0f);
            // 下一份将收到的 Snapshot 对应哪个帧
            // 当前最佳估计值是 latestSnapshotEstimate，它尝试预测下一份从服务器收到的 Snapshot
            // 插值 Tick 应基于 latestSnapshotEstimate，并向后延迟若干插值帧
            // 使用 latestSnapshotEstimate 而不是预测 Tick 作为插值 Tick 的基准，原因如下
            // - 客户端加速推进预测 Tick 时，不应导致插值 Tick 同样加速
            // - 它能更准确反映最近收到的数据，而不是从还受其他因素影响的预测结果近似目标
            //
            // 插值帧数按以下方式计算
            // frames = E[avgNetTickRate] + K*std[avgNetTickRate]
            // interpolationTick = latestSnapshotEstimate - frames
            //
            // avgNetTickRate：根据收到的 Snapshot 之间的 Tick 差值计算，并考虑以下因素
            //  - 丢包，此时插值延迟应增加
            //  - 服务器网络 Tick Rate 变化，例如服务器运行变慢
            //  - 每帧收到多个数据包，此时插值延迟应增加
            // latestSnapshotEstimate：通过当前估计值与实际接收值之间的差值进行调整，因此可以反映延迟变化
            //
            // latestSnapshotEstimate 与 avgNetTickRate 共同补偿最显著影响插值延迟增减的因素
            var delayChangeLimit = deltaTicks*clientTickRate.InterpolationDelayMaxDeltaTicksFraction;
            var deltaInBetweenSnapshotTicks = netTimeData.avgDeltaSimTicks + netTimeData.devDeltaSimTicks * clientTickRate.InterpolationDelayJitterScale;
            // 以模拟 Tick 表示的感知 Snapshot 到达间隔
            var avgNetRate = (netTimeData.avgPacketInterArrival*tickRate.SimulationTickRate + 999)/1000;
            // 插值帧数量以模拟 Tick 表示，因此需要乘以 netTickRateInterval
            float desiredInterpolationDelayTicks = interpolationTimeTicks*netTickRateInterval;
            // 在平均 Snapshot 到达间隔和平均 Snapshot Tick 差值之间选择较大值
            var clampedDelayTick = math.max(avgNetRate, deltaInBetweenSnapshotTicks);
            // 仍将其限制为期望 netTickRate 的 6 倍，因为可以合理假设服务器会尝试恢复正常
            clampedDelayTick = math.min(clampedDelayTick, 6*netTickRateInterval);
            // 如果配置的 desiredInterpolationDelayTicks 更大，则仍采用配置值
            var interpolationFrames = math.max(desiredInterpolationDelayTicks, clampedDelayTick);

            if (math.abs(interpolationFrames - netTimeData.currentInterpolationFrames) > 10f)
            {
                // 差值很大时立即调整帧延迟
                netTimeData.currentInterpolationFrames = interpolationFrames;
            }
            else
            {
                // 缓慢趋近计算得到的目标帧数
                netTimeData.currentInterpolationFrames += math.clamp(
                    (interpolationFrames-netTimeData.currentInterpolationFrames)*deltaTime,
                    -delayChangeLimit, delayChangeLimit);
            }

            var newInterpolationTargetTick = netTimeData.latestSnapshotEstimate;
            newInterpolationTargetTick.Subtract((uint)netTimeData.currentInterpolationFrames);

            // 使用 Forced Input Latency 时，客户端预测目标 Tick 可能落后于插值目标 Tick
            // 因此在此限制范围，实际效果是动态延长插值窗口
            if (netTimeData.effectiveForcedInputLatencyTicks > 0 && netTimeData.predictTargetTick.TicksSince(newInterpolationTargetTick) < 0)
                newInterpolationTargetTick = netTimeData.predictTargetTick;

            var targetTickDelta = newInterpolationTargetTick.TicksSince(netTimeData.interpolateTargetTick) - netTimeData.subInterpolateTargetTick - deltaTicks;
            float interpolationTimeScale = 1f;
            // 如果当前落后，10 个 Tick 已经很多，使用 10% Delta Time 缩放需要 100 帧才能恢复
            // 此处不检查绝对值，因为差值为负时需要向后移动，只需降低 interpolationTimeScale
            if (targetTickDelta < 10)
            {
                interpolationTimeScale = math.clamp(1.0f + targetTickDelta*clientTickRate.InterpolationDelayCorrectionFraction,
                    clientTickRate.InterpolationTimeScaleMin, clientTickRate.InterpolationTimeScaleMax);

                netTimeData.subInterpolateTargetTick += deltaTicks * interpolationTimeScale;
                uint idiff = (uint) netTimeData.subInterpolateTargetTick;
                netTimeData.interpolateTargetTick.Add(idiff);
                netTimeData.subInterpolateTargetTick -= idiff;
            }
            else
            {
                // 直接跳转，使其与插值 Tick 匹配
                netTimeData.interpolateTargetTick = newInterpolationTargetTick;
                netTimeData.subInterpolateTargetTick = 0f;
            }
#if UNITY_EDITOR || NETCODE_DEBUG
            netTimeDataStats.UpdateStats(predictionTimeScale, interpolationTimeScale, netTimeData.currentInterpolationFrames);
#endif
        }

        /// <summary>
        /// 减去全部预测 Tick 补偿量，计算调整后的 Command Age
        /// 这些补偿量源于服务器反馈延迟
        /// </summary>
        /// <param name="lastSnapshot"></param>
        /// <param name="commandAge"></param>
        /// <param name="rttInTicks"></param>
        /// <returns></returns>
        float AdjustCommandAge(in NetworkTick lastSnapshot, float commandAge, uint rttInTicks)
        {
            int curSlot = (int)(lastSnapshot.TickIndexForValidTick % CommandAgeAdjustmentLength);
            // 移到新槽位时，清除旧槽位与新槽位之间的数据
            if (curSlot != commandAgeAdjustmentSlot)
            {
                for (int i = (commandAgeAdjustmentSlot + 1) % CommandAgeAdjustmentLength;
                     i != (curSlot+1) % CommandAgeAdjustmentLength;
                     i = (i+1) % CommandAgeAdjustmentLength)
                {
                    commandAgeAdjustment[i] = 0;
                }
                commandAgeAdjustmentSlot = curSlot;
            }
            // 向下取整为一个 RTT 内执行的完整 Tick 数量
            if (rttInTicks > CommandAgeAdjustmentLength)
                rttInTicks = CommandAgeAdjustmentLength;
            // 客户端通过减去已经应用的修正来调整 Command Age，避免过度补偿或补偿不足
            // 假设客户端收到服务器的 Tick X，反馈该 Tick 的 Command 到达过晚，客户端需要通过加速或减速时间进行补偿
            // 服务器发来的 Ack 描述的是过去状态，因此客户端可能已根据先前报告的 Command Age 应用过部分补偿
            for (int i = 0; i < rttInTicks; ++i)
            {
                var slot = (CommandAgeAdjustmentLength + commandAgeAdjustmentSlot - i) % CommandAgeAdjustmentLength;
                commandAge -= commandAgeAdjustment[slot];
            }
            return commandAge;
        }
    }
}
