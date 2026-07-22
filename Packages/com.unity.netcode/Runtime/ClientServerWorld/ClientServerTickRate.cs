using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// ClientServerTickRate Singleton 用于配置客户端与服务器的模拟时间步长、服务器数据包发送频率和其他相关设置
    /// 如果客户端中不存在该 Singleton Entity，<see cref="Unity.NetCode.NetworkStreamReceiveSystem"/> 会在首次更新时自动创建
    /// 与之相对，服务器永远不会自动创建该 Entity，需要时由用户自行创建 Singleton 实例
    /// 之所以存在这种不对称行为，是因为客户端需要与服务器同步此 Singleton 数据
    /// 这是出于兼容性考虑，未来可能发生变化
    /// 可以通过以下任一种方式配置这些设置：
    /// <list type="bullet">
    /// <item>创建 World 后，在自定义 Unity.NetCode.ClientServerBootstrap 中创建 Entity</item>
    /// <item>在系统的 OnCreate 或 OnUpdate 中创建</item>
    /// </list>
    /// 创建 Singleton 时不必为全部字段设置合适值，只需修改相关设置，再调用 <see cref="ResolveDefaults"/>，
    /// 为尚未赋值的字段配置默认值
    /// <see cref="ClientServerTickRate"/> 设置会作为客户端初始连接握手的一部分进行同步，
    /// 对应 <see cref="Unity.NetCode.ClientServerTickRateRefreshRequest"/> 数据
    /// ClientServerTickRate 还应用于自定义其他仅服务器使用的时间设置，例如：
    /// <list type="bullet">
    /// <item>每帧最大 Tick 数量</item>
    /// <item>每帧最大 Tick 数量</item>
    /// <item>Tick 合批（&lt;`MaxSimulationStepBatchSize`）等</item>
    /// </list>
    /// 更多信息请参见各字段的文档
    /// </summary>
    /// <example>
    /// <code>
    /// class MyCustomClientServerBootstrap : ClientServerBootstrap
    /// {
    ///    override public void Initialize(string defaultWorld)
    ///    {
    ///        base.Initialise(defaultWorld);
    ///        var customTickRate = new ClientServerTickRate();
    ///        // 以 30 Hz 运行
    ///        customTickRate.simulationTickRate = 30;
    ///        customTickRate.ResolveDefault();
    ///        foreach(var world in World.All)
    ///        {
    ///            if(world.IsServer())
    ///            {
    ///               // 此处只在服务器上创建，但也可以对客户端 World 执行相同操作
    ///               var tickRateEntity = world.EntityManager.CreateSingleton(new ClientServerTickRate
    ///               {
    ///                   SimulationTickRate = 30;
    ///               });
    ///            }
    ///        }
    ///    }
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// 客户端连接后，不会再复制 ClientServerTickRate 的变化
    /// 如果在运行时修改设置，必须在客户端和服务器上执行相同修改
    /// </item>
    /// <item>
    /// 绝不能通过 Baker 将 ClientServerTickRate 添加到 SubScene
    /// 如果要根据场景设置配置 ClientServerTickRate，建议实现自定义组件，
    /// 并在游戏系统中修改 ClientServerTickRate
    /// </item>
    /// </list>
    /// </remarks>
    [Serializable]
    public struct ClientServerTickRate : IComponentData
    {
        /// <summary>
        /// 控制实际帧率高于模拟频率时模拟处理方式的枚举
        /// </summary>
        public enum FrameRateMode
        {
            /// <summary>
            /// 在纯服务器构建中使用 `Sleep`，否则使用 `BusyWait`
            /// </summary>
            Auto,
            /// <summary>
            /// 让游戏循环以完整频率运行，如果累计 DeltaTime 尚未达到模拟间隔，则跳过模拟更新
            /// </summary>
            BusyWait,
            /// <summary>
            /// 使用 `Application.TargetFrameRate` 将游戏循环频率限制为模拟频率
            /// </summary>
            Sleep
        }

        /// <summary>
        /// 服务器和预测循环的固定模拟频率
        /// 客户端可以以高于或低于该频率的速度渲染
        /// 默认值为 60 Hz
        /// </summary>
        /// <remarks>
        /// 注意：客户端不会锁定到此刷新率，参见 Partial Tick 文档
        /// 较高的值会提高玩法质量，但也会增加 CPU 和带宽开销
        /// 由于预测成本增加，较高的值在客户端上尤其昂贵
        /// </remarks>
        [Tooltip("The fixed simulation frequency of the Netcode gameplay simulation. Higher values incur higher CPU costs on both the client and server, especially during client prediction.")]
        [Min(1)]
        public int SimulationTickRate;

        /// <summary>
        /// 用于计算 <see cref="PredictedFixedStepSimulationSystemGroup"/> Tick Rate，即频率的乘数
        /// 该 Group 的频率必须是 <see cref="SimulationTickRate"/> 的整数倍
        /// 默认值为 1，表示 <see cref="PredictedFixedStepSimulationSystemGroup"/> 与预测循环以相同频率运行
        /// 计算出的 Delta 为 1.0/(SimulationTickRate*PredictedFixedStepSimulationTickRatio)
        /// </summary>
        [Tooltip("Multiplier used to calculate the tick rate (i.e. frequency) for the PredictedFixedStepSimulationSystemGroup.\n\nThe default (and recommendation) is 0 (which becomes 1 i.e. one fixed step per tick), where higher values allow physics to tick more frequently (i.e. at smaller intervals).")]
        [Range(0, 8)]
        public int PredictedFixedStepSimulationTickRatio;

        /// <summary>
        /// 1f / <see cref="SimulationTickRate"/>，可以把它理解为 NetCode 版本的 `fixedDeltaTime`
        /// </summary>
        public float SimulationFixedTimeStep => 1f / SimulationTickRate;

        /// <summary>
        /// 运行物理模拟使用的固定时间，始终是 SimulationFixedTimeStep 的整数倍 <br/>
        /// 该值等于 1f / (<see cref="SimulationTickRate"/> * <see cref="PredictedFixedStepSimulationTickRatio"/>)
        /// </summary>
        public float PredictedFixedStepSimulationTimeStep => 1f / (PredictedFixedStepSimulationTickRatio*SimulationTickRate);

        /// <summary>
        /// 服务器为每个客户端创建并发送 Snapshot 的频率
        /// 此频率可以低于模拟频率，此时服务器每 N 帧才向客户端发送一次新 Snapshot
        /// 默认值为 <see cref="SimulationTickRate"/>
        /// </summary>
        /// <remarks>
        /// 通过 <see cref="GhostSendSystem"/> 构建和发送 Snapshot 的 CPU 工作，通常是多人游戏中最显著的 CPU 开销
        /// 因此降低发送频率可以显著节省 CPU，但会牺牲玩法质量，网络丢包时尤其明显
        /// 请注意，服务器仍可以在每个模拟 Tick 发送数据，但发送给不同的客户端子集
        /// 这样可以把 CPU 负载分散到多个模拟 Tick，避免 CPU 峰值
        /// 例如 NetworkTickRate 为 30、SimulationTickRate 为 60 时，服务器会在一个 Tick 向一半客户端发送 Snapshot，
        /// 下一个 Tick 再发送给另一半客户端
        /// 因此每个客户端仍会每 2 个模拟 Tick 收到一个数据包，而服务器通过轮询策略把 CPU 负载分散到每个 Tick
        /// </remarks>
        [Tooltip("The rate at which the server creates (and sends) a snapshot to each client.\n\nIf zero (the default), this value will be set to the <b>SimulationTickRate</b>, but half (or one third) is often good enough.\n\nThe CPU work performed to build and send snapshots is often the most significant CPU cost in a multiplayer game. Thus, reducing this send-rate can lead to significant CPU savings, but at the expense of gameplay quality (especially when packets are lost to the network).")]
        [Min(0)]
        public int NetworkTickRate;
        /// <summary>
        /// 如果服务器无法跟上实时流逝，即服务器 Tick 频率过低，无法满足 <see cref="SimulationTickRate"/>，
        /// 则会在一帧中执行多个 Tick，尝试追赶进度
        /// 此设置限制单帧内最多执行多少次这样的更新
        /// 达到限制后，模拟时间的更新速度会低于真实时间
        /// 默认值为 1
        /// </summary>
        /// <remarks>
        /// Network Tick Rate 只适用于 Snapshot，Command 和 RPC 的频率不受此设置影响
        /// </remarks>
        [Tooltip("Denotes how many fixed-step ticks can be performed on any given Unity frame, when 'catching up', when running too slowly.\n\nDefault value is 0 (which becomes 1).")]
        [Range(0, 16)]
        public int MaxSimulationStepsPerFrame;
        /// <summary>
        /// 如果服务器即使执行 `MaxSimulationStepsPerFrame` 个 Tick 仍无法跟上模拟频率，
        /// 可以允许每个 Tick 使用更长的 DeltaTime，以保持游戏时间正确更新
        /// 这意味着系统不再执行两个 DeltaTime 都为 N 的 Tick，而是执行一个 DeltaTime 为 2*N 的 Tick
        /// 这种处理服务器性能尖峰的方式开销更低但精度更差，同时要求游戏逻辑能够正确处理
        /// </summary>
        [Tooltip("Denotes how many individual ticks will be batched together (into a single tick) when recovering from a severe slowdown.\n\nDefault value is 0 (which becomes 4).\n\n<b>Warning: You lose accuracy when batching ticks, and gameplay code must account for it.</b>")]
        [Range(0, 16)]
        public int MaxSimulationStepBatchSize;
        /// <summary>
        /// 如果服务器的更新频率可以高于模拟 Tick Rate，则可以在部分更新中跳过模拟 Tick（`BusyWait`），
        /// 或使用 `Application.TargetFrameRate` 限制更新频率（`Sleep`）
        /// `Auto` 会在 Dedicated Server 构建中使用 `Sleep`，在客户端/服务器构建及编辑器中使用 `BusyWait`
        /// </summary>
        [Tooltip("Denotes how the server should sleep, when determining when it should next tick.\n\nDefaults to <b>Auto</b>, which will use <b>Sleep</b> for dedicated server builds, and <b>BusyWait</b> for client and server builds (as well as the editor).")]
        public FrameRateMode TargetFrameRateMode;
        /// <summary>
        /// 如果服务器必须在同一帧运行多个模拟 Tick，可以为所有这些 Tick 发送 Snapshot（true），
        /// 也可以只发送最后一个（false）
        /// </summary>
        public bool SendSnapshotsForCatchUpTicks
        {
            get { return m_SendSnapshotsForCatchUpTicks; }
            set { m_SendSnapshotsForCatchUpTicks = value; }
        }

        [Tooltip("When the server has to run multiple simulation ticks in the same frame (to catch-up), this flag denotes whether or not the server will send snapshots for all catch-up ticks, or just the last one. Default is <b>false</b> (only the last).")]
        [SerializeField]
        private bool m_SendSnapshotsForCatchUpTicks;

        /// <summary>
        ///     NetCode 需要在服务器上为每个连接保存一份 Snapshot 确认（Ack）历史
        ///     此值表示该历史 Buffer 的大小，单位为 bit
        ///     暴露该值只是为了进一步修复一个特殊问题，详见备注
        ///     默认值为 4096 bit（0.5 KB），在常见情况下应能避免该问题，之前的硬编码默认值为 256 bit
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         由于 <see cref="GhostSendSystem" /> 的优先队列机制，增大此值可能修复以下错误：
        ///         <list type="bullet">
        ///             <item>静态 Ghost 始终重复发送</item>
        ///             <item>
        ///                 静态和动态 Ghost 尝试差分压缩时，无法正确找到 Baseline，
        ///                 即之前已发送并确认的值
        ///             </item>
        ///         </list>
        ///     </para>
        ///     <para>
        ///         NetCode 会按连接、按 Chunk 在环形 Buffer 中保存最多 32 个旧 Snapshot，
        ///         因而也保存对应 Baseline 及其 Ack，参见 <see cref="LowLevel.Unsafe.GhostChunkSerializationState" />
        ///         和 <see cref="GhostSystemConstants.SnapshotHistorySize" />
        ///         每次成功把 Chunk 序列化到 Snapshot Writer 时，都会向该环形 Buffer 追加一个条目
        ///     </para>
        ///     <para>
        ///         问题在于：当单个连接具有数万个相关 Ghost 时，我们强烈不建议这样做，
        ///         优先队列可能需要数十秒才会让一个 Chunk 再次提升到可发送位置
        ///         可以通过下式非常粗略地估算其下限：
        ///         <c>(((numGhosts/avgNumGhostsPerChunk)*averageSizeOfChunkInBytes)/transportMTU)/NetworkTickRate</c>
        ///         例如 10 万个经过良好优化的 Ghost，以 30 Hz 发送且模拟频率为 60 Hz，
        ///         完整复制一遍需要 <c>(((100000/40)*1200)/1400)/30 = ~72s</c>
        ///         也就是说，从上一次向客户端发送 Snapshot 后，大约已经经过 4285 个模拟 Tick
        ///     </para>
        ///     <para>
        ///         因此约 72 秒后检查 Ack Buffer 时，该 Ack 早已通过位移离开 256 Tick 历史 Buffer 的末端
        ///         最简单的解决方案，也就是此处实现的方案，是保存大得多的 Ack Buffer
        ///         当前默认容量为 4096 个条目，在 60 Hz 下约为 1.1 分钟；最小容量为 1024 个条目，在 60 Hz 下约为 17 秒
        ///         之前的默认容量为 256，在 60 Hz 下约为 4.26 秒
        ///         <b>此字段用于配置该容量</b>
        ///     </para>
        ///     <para>
        ///         现在可以找到 4.26 秒以前发送的 Snapshot Ack，
        ///         因而修复了差分压缩性能退化问题
        ///         以前虽然能找到 Baseline，但会将其视为未确认，导致无法使用
        ///     </para>
        ///     <para>
        ///         以前也无法通过 <c>isZeroChange</c> 将此 Chunk 标记为无变化，
        ///         因为判断 Ghost 无变化依赖于把当前值与任意已确认 Baseline 值比较
        ///         这意味着以前无法通过查找零变化的 <c>CanUseStaticOptimization</c> 提前退出
        ///         结果是在这种情况下经常重复发送之前已确认的静态 Ghost，
        ///         至少要等到服务器恰好在上一次 Ack 后的 <see cref="SnapshotAckMaskCapacity" /> 个 Tick 内再次发送同一 Chunk
        ///     </para>
        ///     <para>
        ///         类似地，如果实现了 <see cref="GhostSendSystemData.MinSendImportance" /> 等配置选项，
        ///         会人为延迟 Chunk 的处理
        ///         如果延迟恰好超过容量，该 Chunk 及其中的 Ghost 将永远无法确认
        ///         所幸现在的 <c>SnapshotAckMaskCapacity</c> 远高于建议设置的任何 <c>MinSendImportance</c>
        ///     </para>
        /// </remarks>
        [Tooltip("Denotes how many entries the snapshot ack history BitArray stores. Default value: 4096 bits. Min: 1024 bits.\n\nSolves an emergent problem when replicating tens of thousands of relevant static ghosts to a single connection - a case we strongly advise against. See XML doc.")]
        public uint SnapshotAckMaskCapacity;

        /// <summary>
        /// 在客户端上，NetCode 会尝试让自身固定时间步与渲染刷新率对齐，以减少 Partial Tick 并提高稳定性
        /// 此设置表示用于吸附和对齐的窗口百分比
        /// 默认值为 5，即双向各应用 5%
        /// 如果距离上一个完整 Tick 不超过 5%，或距离下一个完整 Tick 不超过 5%，则执行钳制
        /// -1 表示关闭钳制，0 表示使用默认值
        /// 最大值为 50，即双向各 50%，由于两个方向都会应用，因此会完全钳制
        /// </summary>
        /// <remarks>较高的值会产生更激进的对齐，可能被用户感知，因为需要移动更长的时间距离</remarks>
        public int ClampPartialTicksThreshold
        {
            readonly get => m_ClampPartialTicksThreshold;
            set => m_ClampPartialTicksThreshold = value;
        }
        [Tooltip("On the client, Netcode attempts to align its own fixed step with the render refresh rate, with the goal of reducing Partial ticks, and increasing stability.\n\nThis setting denotes the window (in %) to snap and align.\n\nDefaults to 5 (5%), which is applied each way.\nI.e. If you're within 5% of the last full tick, or if you're within 5% of the next full tick, we'll clamp. 50 (50%) to always clamp.")]
        [SerializeField]
        [Range(-1, 50)]
        private int m_ClampPartialTicksThreshold;

        /// <summary>
        /// 连接握手和审批流程的超时时间
        /// 注意：两个状态共用一个计时器
        /// 客户端必须在超时前同时完成 Handshake 和 Approval，进入 Approval 时不会重置计时器
        /// <br/>服务器接受客户端后会立即开始计时
        /// 如果服务器未在指定时间内完成连接握手和审批，则发生超时，默认值为 5000 ms
        /// </summary>
        /// <remarks>
        /// 客户端连接时的完整超时流程如下：
        /// <br/>   1. 客户端首先经历 Transport 层连接超时，即最大连接尝试次数乘以连接超时
        /// <br/>   2. UTP 连接成功后，NetCode 开始握手流程，自动交换协议版本 RPC
        /// <br/>   3. 如果客户端协议有效，服务器会把客户端转入已连接状态，
        /// 或在通过 <see cref="NetworkStreamDriver.RequireConnectionApproval"/> 启用审批时转入审批状态
        /// <br/>此超时同时适用于 Handshake 和 Approval 的累计时长，两者共用一个计时器
        /// </remarks>
        [Tooltip("The timeout for the connection handshake and approval procedure. Both must succeed within the allotted time!\n\nDefaults to 0ms (which becomes 5s).")]
        [Range(0, 120_000)]
        public uint HandshakeApprovalTimeoutMS;

        internal const int DefaultTickRate = 60;
        internal const int DefaultMaxSimulationStepsPerFrame = 1;
        internal const int DefaultMaxSimulationStepBatchSize = 4;
        internal const int DefaultPredictedFixedStepSimulationTickRatio = 1;
        internal const int DefaultHandshakeApprovalTimeoutMS = 5_000;

        /// <summary>
        /// 把所有用户未修改或范围无效的属性设为合适的默认值
        /// 尤其确保 <see cref="NetworkTickRate"/> 和 <see cref="SimulationTickRate"/> 永远不为 0
        /// </summary>
        public void ResolveDefaults()
        {
            if (SimulationTickRate <= 0)
                SimulationTickRate = DefaultTickRate;
            if (PredictedFixedStepSimulationTickRatio <= 0)
                PredictedFixedStepSimulationTickRatio = DefaultPredictedFixedStepSimulationTickRatio;
            if (NetworkTickRate <= 0)
                NetworkTickRate = SimulationTickRate;
            if (NetworkTickRate > SimulationTickRate)
                NetworkTickRate = SimulationTickRate;
            if (MaxSimulationStepsPerFrame <= 0)
                MaxSimulationStepsPerFrame = DefaultMaxSimulationStepsPerFrame;
            if (MaxSimulationStepBatchSize <= 0)
                MaxSimulationStepBatchSize = DefaultMaxSimulationStepBatchSize;
            if (SnapshotAckMaskCapacity == 0)
                SnapshotAckMaskCapacity = 4096;
            if (ClampPartialTicksThreshold == 0)
                ClampPartialTicksThreshold = 5;
            if (HandshakeApprovalTimeoutMS == 0)
                HandshakeApprovalTimeoutMS = DefaultHandshakeApprovalTimeoutMS;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void Validate()
        {
            FixedList4096Bytes<FixedString64Bytes> errors = default;
            ValidateAll(ref errors);
            if (errors.Length > 0)
                throw new ArgumentException(errors[0].ToString());
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void ValidateAll(ref FixedList4096Bytes<FixedString64Bytes> errors)
        {
            // 严格来说 ReSharper 在这里是正确的，得益于特性验证，使用 NetCodeConfig 时其中部分情况不可能发生
            // 但用户可以直接在 C# 中修改这些值，因此仍必须进行验证
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            if (SimulationTickRate <= 0)
                errors.Add($"{nameof(SimulationTickRate)} must always be > 0");
            if (PredictedFixedStepSimulationTickRatio <= 0)
                errors.Add($"{nameof(PredictedFixedStepSimulationTickRatio)} must always be > 0");
            if (NetworkTickRate <= 0)
                errors.Add($"{nameof(NetworkTickRate)} must always be > 0");
            if (NetworkTickRate > SimulationTickRate)
                errors.Add($"{nameof(NetworkTickRate)} must always be <= {nameof(SimulationTickRate)}");
            if (MaxSimulationStepsPerFrame <= 0)
                errors.Add($"{nameof(MaxSimulationStepsPerFrame)} must always be > 0");
            if (MaxSimulationStepBatchSize <= 0)
                errors.Add($"{nameof(MaxSimulationStepBatchSize)} must always be > 0");
            if (SnapshotAckMaskCapacity < 1024)
                errors.Add($"{nameof(SnapshotAckMaskCapacity)} has a minimum size of 1024");
            if (ClampPartialTicksThreshold > 50)
                errors.Add($"{nameof(ClampPartialTicksThreshold)} must always within be [-1, 50]");
            if(HandshakeApprovalTimeoutMS < 1000)
                errors.Add($"{nameof(HandshakeApprovalTimeoutMS)} must be >= 1000ms");
            // ReSharper restore ConditionIsAlwaysTrueOrFalse
        }

        /// <summary>
        /// 辅助方法：
        /// NetworkTickRate 等于 SimulationTickRate，或通过舍入足够接近时返回 1
        /// 为其一半时返回 2，为其三分之一时返回 3，以此类推
        /// </summary>
        /// <returns>Snapshot 发送间隔</returns>
        public int CalculateNetworkSendRateInterval() => (SimulationTickRate + NetworkTickRate - 1) / NetworkTickRate;

        /// <summary>
        /// 返回以 <see cref="SimulationTickRate"/> Tick 表示的 <see cref="MaxSendRate"/> 间隔，即再次发送此 Chunk 前的等待间隔
        /// </summary>
        /// <param name="MaxSendRate">来自 GhostAuthoring 的值</param>
        /// <returns>发送间隔，即每第 N 个 <see cref="SimulationTickRate"/> Tick 发送一次</returns>
        public byte CalculateNetworkSendIntervalOfGhostInTicks(ushort MaxSendRate)
        {
            if (MaxSendRate == 0)
                return 1; // 每个 SimulationTickRate Tick 都发送
            var maxSendRateMs = 1000f / MaxSendRate; // 例如 9 Hz 对应 111 ms
            var networkTickRateDelayMS = 1000f / NetworkTickRate; // 60 Hz 对应 16 ms
            return (byte)math.ceil((maxSendRateMs - 0.001f) / (networkTickRateDelayMS)); // = 111/16 = 6.9375 = 7
                                                                                          // 即每第 7 个 Tick 发送一次
                                                                                          // 也就是等待 6 个 Tick
        }
    }

    /// <summary>
    /// 服务器在初始握手期间向客户端发送的 RPC，用于让客户端的模拟 Tick Rate 属性与服务器保持一致
    /// </summary>
    internal struct ClientServerTickRateRefreshRequest : IComponentData
    {
        /// <inheritdoc cref="ClientServerTickRate.SimulationTickRate"/>
        public int SimulationTickRate;
        /// <inheritdoc cref="ClientServerTickRate.PredictedFixedStepSimulationTickRatio"/>
        public int PredictedFixedStepSimulationTickRatio;
        /// <inheritdoc cref="ClientServerTickRate.NetworkTickRate"/>
        public int NetworkTickRate;
        /// <inheritdoc cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/>
        public int MaxSimulationStepsPerFrame;
        /// <inheritdoc cref="ClientServerTickRate.MaxSimulationStepBatchSize"/>
        public int MaxSimulationStepBatchSize;
        /// <inheritdoc cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>
        public uint HandshakeApprovalTimeoutMS;

        internal readonly void Serialize(ref DataStreamWriter writer, in StreamCompressionModel compressionModel)
        {
            writer.WritePackedUIntDelta((uint) SimulationTickRate, ClientServerTickRate.DefaultTickRate, compressionModel);
            writer.WritePackedUIntDelta((uint) NetworkTickRate, ClientServerTickRate.DefaultTickRate, compressionModel);
            writer.WritePackedUIntDelta((uint) MaxSimulationStepBatchSize, ClientServerTickRate.DefaultMaxSimulationStepBatchSize, compressionModel);
            writer.WritePackedUIntDelta((uint) MaxSimulationStepsPerFrame, ClientServerTickRate.DefaultMaxSimulationStepsPerFrame, compressionModel);
            writer.WritePackedUIntDelta((uint) PredictedFixedStepSimulationTickRatio, ClientServerTickRate.DefaultPredictedFixedStepSimulationTickRatio, compressionModel);
            writer.WritePackedUIntDelta((uint) HandshakeApprovalTimeoutMS, ClientServerTickRate.DefaultHandshakeApprovalTimeoutMS, compressionModel);
        }

        internal void Deserialize(ref DataStreamReader reader, in StreamCompressionModel compressionModel)
        {
            SimulationTickRate = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultTickRate, compressionModel);
            NetworkTickRate = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultTickRate, compressionModel);
            MaxSimulationStepBatchSize = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultMaxSimulationStepBatchSize, compressionModel);
            MaxSimulationStepsPerFrame = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultMaxSimulationStepsPerFrame, compressionModel);
            PredictedFixedStepSimulationTickRatio = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultPredictedFixedStepSimulationTickRatio, compressionModel);
            HandshakeApprovalTimeoutMS = reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultHandshakeApprovalTimeoutMS, compressionModel);
        }

        public void ApplyTo(ref ClientServerTickRate tickRate)
        {
            tickRate.MaxSimulationStepsPerFrame = MaxSimulationStepsPerFrame;
            tickRate.NetworkTickRate = NetworkTickRate;
            tickRate.SimulationTickRate = SimulationTickRate;
            tickRate.MaxSimulationStepBatchSize = MaxSimulationStepBatchSize;
            tickRate.PredictedFixedStepSimulationTickRatio = PredictedFixedStepSimulationTickRatio;
            tickRate.HandshakeApprovalTimeoutMS = HandshakeApprovalTimeoutMS;
        }

        public void ReadFrom(in ClientServerTickRate tickRate)
        {
            NetworkTickRate = tickRate.NetworkTickRate;
            MaxSimulationStepsPerFrame = tickRate.MaxSimulationStepsPerFrame;
            MaxSimulationStepBatchSize = tickRate.MaxSimulationStepBatchSize;
            SimulationTickRate = tickRate.SimulationTickRate;
            PredictedFixedStepSimulationTickRatio = tickRate.PredictedFixedStepSimulationTickRatio;
            HandshakeApprovalTimeoutMS = tickRate.HandshakeApprovalTimeoutMS;
        }
    }

    /// <summary>
    /// 配置客户端何时运行预测循环
    /// </summary>
    public enum PredictionLoopUpdateMode
    {
        /// <summary>
        /// 仅当客户端至少生成了一个 Predicted Ghost 时，预测循环才会运行预测系统
        /// </summary>
        RequirePredictedGhost,
        /// <summary>
        /// 无论客户端是否生成了 Predicted Ghost，预测循环都会运行
        /// </summary>
        AlwaysRun
    }

    /// <summary>
    /// 在客户端 World 中创建 ClientTickRate 单例，可在运行时创建或从 SubScene 加载
    /// 用于配置客户端的网络时间同步、插值延迟、预测批处理及其他设置
    /// 各属性的详细信息请参阅对应字段
    /// </summary>
    [Serializable]
    public struct ClientTickRate : IComponentData
    {
        /// <summary>
        /// Interpolated Ghost 的插值 Buffer 所使用的网络 Tick 数量
        /// </summary>
        [Tooltip("If not zero, denotes the number of network ticks to use as an interpolation buffer for interpolated ghosts.\n\nDefaults to 2.\n\n<b>Warning: Ignored when InterpolationTimeMS is set.</b>")]
        [Min(0)]
        public uint InterpolationTimeNetTicks;
        /// <summary>
        /// Interpolated Ghost 的插值 Buffer 时长，单位为 ms
        /// 指定后优先于按 Tick 配置的插值时间并覆盖后者
        /// </summary>
        [Tooltip("If not zero, denotes the number of milliseconds to use as an interpolation buffer for interpolated ghosts.\n\nDefaults to 0 (OFF).\n\n<b>Warning: Is used instead of InterpolationTimeNetTicks, if set.</b>")]
        [Min(0)]
        public uint InterpolationTimeMS;
        /// <summary>
        /// 数据缺失时客户端能够向前外推的最大时长，单位为模拟 Tick
        /// </summary>
        [Tooltip("The maximum time (in simulation ticks) which the client can extrapolate ahead, when data is missing.\n\nDefaults to 20.")]
        [Min(0)]
        public uint MaxExtrapolationTimeSimTicks;
        /// <summary>
        /// 强制客户端输入延迟指定数量的 SimulationTickRate Tick，之后才通过客户端预测在本地回放
        /// 这会<b>显著</b>减少客户端平均需要回滚并重新模拟的 Tick 数量，
        /// 但代价是明显增加可感知的输入延迟，对游戏手感造成<b>较大</b>影响
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>警告 1：</b>仅应在能够接受较慢节奏的游戏和平台中把该值设为非零值，
        /// 例如移动端游戏、不使用鼠标瞄准的游戏等
        /// </para>
        /// <para>
        /// <b>警告 2：</b>该值可能配置得过高，使 <see cref="NetworkTime.ServerTick"/>
        /// 向后推移到落后于 <see cref="NetworkTime.InterpolationTick"/> 的程度
        /// 当这种情况即将发生时，Netcode for Entities 会改为增大插值窗口间隔，把插值时间线进一步后移
        /// 因此只要满足 <see cref="PredictionLoopUpdateMode"/> 条件，它始终会运行至少一次预测循环
        /// </para>
        /// <para>
        /// 由于不保存 Partial Tick 的输入，即使此处设为 1，Netcode for Entities 也会把输入回放钳制到完整 Tick
        /// 这可能使手柄方向键或摇杆等连续输入产生可感知的平滑度损失
        /// </para>
        /// </remarks>
        /// <seealso cref="NetworkTime.InputTargetTick"/>
        [Tooltip("Force the client input to be delayed by this many SimulationTickRate ticks before even being played back locally (via client prediction).\n\nI.e. Reduces the quantity of ticks your client needs to predict, but at the <b>considerable</b> expense of local input latency.\n\nDefaults to 0 (OFF).\n\n<i><b>WARNING: This value should only be greater than zero for games and platforms which can support slower-paced play. E.g. Mobile.</b></i>")]
        public byte ForcedInputLatencyTicks;
        /// <summary>
        /// 可接受的最大 Ping 值
        /// 客户端计算服务器 Tick 时会把 RTT 钳制到该值，因此 Ping 高于该值时，客户端会开始产生输入延迟，
        /// 效果类似启用 <see cref="ForcedInputLatencyTicks"/>
        /// 增大该值可以让客户端应对更高的 Ping，但高 Ping 客户端也需要执行更多预测步骤，从而增加 CPU 开销
        /// </summary>
        [Tooltip("This is the maximum accepted ping. RTT will be clamped to this value when calculating the server tick on the client, which means if ping is higher than this, the client will begin to incur input latency.\n\nIncreasing this makes the client able to deal with higher ping, but higher-ping clients will then need to run more prediction steps, which incurs more CPU time.")]
        [Range(0, 500)]
        public uint MaxPredictAheadTimeMS;

        /// <summary>
        ///     <para>
        ///         当 <see cref="NetworkTime.InterpolationTick" /> 越过客户端预测生成对象的 <b>spawnTick</b> 时，
        ///         如果该对象仍未完成分类，NetCode 包会自动销毁它
        ///         参见 <see cref="PredictedGhostSpawnSystem.CleanupPredictedSpawns" />
        ///     </para>
        ///     <para>
        ///         但这种默认行为可能过早销毁 Predicted Ghost，原因如下：
        ///         <list type="bullet">
        ///             <item>
        ///                 当某个连接具有大量相关 Ghost 时，即使没有丢包，新的 Ghost 生成也可能延迟复制，
        ///                 因为 Snapshot 容量和发送频率分别受 GhostSendSystemData.DefaultSnapshotPacketSize
        ///                 与 ClientServerTickRate.NetworkTickRate 限制
        ///             </item>
        ///             <item>
        ///                 任何形式的丢包都会延迟新 Ghost 生成的复制和确认，因为 Snapshot 本身可能丢失，
        ///                 客户端通过 CommandSendSystem 发送的 Ack Mask 也可能丢失
        ///                 注意：高抖动同样会导致丢包
        ///             </item>
        ///             <item>
        ///                 如果 InterpolationTimeMS 或 InterpolationTimeNetTicks 的 Buffer 窗口配置得很短，默认值即如此，
        ///                 NetCode 包就只有较少机会复制这个新生成对象
        ///                 如果频繁发生这种情况，应先考虑增大插值 Buffer 窗口，再调整此值
        ///             </item>
        ///         </list>
        ///     </para>
        ///     <para>
        ///         此值表示所有客户端 Predicted Ghost 额外存活的客户端预测 Tick 数量，
        ///         从而提高它们与较晚到达的服务器权威对应对象成功分类的概率
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     注意，增大此值也会让<b>错误预测</b>的客户端预测生成对象存活更久
        ///     <br />
        ///     还要注意：如果客户端 Predicted Ghost 经常无法在<b>插值窗口</b>内完成分类，可能说明该窗口过短，应考虑增大它
        /// </remarks>
        [Tooltip("Denotes how many additional <b>SimulationTickRate</b> ticks that all client predicted spawns will live for, increasing the likelihood of them being successfully classified against the real ghost sent by the authoritative server.\n\nDefaults to 0 (OFF).")]
        public ushort NumAdditionalClientPredictedGhostLifetimeTicks;
        /// <summary>
        /// 表示允许客户端 Predicted Ghost 自动分类的正负误差范围，
        /// 单位为 <see cref="ClientServerTickRate.SimulationTickRate"/> ServerTick，默认值为 ±5 Tick
        /// <br />
        /// 换言之，如果没有为某种 Predicted Ghost 编写用户分类系统，并且客户端检测到新的预测生成对象，
        /// 系统会检查新的服务器 Ghost 是否在客户端预测生成 Tick 的指定范围内生成，例如包含边界的 ±5 Tick
        /// 如果满足条件，系统会认为二者是同一个 Ghost，分类随即成功
        /// </summary>
        /// <remarks>
        /// 如果观察到因 spawnTick 差异过大而频繁分类失败，可增大该值，
        /// 例如发生 Server Tick Batching 时这种情况很常见
        /// <br />
        /// 如果观察到预测生成的 Ghost 经常错误分类，可减小该值，
        /// 尤其是在几个 Tick 内连续生成大量对象时
        /// <br />
        /// 条件允许时，应优先编写自己的分类系统，借助项目专用的逐实例 <see cref="GhostFieldAttribute"/>
        /// 数据更准确地分类新生成对象
        /// </remarks>
        [Tooltip(@"Denotes the plus and minus range (in ServerTick's) discrepancy that we allow client predicted ghosts to be automatically classified within. Defaults to ±5 ticks.

In other words: If no user-code classification system is written for a predicted ghost type, and a new predicted ghost spawn is detected on the client, we will check to see whether or not the new server ghost spawned within this many ticks of the client spawn. If it has, we will assume they are the same ghost, and therefore classification will succeed.

 - Increase this value if you observe frequent classification failures due to large spawnTick discrepancies (common when encountering Server Tick Batching, for example).

 - Decrease this value if you observe frequent mis-classification of predicted spawned ghosts.")]
        [Min(1)]
        public ushort DefaultClassificationAllowableTickPeriod;
        /// <summary>
        /// 指定客户端尝试领先服务器的模拟 Tick 数量，尽量确保服务器在实际消费命令前收到这些命令
        /// </summary>
        /// <remarks>较高的值会提高命令到达的可靠性，但代价是客户端预测窗口更长，而这本身可能降低游戏性能</remarks>
        [Tooltip("Specifies the number of simulation ticks that the client tries to stay ahead of the server, to try to make sure the commands are received by the server before they are actually consumed.\n\nDefaults to 2.\n\nHigher values increase command arrival reliability, at the cost of a longer client prediction window (which can itself degrade gameplay performance). This contributes to the overall RTT, including frame time, target command slack, etc.")]
        [Range(0, 16)]
        public uint TargetCommandSlack;
        /// <summary>
        /// `CommandSendSystem` 会在每个输入包中发送 <see cref="TargetCommandSlack"/> + <see cref="NumAdditionalCommandsToSend"/>
        /// 条命令作为丢包恢复机制，二者默认均为 2，因此共 4 条；硬上限为 32，参见 <see cref="CommandSendSystemGroup.k_MaxInputBufferSendSize"/>
        /// 此选项定义在 `TargetCommandSlack` 之外发送多少条<b>额外</b>命令
        /// 最小值为 1，因为即使连接没有丢包，不发送额外输入也可能造成输入丢失
        /// 默认值为 2
        /// 较高的值会消耗更多服务器入站带宽，但有助于应对不稳定连接
        /// 不过，也可能只是重复发送已经过期而无法使用的命令
        /// </summary>
        /// <remarks>
        /// 可通过 Packet Dump Utility 和 <see cref="NetworkSnapshotAck.CommandArrivalStatistics"/> 调试命令到达率及其统计信息
        /// </remarks>
        [Tooltip("The `CommandSendSystem` will send `TargetCommandSlack` + `NumAdditionalCommandsToSend` commands in each input packet (default of 2 and 2, thus 4), as a packet loss recovery mechanism.\n\nThis option defines how many additional packets to send (on top of `TargetCommandSlack`).\n\nMin value is 1, default value is 2.\n\nHigher values incur more server ingress bandwidth consumption, but can be useful when dealing with unstable connections.\n\nDebug command arrival rate (and statistics) via the Packet Dump Utility and/or the `NetworkSnapshotAck.CommandArrivalStats`.")]
        [Range(1, 32)]
        public uint NumAdditionalCommandsToSend;
        /// <summary>
        /// 客户端可以在预测循环中批处理模拟步骤
        /// 此设置控制对于之前已经预测过的 Tick，模拟能够批处理多少个模拟步骤
        /// 设为大于 1 的值可以提高性能，但 Gameplay 系统必须适配这种行为
        /// </summary>
        [Tooltip("The client can batch simulation steps in the prediction loop. This setting controls how many simulation steps the simulation can batch, <b>for ticks which have previously been predicted</b>.\n\nWhen 0, defaults to 1 at runtime.\n\nSetting this to a value larger than 1 will save performance at the cost of simulation accuracy. Gameplay systems need to account for it.")]
        [Range(0, 16)]
        public int MaxPredictionStepBatchSizeRepeatedTick;
        /// <summary>
        /// 客户端可以在预测循环中批处理模拟步骤
        /// 此设置控制对于首次进行预测的 Tick，模拟能够批处理多少个模拟步骤
        /// 设为大于 1 的值可以提高性能，但 Gameplay 系统需要相应适配
        /// </summary>
        [Tooltip("The client can batch simulation steps in the prediction loop. This setting controls how many simulation steps the simulation can batch, <b>for ticks which are being predicted for the first time</b>.\n\nWhen 0, defaults to 1 at runtime.\n\nSetting this to a value larger than 1 will save performance at the cost of simulation accuracy. Gameplay systems needs to be adapted.")]
        [Range(0, 16)]
        public int MaxPredictionStepBatchSizeFirstTimeTick;
        /// <summary>
        /// 配置客户端如何运行预测循环系统
        /// 默认情况下，只有 World 中存在 Predicted Ghost 时，客户端才会运行 <see cref="PredictedSimulationSystemGroup"/> 内的系统，
        /// 因而也只会在此时运行 <see cref="PredictedFixedStepSimulationSystemGroup"/> 内的系统
        /// 这种行为通常能够节省 CPU 时间，但在某些希望系统始终运行的场景下可能不够直观，例如：
        /// <list type="bullet">>
        /// <item>即使场景中只有 Interpolated Ghost 和静态几何体，也希望对 Physics World 执行射线检测，例如生成第一个 Predicted Ghost 前需要先检测静态几何体</item>
        /// <item>希望某些系统同时作用于 Interpolated Ghost 和 Predicted Ghost，并在同一个组中运行，当然需要留意相关限制，例如极少更新、重要度很低且使用航位推算的静态 Interpolated Ghost</item>
        /// </list>
        /// 选择备选模式 <see cref="PredictionLoopUpdateMode.AlwaysRun"/> 前，必须理解其影响，尤其是 CPU 成本
        /// 此模式下系统会始终运行，因此必须避免执行不必要的工作，例如为空查询调度 Job
        /// 尽管惯用的 foreach 和大多数 Job 在没有匹配项时通常不会执行实际工作，系统更新本身仍可能产生额外 CPU 开销
        /// 最佳实践是使用 RequireForUpdate 或类似检查作为系统运行的前置条件
        /// </summary>
        [Tooltip("Denotes if the client should run the prediction loop systems, even if no predicted ghosts are present in the client world. By default, the client doesn't run the systems inside the PredictedSimulationSystemGroup (and consequently, nor the ones in PredictedFixedStepSimulationSystemGroup) if there are no predicted ghosts.\n\nThis is a good behaviour in general, that saves some CPU cycles. However, it may be unintuitive, as there are situations where you would like to have these systems always run. For example:\n\n - You would like to ray cast against the physics world, even in cases where there are only interpolated ghosts and/or static geometry present. I.e. In order to spawn a predicted ghost in first place, you need to raycast against the static geometry.\n\n - You want some systems to act on both interpolated and predicted ghosts (and run in the same group, with certain caveats, of course). An example could be a \"dead-reckoned\" static, interpolated ghost that rarely updates (i.e. it has very low importance).")]
        public PredictionLoopUpdateMode PredictionLoopUpdateMode;
        /// <summary>
        /// 计算 Interpolation Delay 时用于补偿接收 Snapshot 频率抖动的乘数
        /// 默认值为 1.25
        /// </summary>
        [Tooltip("Multiplier used to compensate received snapshot rate jitter when calculating the Interpolation Delay.\n\nDefaults to 1.25.")]
        [Min(0.001f)]
        public float InterpolationDelayJitterScale;
        /// <summary>
        /// 用于限制单帧内 InterpolationDelay 的最大变化量，以该帧 deltaTicks 的百分比表示
        /// 默认值为帧 deltaTicks 的 10%
        /// 较小的值会更慢地适应丢包和抖动等网络状态，但延迟变化更平滑
        /// 较大的值会让 InterpolationDelay 更快适应变化，但可能导致插值结果突然跳变
        /// 建议范围为 [0.10 - 0.3]
        /// </summary>
        [Tooltip("Used to limit the maximum InterpolationDelay changes in one frame, as percentage of the frame deltaTicks.\n\nDefaults to 10% of the frame delta ticks. Recommended range is [0.10 - 0.3].\n\n - Smaller values will result in slow adaptation to the network state (loss and jitter) but would result in smooth delay changes.\n - Larger values would make the InterpolationDelay change quickly adapt but may cause sudden jump in the interpolated values.")]
        [Range(0.01f, 0.5f)]
        public float InterpolationDelayMaxDeltaTicksFraction;
        /// <summary>
        /// <para>单帧内可修正的插值延迟误差百分比，用于控制 InterpolationTickTimeScale
        /// 必须位于 (0, 1) 范围内</para>
        /// <code>
        ///              ________ 最大值
        ///            /
        ///           /
        /// 最小值 __/____________
        ///                         InterpolationDelayDelta
        /// </code>
        /// <para>默认值为当前插值 Tick 与下一个目标插值 Tick 之间差值的 10%
        /// 建议范围为 [0.075 - 0.2]</para>
        /// </summary>
        [Tooltip("The percentage of the error in the interpolation delay that can be corrected in one frame. Used to control InterpolationTickTimeScale.\n\nRecommended range is [0.075 - 0.2].")]
        [Range(0f, 1f)]
        public float InterpolationDelayCorrectionFraction;
        /// <summary>
        /// InterpolateTimeScale 的最小值，必须位于 (0, 1) 范围内，默认值为 0.85
        /// </summary>
        [Tooltip("The minimum value for the InterpolateTimeScale.\n\nDefaults to 0.85.")]
        [Range(0f, 1f)]
        public float InterpolationTimeScaleMin;
        /// <summary>
        /// InterpolateTimeScale 的最大值，必须大于 1.0，默认值为 1.1
        /// </summary>
        [Tooltip("The maximum value for the InterpolateTimeScale.\n\nDefaults to 1.1.")]
        [Min(1f)]
        public float InterpolationTimeScaleMax;
        /// <summary>
        /// <para>每帧可修正的预测服务器 Tick 误差百分比，用于控制客户端 deltaTime 缩放，
        /// 从而减慢或加快服务器 Tick 估算
        /// 必须位于 (0, 1) 范围内</para>
        /// <code>
        ///
        ///              ________ 最大值
        ///             /
        ///            /
        /// 最小值 ___/__________
        ///                      CommandAge
        /// </code>
        /// <para>默认值为误差的 10%
        /// 影响 Command Age 的两个主要因素是：
        ///  - 网络状况，包括延迟和抖动
        ///  - 服务器性能，例如运行帧率低于目标帧率
        ///
        /// 较小的 Time Scale 值可以平滑调整预测 Tick，但对网络和服务器帧率变化的响应较慢
        /// 较大的值可以更快地从网络状况不佳或服务器性能过慢引起的不同步中恢复，
        /// 但预测 Tick 的变化量也更大
        /// 建议范围为 [0.075 - 0.2]</para>
        /// </summary>
        [Tooltip("The percentage of the error in the predicted server tick that can be corrected each frame. Used to control the client deltaTime scaling, used to slow-down/speed-up the server tick estimate.\n\nDefaults to 10% of the error. Recommended range is [0.075 - 0.2].\n\n - Small time scale values allow for smooth adjustments of the prediction tick, but slower reaction to changes in both network and server frame rate.\n - Larger values causes recovery to be faster in desync situations, but the predicted ticks delta are larger.")]
        [Range(0f, 1f)]
        public float CommandAgeCorrectionFraction;
        /// <summary>
        /// PredictionTick Time Scale 的最小值，必须小于 1.0f，默认值为 0.9f
        /// 注意：最小值和最大值不必对称
        /// 建议范围为 (0.8 - 0.95)
        /// </summary>
        [Tooltip("The PredictionTick time scale min value.\n\nDefaults to 0.9. Recommended range is (0.8 - 0.95).\n\nNote: It is not mandatory to have the min and max values symmetric.")]
        [Range(0f, 1f)]
        public float PredictionTimeScaleMin;
        /// <summary>
        /// PredictionTick Time Scale 的最大值，必须大于 1.0f，默认值为 1.1f
        /// 注意：最小值和最大值不必对称
        /// 建议范围为 (1.05 - 1.2)
        /// </summary>
        [Tooltip("PredictionTick time scale max value.\n\nDefaults to 1.1. Recommended range is (1.05 - 1.2).\n\nNote: It is not mandatory to have the min and max values symmetric.")]
        [Range(1f, 2f)]
        public float PredictionTimeScaleMax;

        /// <summary>
        /// 插值窗口的大小
        /// </summary>
        /// <param name="tickRate">当前结构体值</param>
        /// <returns>以 <see cref="ClientServerTickRate.SimulationTickRate"/> Tick 表示的值</returns>
        public int CalculateInterpolationBufferTimeInTicks(in ClientServerTickRate tickRate)
        {
            if (InterpolationTimeMS != 0)
                return (int)((InterpolationTimeMS * tickRate.NetworkTickRate + 999) / 1000);
            return (int) InterpolationTimeNetTicks;
        }
        /// <summary>
        /// 插值窗口的大小
        /// </summary>
        /// <param name="tickRate">当前结构体值</param>
        /// <returns>以毫秒表示的值</returns>
        public float CalculateInterpolationBufferTimeInMs(in ClientServerTickRate tickRate) => CalculateInterpolationBufferTimeInTicks(in tickRate) * tickRate.SimulationFixedTimeStep * 1000;
    }
}
