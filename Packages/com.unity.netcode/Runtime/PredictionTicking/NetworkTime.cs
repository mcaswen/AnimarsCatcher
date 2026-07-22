using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <see cref="NetworkTime"/> Singleton 用于为当前模拟 Tick 添加属性的标志
    /// 更多信息请参阅各标志的说明
    /// </summary>
    [Flags]
    public enum NetworkTimeFlags : byte
    {
        /// <summary>
        /// 表示当前 <see cref="NetworkTime.ServerTick"/> 是预测 Tick，且模拟正在预测 Group 内运行
        /// </summary>
        IsInPredictionLoop = 1 << 0,
        /// <summary>
        /// 仅在预测循环内有效，表示预测开始时的服务器 Tick
        /// </summary>
        IsFirstPredictionTick = 1 << 2,
        /// <summary>
        /// 仅在预测循环内有效，表示当前服务器 Tick 是最后一个待预测 Tick
        /// </summary>
        IsFinalPredictionTick = 1 << 3,
        /// <summary>
        /// 仅在预测循环内有效，表示当前服务器 Tick 是最后一个待预测的完整 Tick
        /// 设置 IsFinalPredictionTick 时，IsPartial 标志必须为 false
        /// 当前预测的服务器 Tick 是完整 Tick 时，也可以设置 IsFinalPredictionTick
        /// </summary>
        IsFinalFullPredictionTick = 1 << 4,
        /// <summary>
        /// 仅在服务器上有效
        /// 当前模拟 Tick 使用可变 Delta Time 运行，以补偿之前耗时过长的帧时为 true
        /// </summary>
        IsCatchUpTick = 1 << 5,
        /// <summary>
        /// 仅在预测循环内有效，表示当前服务器 Tick 是完整 Tick，且这是它首次作为非部分 Tick 被预测
        /// IsPartial 标志必须为 false
        /// 常用于确保不易回滚的效果只发生一次且不会重复，例如生成对象、粒子、VFX 或播放声音
        /// </summary>
        IsFirstTimeFullyPredictingTick = 1 << 6,
    }
    /// <summary>
    /// 同时存在于客户端和服务器 World 中，包含客户端与服务器模拟循环全部时间特征的 Singleton Component
    /// </summary>
    public struct NetworkTime : IComponentData
    {
        /// <summary>
        /// 服务器本帧将运行的当前模拟服务器 Tick，始终从 1 开始，0 视为无效值
        /// ServerTick 在客户端和服务器上的行为不同
        /// 在服务器上
        ///  - 始终是完整 Tick
        ///  - 严格单调递增，并且只按服务器 DGS 渲染帧率递增，即 UnityEngine 帧率，可配置为与该频率相同
        ///  - 因此在预测循环内外具有相同值
        /// 在客户端上
        ///  - 表示客户端当前预测服务器应在本帧模拟的 Tick
        ///    该值取决于当前 <see cref="NetworkSnapshotAck.EstimatedRTT"/> 和 <see cref="ClientTickRate.TargetCommandSlack"/>
        ///  - 可以是完整 Tick 或部分 Tick，客户端部分 Tick 的详细说明请参阅文档
        ///  - 不保证单调递增
        ///      - 在少数异常或恢复场景中，可能因为时间或延迟调整而回滚或向前跳跃
        ///      - 在预测循环期间，ServerTick 会改为最近一个已完整模拟的 Tick
        ///        如果因为收到 Snapshot 而发生回滚，则会改为所有 Entity 已接收 Tick 中最早的一个
        ///  - 两种情况下，该值都会在预测循环结束时重置为当前预测的服务器 Tick
        /// </summary>
        /// <remarks>
        /// 通过 <see cref="CommandDataUtility.AddCommandData{T}"/> 为 Command Data 分配 Tick 值时，
        /// 应使用 <see cref="InputTargetTick"/>，而不是 <see cref="ServerTick"/>
        /// </remarks>
        public NetworkTick ServerTick;

        /// <summary>
        /// 应为其收集、触发并发送输入 Command 的 Tick，以确保这些 Command 能及时到达服务器并被处理
        /// 通常与 <see cref="ServerTick"/> 相同，但以下情况除外
        /// a）在高 Ping 连接下使用 <see cref="ClientTickRate.MaxPredictAheadTimeMS"/>
        /// b）使用 <see cref="ClientTickRate.ForcedInputLatencyTicks"/>
        /// c）在 <see cref="NetCodeConfig.HostWorldMode.SingleWorld"/> 模式下处于不执行预测的 Off Frame
        /// 此时会在这些 Off Frame 中累积下一 Tick 的输入，因此 <see cref="InputTargetTick"/> 指向下一 Tick
        /// 四条时间线依次为：<c>Interpolation Tick（最早） -> Snapshot Arrival Tick（来自服务器）
        /// -> ServerTick（客户端预测） -> InputTargetTick（正在发送的输入）</c>
        /// </summary>
        /// <remarks>
        /// 通过 <see cref="CommandDataUtility.AddCommandData{T}"/> 为 Command Data 分配 Tick 值时，
        /// 应使用此变量，而不是 <see cref="ServerTick"/>
        /// </remarks>
        public NetworkTick InputTargetTick
        {
            get
            {
                if (ServerTick.IsValid)
                {
                    var networkTick = ServerTick;
                    networkTick.Add(EffectiveInputLatencyTicks);
                    if (IsOffFrame)
                        networkTick.Add(1);
                    return networkTick;
                }
                return NetworkTick.Invalid;
            }
        }

        /// <summary>
        /// 当前生效的 <see cref="ClientTickRate.ForcedInputLatencyTicks"/> 值，以 Tick 为单位，<b>仅限客户端</b>
        /// </summary>
        /// <remarks>
        /// 注意：客户端 Ping 大于 <see cref="ClientTickRate.MaxPredictAheadTimeMS"/> 时，此值会增加
        /// </remarks>
        public uint EffectiveInputLatencyTicks;
        /// <summary>
        /// 仅对使用可变步长运行的客户端有意义，在服务器上始终为 1.0，取值范围始终为 (0.0, 1.0]
        /// </summary>
        public float ServerTickFraction;
        /// <summary>
        /// 当前插值 Tick 的整数部分，在客户端上始终小于 ServerTick，在服务器上等于 ServerTick
        /// </summary>
        public NetworkTick InterpolationTick;
        /// <summary>
        /// Tick 的小数部分，即 XXX.fraction，始终处于 (0.0, 1.0] 范围内
        /// </summary>
        public float InterpolationTickFraction;
        /// <summary>
        /// 此 Tick 合并的模拟步数，用于让一次更新覆盖 N 个 Tick 以降低 CPU 开销
        /// 预测循环内的部分 Tick 始终为 1，预测循环外的部分 Tick 则可以大于 1
        /// </summary>
        public int SimulationStepBatchSize;
        /// <summary>
        /// 仅供内部使用，为当前服务器 Tick 值补充上下文和属性的特殊标志
        /// </summary>
        internal NetworkTimeFlags Flags;
        /// <summary>
        /// 仅供内部使用，World 创建后经过的网络总时间，服务器与客户端的行为不同
        /// - 服务器按固定时间步长推进，具体取决于 ClientServerTickRate
        /// - 客户端使用根据预测服务器 Tick 算出的网络 Delta Time，该时间不保证单调递增
        /// </summary>
        internal double ElapsedNetworkTime;
        /// <summary>
        /// 当前 Tick 使用 ServerTickDeltaTime 的一部分作为 Delta Time 运行时为 true
        /// 仅在客户端以可变帧率运行时可能为 true
        /// </summary>
        public bool IsPartialTick => ServerTickFraction < 1f;
        /// <summary>
        /// 表示当前 <see cref="NetworkTime.ServerTick"/> 是预测 Tick，且模拟正在预测 Group 内运行
        /// </summary>
        public bool IsInPredictionLoop => (Flags & NetworkTimeFlags.IsInPredictionLoop) != 0;
        /// <summary>
        /// 仅在预测循环内有效，表示预测开始时的服务器 Tick
        /// </summary>
        public bool IsFirstPredictionTick => (Flags & NetworkTimeFlags.IsFirstPredictionTick) != 0;
        /// <summary>
        /// 仅在预测循环内有效，表示当前服务器 Tick 是最后一个待预测 Tick
        /// </summary>
        public bool IsFinalPredictionTick => (Flags & NetworkTimeFlags.IsFinalPredictionTick) != 0;
        /// <summary>
        /// 仅在预测循环内有效，表示当前服务器 Tick 是最后一个待预测的完整 Tick
        /// </summary>
        public bool IsFinalFullPredictionTick => (Flags & NetworkTimeFlags.IsFinalFullPredictionTick) != 0;
        /// <summary>
        /// 仅在预测循环内有效，此 `ServerTick` 首次作为完整 Tick 被预测时为 true
        /// 完整是指第一次进行非部分 Tick 模拟，部分 Tick 不计入
        /// </summary>
        public bool IsFirstTimeFullyPredictingTick => (Flags & NetworkTimeFlags.IsFirstTimeFullyPredictingTick) != 0;
        /// <summary>
        /// 仅在服务器上有效
        /// 服务器判断自己落后超过一个 Tick 时，会查询 <see cref="ClientServerTickRate.MaxSimulationStepBatchSize"/>
        /// 和 <see cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/> 以决定如何追赶
        /// 如果配置使服务器在一帧内模拟两个或更多 Tick，除最后一个 Tick 外，其余 Tick 的 Catch-up 标志都会设为 true
        /// <br/>注意：仅将多个 Tick 批处理成一个 Tick，本身不会被视为 Catch-up Tick
        /// </summary>
        /// <remarks>
        /// 当 <see cref="ClientServerTickRate.SendSnapshotsForCatchUpTicks"/> 为 false 时，
        /// 此标志用于限制通过 <see cref="GhostSendSystem"/> 发送 Snapshot
        /// </remarks>
        public bool IsCatchUpTick => (Flags & NetworkTimeFlags.IsCatchUpTick) != 0;
        /// <summary>
        /// 统计本帧在预测循环内已触发的预测 Tick 数量
        /// 因此仅供客户端使用，并在 Tick 发生前递增，即第一个预测 Tick 的值为 1
        /// 在预测循环外，记录当前帧或上一帧的预测 Tick 数量，直至预测重新开始
        /// </summary>
        public int PredictedTickIndex { get; internal set; }
        /// <summary>
        /// 统计本帧预计触发的预测 Tick 数量，不考虑批处理
        /// 客户端：在预测循环开始时写入，参见 <see cref="PredictedSimulationSystemGroup"/>，并在第一个 Tick 发生前设置
        /// 服务器：在模拟 Group，即 <see cref="SimulationSystemGroup"/> 之前写入
        /// </summary>
        /// <remarks>
        /// Single World Host 可能出现没有游戏预测 Group 执行的 Off Frame
        /// 如果本帧将要或已经执行预测 Group，此值会被设置，并且仅在 SimulationSystemGroup 期间设置
        /// 要判断本帧是否会执行 Tick，请使用 <see cref="IsOffFrame"/>
        /// </remarks>
        public int NumPredictedTicksExpected { get; internal set; }
        /// <summary>
        /// 表示当前是否处于不执行任何 NetCode Tick 的 Off Frame
        /// 客户端 World 始终存在部分 Tick，因此此值始终为 false
        /// 在服务器和 Host World 中，此值取决于 Tick Rate 和 <see cref="ClientServerTickRate.FrameRateMode"/>
        /// 此值由 <see cref="UpdateNetworkTimeSystem"/> 更新，因此应在该系统执行后读取
        /// </summary>
        public bool IsOffFrame;

        /// <summary>
        /// 用于通过日志调试 NetworkTime 问题的辅助方法
        /// </summary>
        /// <returns>包含 NetworkTime 数据的格式化字符串</returns>
        public FixedString512Bytes ToFixedString()
        {
            var commandInterpolationDelay = ServerTick.IsValid && InterpolationTick.IsValid ? ServerTick.TicksSince(InterpolationTick) : 0;
            FixedString512Bytes flags = default;
            if (Flags == default)
                flags = "0";
            else
            {
                if (IsInPredictionLoop) flags.Append((FixedString32Bytes) $"|{nameof(IsInPredictionLoop)}");
                if (IsFirstPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFirstPredictionTick)}");
                if (IsFinalPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFinalPredictionTick)}");
                if (IsFinalFullPredictionTick) flags.Append((FixedString32Bytes) $"|{nameof(IsFinalFullPredictionTick)}");
                if (IsFirstTimeFullyPredictingTick) flags.Append((FixedString64Bytes) $"|{nameof(IsFirstTimeFullyPredictingTick)}");
                if (IsCatchUpTick) flags.Append((FixedString32Bytes) $"|{nameof(IsCatchUpTick)}");
            }
            FixedString32Bytes partial = IsPartialTick ? "PARTIAL" : "FULL";
            return $"NetworkTime[ServerTick:{ServerTick.ToFixedString()}|{(int) (ServerTickFraction * 100)}%|{partial}|+{SimulationStepBatchSize}|{PredictedTickIndex}/{NumPredictedTicksExpected}, InputTargetTick:{InputTargetTick.ToFixedString()}|+{EffectiveInputLatencyTicks}, InterpolationTick:{InterpolationTick.ToFixedString()}|{(int) (InterpolationTickFraction * 100)}%|D{commandInterpolationDelay}, Flags:{flags}]";
        }

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }

    /// <summary>
    /// 在客户端 World 中创建 NetworkTime Singleton Entity 时添加的组件
    /// 包含未缩放的应用程序 ElapsedTime 和 DeltaTime
    /// </summary>
    public struct UnscaledClientTime : IComponentData
    {
        /// <summary>
        /// World 创建后经过的当前未缩放时间
        /// 它可靠地跟踪真实经过时间，并在客户端所有状态下保持一致，包括已连接、已断开和游戏中
        /// </summary>
        public double UnscaleElapsedTime;
        /// <summary>
        /// 自上一帧以来的当前未缩放 Delta Time
        /// </summary>
        public float UnscaleDeltaTime;
    }

    static class NetworkTimeHelper
    {
        /// <summary>
        /// 当前 Tick 是完整 Tick 时返回当前 ServerTick，否则返回前一个 Tick
        /// 返回的服务器 Tick 会正确处理回绕，服务器 Tick 永远不会等于 0
        /// </summary>
        /// <param name="networkTime"></param>
        /// <returns></returns>
        static public NetworkTick LastFullServerTick(in NetworkTime networkTime)
        {
            var targetTick = networkTime.ServerTick;
            if (targetTick.IsValid && networkTime.IsPartialTick)
            {
                targetTick.Decrement();
            }
            return targetTick;
        }

        /// <summary>
        /// 当前 Tick 是完整 Tick 时返回当前 InputTargetTick，否则为部分 Tick 返回前一个 Tick
        /// </summary>
        /// <param name="networkTime"></param>
        /// <returns></returns>
        public static NetworkTick LastFullInputTargetTick(in NetworkTime networkTime)
        {
            var targetTick = networkTime.InputTargetTick;
            if (targetTick.IsValid && networkTime.IsPartialTick)
            {
                targetTick.Decrement();
            }
            return targetTick;
        }
    }

    /// <summary>
    /// 负责提前更新部分网络时间值的系统，使其可以在常规 <see cref="SimulationSystemGroup"/> 外部使用
    /// 如需获取 <see cref="NetworkTime.IsOffFrame"/>，请确保调用方系统在此系统之后执行
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct UpdateNetworkTimeSystem : ISystem
    {
        /// <inheritdoc cref="ISystem.OnUpdate"/>
        public void OnUpdate(ref SystemState state)
        {
            // 需要在 SimulationGroup Rate Manager 外设置 IsOffFrame，供该 Group 外部的用户系统访问
            // 因为服务器 World 可能根本不运行模拟 Group，否则用户将永远无法读取有效值
            ref var networkTime = ref SystemAPI.GetSingletonRW<NetworkTime>().ValueRW;
            var rateManager = state.World.GetExistingSystemManaged<SimulationSystemGroup>().RateManager;
            if (state.World.IsServer())
            {
                if (state.World.IsHost())
                {
                    var hostRateManager = rateManager as NetcodeHostRateManager;
                    networkTime.IsOffFrame = !hostRateManager.WillUpdateInternal();
                }
                else
                {
                    var serverRateManager = rateManager as NetcodeServerRateManager;
#pragma warning disable CS0618 // 类型或成员已过时
                    networkTime.IsOffFrame = !serverRateManager.WillUpdateInternal();
#pragma warning restore CS0618 // 类型或成员已过时
                }
            }
            else
            {
                networkTime.IsOffFrame = false; // 客户端存在部分 Tick，因此始终会执行 Tick
            }
        }
    }
}
