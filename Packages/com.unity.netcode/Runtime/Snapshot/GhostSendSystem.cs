#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using UnityEngine;


namespace Unity.NetCode
{
    internal struct GhostCleanup : ICleanupComponentData
    {
        public int ghostId;
        public NetworkTick spawnTick;
        public NetworkTick despawnTick;
    }

    /// <summary>
    /// 仅供内部使用，用于向代码生成的 Ghost Serializer 传递部分数据的结构
    /// </summary>
    public struct GhostSerializerState
    {
        /// <summary>
        /// 根据实体引用获取 <see cref="GhostInstance"/> 的只读访问器
        /// 用于序列化 Ghost 实体引用
        /// </summary>
        public ComponentLookup<GhostInstance> GhostFromEntity;
    }

    internal struct GhostSystemConstants
    {
        /// <summary>
        /// 服务器在 <see cref="GhostChunkSerializationState"/> 中以及客户端在 <see cref="SnapshotDataBuffer"/> 环形 Buffer 中
        /// 内部保存的 Ghost Snapshot 数量
        /// 减小 SnapshotHistorySize 可降低服务器和客户端的存储成本
        /// 但会削弱服务器有效执行增量压缩的能力
        /// 原因是受客户端延迟影响，服务器通过客户端 Command 流收到 Snapshot Ack 时
        /// 存储已确认数据的槽位可能已经被覆盖
        /// 默认值 32 适用于 60 Hz NetworkTickRate、约 500 毫秒 RTT
        /// 且服务器每个 Tick 都向单个连接发送同一动态 Ghost 的场景
        /// </summary>
        /// <remarks>
        /// 默认值 32 适用于 60 Hz NetworkTickRate、约 500 毫秒 RTT
        /// 且服务器每个 Tick 都向单个连接发送同一动态 Ghost 的场景
        ///     <br />
        /// <c>NETCODE_SNAPSHOT_HISTORY_SIZE_16</c> 在静态 Ghost 的存储缩减和动态 Ghost 的 Ack 可用性之间取得较好平衡
        /// 建议用于最高 <see cref="GhostPrefabCreation.Config.MaxSendRate"/> 为 30 Hz
        /// 或 <see cref="ClientServerTickRate.NetworkTickRate"/> 为 30 的项目
        ///     <br />
        /// <c>NETCODE_SNAPSHOT_HISTORY_SIZE_6</c> 最适合大型项目
        /// 例如包含数百个动态 Ghost、数千个静态 Ghost
        /// 且玩家角色控制器已经因拥塞或 <see cref="GhostPrefabCreation.Config.MaxSendRate"/> 而以显著较低频率发送
        /// </remarks>
        public const int SnapshotHistorySize =
#if NETCODE_SNAPSHOT_HISTORY_SIZE_6
            6;
#elif NETCODE_SNAPSHOT_HISTORY_SIZE_16
            16;
#else
            32;
#endif
        /// <summary>
        /// 新 Prefab 数据最多约占 Snapshot 的一半
        /// </summary>
        public const uint MaxNewPrefabsPerSnapshot = 32u;
        /// <summary>
        /// 在 Snapshot 中每个序列化 Ghost 前写入其压缩后大小
        /// 客户端可利用此信息从错误状态恢复，或在部分情况下跳过 Ghost 数据
        /// 例如场景流入或流出期间的短暂状态
        /// </summary>
        public const bool SnapshotHasCompressedGhostSize = true;
        /// <summary>
        /// Baseline 的最大年龄，超过此限制的 Baseline 不会用于增量压缩
        /// </summary>
        /// <remarks>
        /// 网络 Tick 的索引部分为 31 位，为避免回绕使 TicksSince 产生负值，最多只能使用 30 位
        /// 此限制再保留 2 位余量
        /// </remarks>
        public const uint MaxBaselineAge = 1u<<28;

        /// <summary>
        /// 连单个 Ghost 都无法放入 Snapshot 后允许的最大 Snapshot 发送尝试次数
        /// </summary>
        /// <remarks>每次尝试都会将包大小翻倍，因此最后一次尝试的 Snapshot 比配置值大 <c>2^(8-1)，即 128 倍</c></remarks>
        public const int MaxSnapshotSendAttempts = 8;

        /// 配置 <see cref="GhostSendSystemData.DefaultSnapshotPacketSize"/> 时允许的最小值
        internal const int MinSnapshotPacketSize = 100;
        /// <see cref="GhostSendSystemData.PercentReservedForDespawnMessages"/> 的最小值
        internal const float MinPercentReservedForDespawnMessages = .2f;
        /// <see cref="GhostSendSystemData.PercentReservedForDespawnMessages"/> 的最大值
        internal const float MaxPercentReservedForDespawnMessages = .8f;
    }

#if UNITY_EDITOR
    internal struct GhostSendSystemAnalyticsData : IComponentData
    {
        public NativeArray<uint> UpdateLenSums;
        public NativeArray<uint> NumberOfUpdates;
    }
#endif


    /// <summary>
    /// 包含 <see cref="GhostSendSystem"/> 所有可调设置的单例组件
    /// </summary>
    [Serializable]
    public struct GhostSendSystemData : IComponentData
    {
        /// <summary>
        /// <see cref="MinSendImportance"/> 为非零值时，复制优先级系统可能在数秒内忽略以下两类 Chunk
        /// - 对新加入者而言尚未变化但属于新的 Chunk
        /// - 新生成的 Chunk
        /// 如果不希望出现此行为，请将本值设为高于 <see cref="MinSendImportance"/>
        /// 它会乘到这些对玩家或 World 而言属于新内容的 Ghost Chunk Importance 上
        /// 注意：这不保证所有新 Chunk 都能送达
        /// 只保证在带宽允许等条件下，每个 Ghost Chunk 会尽快为每个连接至少序列化并发送一次
        /// </summary>
        public uint FirstSendImportanceMultiplier
        {
            get => m_FirstSendImportanceMultiplier;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(FirstSendImportanceMultiplier));
#endif
                m_FirstSendImportanceMultiplier = value;
            }
        }

        /// <summary>
        /// 非 0 时表示单个 Snapshot 的目标大小，除非连接具有 <see cref="NetworkStreamSnapshotTargetSize"/> 组件
        /// 为 0 时使用扣除 Header 后的 <see cref="NetworkParameterConstants.MTU"/>
        /// 最小值为 <see cref="GhostSystemConstants.MinSnapshotPacketSize"/>
        /// </summary>
        [Tooltip("- If zero (the default), <b>NetworkParameterConstants.MTU</b> is used (minus headers).\n\n - Otherwise, denotes the desired size of an individual snapshot (unless the per-connection <b>NetworkStreamSnapshotTargetSize</b> component is present).")]
        [Min(0)]
        public int DefaultSnapshotPacketSize;

        /// <summary>
        /// 表示 Snapshot 容量中可用于销毁消息的最大比例
        /// 默认值为 33%，即 Snapshot 的三分之一，大型游戏建议约为 75%
        /// </summary>
        /// <remarks>
        /// 销毁数量越多，对销毁 <see cref="GhostInstance.ghostId"/> 的增量压缩效果越好
        /// 注意：受 Importance 缩放影响，<see cref="GhostSendSystem"/> 可能需要多个 Tick 才会再次处理某个 Chunk
        /// 并发现其中有 Ghost 需要销毁
        /// 因此提高 <see cref="MaxIterateChunks"/> 可能有助于更快登记销毁
        /// </remarks>
        [Tooltip("Denotes the maximum percentage of the snapshot's capacity that can be used for despawn messages.\n\nThe default is 33% (i.e. one third of a snapshot), though we recommend up to 75% for large scale games.")]
        [Range(GhostSystemConstants.MinPercentReservedForDespawnMessages, GhostSystemConstants.MaxPercentReservedForDespawnMessages)]
        public float PercentReservedForDespawnMessages;

        /// <summary>
        /// 纳入 Snapshot 所需的最低 Importance
        /// 即使包中仍有足够空间，Importance 低于此值的 Ghost Chunk 也不会加入 Snapshot
        /// </summary>
        /// <remarks>
        /// 从 1.4 起，优先使用可通过 GhostAuthoringComponent 配置的 <see cref="GhostPrefabCreation.Config.MaxSendRate"/>
        /// 此值按连接和 Chunk 分别计算，Importance 每个 Tick 按其配置值增长，直到数据发送，而非确认送达
        /// 例如 <c>MinSendImportance=60, SimulationTickRate=60, GhostAuthoringComponent.Importance=1</c>
        /// 表示 Ghost 大约每秒复制一次
        /// </remarks>
        [Tooltip("The minimum importance considered for inclusion in a snapshot. The Defaults to 0 (disabled).\n\nAny ghost chunk with an importance value lower than this value will not be added to the snapshot, even if there is enough space in the packet. Use to reduce send-rate for low-importance ghosts.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MinSendImportance;

        /// <summary>
        /// 对 Ghost Chunk 应用基于距离的优先级缩放后，纳入 Snapshot 所需的最低 Importance
        /// 即使包中仍有足够空间，缩放后 Importance 低于此值的 Ghost Chunk 也不会加入 Snapshot
        /// </summary>
        [Tooltip("The minimum importance considered for inclusion in a snapshot after applying distance based priority scaling to the ghost chunk. Any ghost chunk with a downscaled importance value lower than this will not be added to the snapshot, even if there is enough space in the packet.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MinDistanceScaledSendImportance;

        /// <summary>
        /// 表示在单个 <see cref="ClientServerTickRate.NetworkTickRate"/> Snapshot 发送间隔内
        /// <see cref="GhostSendSystem"/> 为给定连接在一个 Tick 中最多遍历的 Chunk 数量
        /// 适用于包含数千个静态 Ghost 的优化场景
        /// 这类场景通常有数百个静态 Chunk，系统会为寻找可能变化的 Chunk 而执行大量无效遍历
        /// </summary>
        /// <remarks>
        /// 正值会限制最大遍历 Chunk 数，但不能小于 <see cref="MaxSendChunks"/>，因此会自动提高到该值
        /// 使用默认值 0 表示让 <see cref="MaxIterateChunks"/> 使用 <see cref="MaxSendChunks"/> 的值
        /// 但这可能导致 Snapshot 包未达到预期填充度
        /// 使用 -1 表示持续遍历直到包填满，或触发 <see cref="MaxSendChunks"/> 等发送规则
        ///     <br/>
        /// <b>第一项警告：</b>如果 NetCode 因任何原因无法在 <see cref="MaxIterateChunks"/> 个 Chunk 内填满包
        /// 此索引之后的 Ghost Chunk 都不会处理，即使包中仍有空间
        /// 如果预期包应填满但实际未填满，请提高此值
        ///     <br/>
        /// <b>第二项警告：</b><see cref="MaxIterateChunks"/> 会限制处理的 Chunk 数量
        /// 且此筛选发生在检查 Ghost 是否不相关之前
        /// 例如 <see cref="MaxIterateChunks"/> 为 4，且 Importance 最高的 4 个 Chunk 只包含不相关 Ghost
        /// 则当前 Snapshot 不会发送任何 Ghost
        /// 因此建议将 <see cref="MaxIterateChunks"/> 设为至少 <see cref="MaxSendChunks"/> 的 2 倍
        /// </remarks>
        [Tooltip("Denotes the maximum number of chunks the <b>GhostSendSystem</b> will iterate over in a single tick, for a given connection, within a single <b>NetworkTickRate</b> snapshot send interval.\n\nIt's an optimization in use-cases where you have many thousands of static ghosts (and thus hundreds of static chunks which are iterated over unneccessarily to find ones containing possible changes).\n\nDefaults to 0 (i.e. use <b>MinSendImportance</b>)\nRecommendation: ~10\n\n - A positive value will clamp the maximum number of chunks we iterate over (but cannot be less than <b>MaxSendChunks</b>, thus clamped automatically to it).\n - Use 0 to denote that <b>MaxIterateChunks</b> should use <b>MaxSendChunks</b>.\n\n - Use -1 to denote that you want to iterate until the packet is filled - or send rules (like <b>MaxSendChunks</b>) are encountered.")]
        [Min(0)]
        public int MaxIterateChunks;

        /// <summary>
        /// 在单个 <see cref="ClientServerTickRate.NetworkTickRate"/> Snapshot 发送间隔内
        /// <see cref="GhostSendSystem"/> 为任意给定连接加入 Snapshot 的最大 Chunk 数量
        /// 仅当某 Chunk 至少有一个 Ghost 加入 Snapshot 时才增加计数
        /// <br/>
        /// <b>警告：</b>如果加入这些 Chunk 后未完全填满 Snapshot
        /// <see cref="MaxSendChunks"/> 可能导致包中留下不必要的空闲空间，解决方式参见 <see cref="MaxIterateChunks"/>
        /// </summary>
        [Tooltip("The maximum number of chunks the GhostSendSystem will add to the snapshot for any given connection, within a single NetworkTickRate snapshot send interval. Only incremented when at least one ghost is added to the snapshot for a chunk. Warning: <b>MaxSendChunks</b> may lead to unnecessarily empty snapshot packets, in cases where adding this many chunks to the snapshot does not completely fill it. See <b>MaxIterateChunks</b> for resolution.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MaxSendChunks;

        /// <summary>
        /// 在单个 <see cref="ClientServerTickRate.NetworkTickRate"/> Snapshot 发送间隔内
        /// <see cref="GhostSendSystem"/> 为任意给定连接加入 Snapshot 的最大实体数量
        /// 不统计不相关 Ghost 和已取消发送，例如零变化的静态优化 Chunk
        /// 可用于降低或控制服务器 CPU 时间
        /// <b>警告：</b>如果加入这些实体后未完全填满 Snapshot
        /// <see cref="MaxSendChunks"/> 可能导致包中留下不必要的空闲空间
        /// 请优先使用 <see cref="MaxSendChunks"/> 和 <see cref="MaxIterateChunks"/>
        /// </summary>
        /// <remarks>
        /// 需要注意的实现细节是，当前只能在 Chunk 已部分或全部写入 Snapshot 后检查此值
        /// 因此实际使用中，值 1 等同于 <c>MaxSendChunks = 1;</c>
        /// </remarks>
        [Tooltip("<b>Obsolete: No longer functional!</b>\n\nThe maximum number of entities the <b>GhostSendSystem</b> will add to the snapshot for any given connection, within a single <b>NetworkTickRate</b> snapshot send interval. Ignores irrelevant ghosts and cancelled sends (e.g. zero change static optimized chunks). This can be used to reduce / control CPU time on the server.\n\n<b>Warning</b>: <b>MaxSendChunks</b> may lead to unnecessarily empty snapshot packets, in cases where adding this many entities to the snapshot does not completely fill it. Prefer <b>MaxSendChunks</b> and <b>MaxIterateChunks</b>.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        [ReadOnly]
        [Obsolete("No longer functional! Prefer MaxSendChunks and MaxIterateChunks to tweak GhostSendSystem CPU characteristics. (RemovedAfter 1.x)", false)]
        public int MaxSendEntities;

        /// <summary>
        /// 用于降低上次发送时所有实体均不相关的 Chunk Importance
        /// Importance 会除以此值，可配合 MinSendImportance 使用
        /// 以避免每帧更新低 Importance 内容的相关性
        /// </summary>
        public int IrrelevantImportanceDownScale
        {
            get => m_IrrelevantImportanceDownScale;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(IrrelevantImportanceDownScale));
#endif
                m_IrrelevantImportanceDownScale = value;
            }
        }

        /// <summary>
        /// 将 Chunk 传给 Ghost Importance 缩放函数指针前，会把每个 Chunk 的优先级乘以此值，默认 1000
        /// 使缩放函数能够计算并返回粒度更细的结果
        /// </summary>
        public ushort ImportanceScalingMultiplier
        {
            get { return m_ImportanceScalingMultiplier; }
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(IrrelevantImportanceDownScale));
#endif
                m_ImportanceScalingMultiplier = value;
            }
        }
        [Tooltip("We multiply every chunks priority by this value (default: 1k) just before passing said chunks to ghost importance scaling function pointers, to allow said scaling functions to play with -- and therefore return -- better, more fine-grained values.")]
        [Min(1)]
        internal ushort m_ImportanceScalingMultiplier;

        /// <summary>
        /// 强制所有 Ghost 使用单个 Snapshot 增量压缩值预测 Baseline
        /// 此设置以增加带宽为代价降低 CPU 使用量，主要用于衡量哪些 Ghost 应使用静态优化而非动态优化
        /// 如果启用后每个 Ghost 的位数没有显著增加，该 Ghost 可以使用静态优化节省 CPU
        /// </summary>
        public bool ForceSingleBaseline
        {
            get { return m_ForceSingleBaseline; }
            set { m_ForceSingleBaseline = value; }
        }
        [Tooltip("Force all ghosts to use a single snapshot delta-compression value prediction baseline. This will reduce CPU usage at the expense of increased bandwidth usage.\n\nDefaults to false (no).\n\nThis is mostly meant as a way of measuring which ghosts should use static optimization instead of dynamic. If the bits / ghost does not significantly increase when enabling this the ghost can use static optimization to save CPU.")]
        [SerializeField]
        internal bool m_ForceSingleBaseline;

        /// <summary>
        /// 调试功能：强制所有 Ghost 使用预序列化
        /// 这意味着部分序列化会为所有连接统一执行一次，而不是每个连接分别执行
        /// 对简单 Ghost 或很少发送的 Ghost，此设置可能增加 CPU 时间
        /// 此开关用于衡量哪些 Ghost 能从预序列化中获益
        /// </summary>
        /// <remarks>不应在生产环境启用</remarks>
        public bool ForcePreSerialize
        {
            get { return m_ForcePreSerialize; }
            set { m_ForcePreSerialize = value; }
        }
        [Tooltip("DEBUG FEATURE: Force all ghosts to use pre-serialization. This means part of the serialization will be done once for all connection, instead of once per-connection.\n\nDefaults to false (don't).\n\nThis can increase CPU time for simple ghosts and ghosts which are rarely sent. This switch is meant as a way of measuring which ghosts would benefit from using pre-serialization.\n\n<b>Should not be enabled in Production builds!</b>")]
        [SerializeField]
        internal bool m_ForcePreSerialize;

        /// <summary>
        /// 实体发生结构变更时尝试保留其 Snapshot 历史 Buffer
        /// 每当 Ghost 发生结构变更，都需要查找并复制数据，因此会增加服务器 CPU 成本
        /// Snapshot 历史并非始终能够保留，所以此标志不提供 100% 保证
        /// 修改此值时应测量 CPU 和带宽影响
        /// </summary>
        public bool KeepSnapshotHistoryOnStructuralChange
        {
            get { return m_KeepSnapshotHistoryOnStructuralChange; }
            set { m_KeepSnapshotHistoryOnStructuralChange = value; }
        }
        [Tooltip("Try to keep the snapshot history buffer for an entity when there is a structural change. Doing this will require a lookup and copy of data whenever a ghost has a structural change, which will add additional CPU cost on the server.\n\nDefaults to true (do).\n\nKeeping the snapshot history will not always be possible, so, this flag does no give a 100% guarantee, and you are expected to measure CPU and bandwidth when changing this.")]
        [SerializeField]
        internal bool m_KeepSnapshotHistoryOnStructuralChange;

        /// <summary>
        /// 为 Ghost 中的每个组件启用性能分析作用域
        /// 可帮助定位 Ghost 序列化成本高的原因，但会产生性能开销，因此默认不启用
        /// </summary>
        public bool EnablePerComponentProfiling
        {
            get { return m_EnablePerComponentProfiling; }
            set { m_EnablePerComponentProfiling = value; }
        }

        [Tooltip("Enable profiling scopes for each component in a ghost. This can help track down why a ghost is expensive to serialize - but it comes with a performance cost, so is not enabled by default.")]
        [SerializeField]
        internal bool m_EnablePerComponentProfiling;

        /// <summary>
        /// 单个 Tick 中清理未使用序列化数据的连接数量
        /// 提高此值可更快回收内存，但会使用更多 CPU 时间
        /// </summary>
        [Tooltip("The number of connections to cleanup unused serialization data for, in a single tick. Setting this higher can recover memory faster, but uses more CPU time.\n\nDefaults to 1.")]
        [Min(1)]
        public int CleanupConnectionStatePerTick;

        [Tooltip("This multiplies the importance value used on new (new to the player, or new to the world) ghost chunks.\n\nDefaults to 1 (OFF).\n\nNon-zero values for MinSendImportance can cause both: a) 'unchanged chunks that are new to a new-joiner' and b) 'newly spawned chunks' to be ignored by the replication priority system for multiple seconds. If this behaviour is undesirable, set this to be above MinSendImportance.\n\nNote: This does not guarantee delivery of all new chunks, it only guarantees that every ghost chunk will get serialized and sent at least once per connection, as quickly as possible (e.g. assuming you have the bandwidth for it).")]
        [Min(1)]
        [SerializeField]
        uint m_FirstSendImportanceMultiplier;
        [Tooltip("Value used to scale down the importance of chunks where all entities were irrelevant last time it was sent. The importance is divided by this value.\n\nDefaults to 1 (OFF).\n\nIt can be used together with MinSendImportance to make sure relevancy is not updated every frame, for ghosts with low importance.")]
        [Min(1)]
        [SerializeField]
        int m_IrrelevantImportanceDownScale;

        /// <summary>
        /// 设置内部临时流的初始大小，Ghost 数据会序列化到该流中
        /// 较小值会因多轮序列化产生额外成本，较大值通常能提供更好性能
        /// 此 Buffer 的最小大小会强制设为出站数据流的初始容量
        /// 通常是 MaxMessageSize，分片载荷时可能更大
        /// 建议默认值 8 KB 相对于包大小非常大，但可让 <see cref="GhostSendSystem"/>
        /// 写入多种大小不一的中小型 Ghost 实体，每个实体可达数百字节，而无需额外序列化开销
        /// </summary>
        public int TempStreamInitialSize
        {
            get => m_TempStreamSize;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(m_TempStreamSize));
#endif
                m_TempStreamSize = value;
            }
        }

        /// <summary>
        /// 设置后允许使用任意已注册 <see cref="GhostPrefabCustomSerializer"/> 序列化 Ghost Chunk
        /// </summary>
        public int UseCustomSerializer
        {
            get => m_UseCustomSerializer ? 1 : 0;
            set => m_UseCustomSerializer = value > 0;
        }
        [Tooltip("Value used to set the initial size of the internal temporary stream in which ghost data is serialized.\n - Smaller sizes will incur in extra serialization costs (as it may need to be resized mid-serialization, causing multiple round of serialization).\n - Larger sizes provide better performance (overall).\n\nThe minimum size of this buffer is forced to be the initial capacity of the outgoing data stream (usually MaxMessageSize or larger for fragmented payloads).\n\nThe suggested default (8kb), while extremely large in respect to the packet size, would allow the GhostSendSystem to be able to to write a large range of mid/small ghost entities types, with varying size (up to hundreds of bytes each), without incurring in extra serialization overhead.")]
        [Range(2 * 1024, 10 * 1024)]
        [SerializeField]
        internal int m_TempStreamSize;
        [Tooltip("When set, enables support for using any registered GhostPrefabCustomSerializer to serialize ghost chunks.")]
        [SerializeField]
        internal bool m_UseCustomSerializer;

        internal void Initialize()
        {
            MinSendImportance = 0;
            MinDistanceScaledSendImportance = 0;
            PercentReservedForDespawnMessages = .33f;
            MaxSendChunks = 0;
            MaxIterateChunks = 0;
            ForceSingleBaseline = false;
            ForcePreSerialize = false;
            KeepSnapshotHistoryOnStructuralChange = true;
            EnablePerComponentProfiling = false;
            CleanupConnectionStatePerTick = 1;
            m_FirstSendImportanceMultiplier = 1;
            m_IrrelevantImportanceDownScale = 1;
            m_TempStreamSize = 8 * 1024;
            m_ImportanceScalingMultiplier = 1000;
        }
    }

    /// <summary>
    /// <para>
    /// 仅存在于服务器 World，负责向客户端复制 Ghost 实体
    /// <see cref="GhostSendSystem"/> 是整个包中最复杂的系统之一
    /// 大量依赖多线程 Job，尽可能并行地向所有连接分发 Ghost
    /// </para>
    /// <para>
    /// Ghost 实体通过以 <see cref="ClientServerTickRate.NetworkTickRate"/> 频率向客户端发送状态 Snapshot 进行复制
    /// 连接带有 <see cref="NetworkStreamInGame"/> 组件时，Snapshot 才会通过不可靠通道流式发送到客户端
    /// 这种连接通常称为已进入游戏
    /// 为节省带宽，Snapshot 会相对于客户端报告的最新已接收 Snapshot 执行增量压缩
    /// 默认最多使用 3 个 Baseline，并采用预测压缩方案，参见 <see cref="GhostDeltaPredictor"/>
    /// 可通过 <see cref="GhostSendSystemData"/> 设置减少 Baseline 数量及 CPU 周期
    /// </para>
    /// <para>
    /// GhostSendSystem 设计为每次网络更新向每个连接发送<b>单个数据包</b>
    /// 默认情况下，系统会尝试向客户端复制 World 中所有现有 Ghost
    /// 当所有 Ghost 无法序列化到同一个包时，会按 Importance 设置实体优先级
    /// </para>
    /// <para>
    /// 可在 Prefab 的 Authoring 阶段通过 <see cref="Unity.NetCode.GhostAuthoringComponent"/> 设置基础 Ghost Importance
    /// 运行时会根据以下因素缩放 Ghost Importance
    /// </para>
    /// <para>- 年龄，即实体上次发送距今的时间</para>
    /// <para>- 基于距离缩放，参见 <see cref="GhostConnectionPosition"/> 和 <see cref="GhostDistanceImportance"/></para>
    /// <para>- 自定义缩放，参见 <see cref="GhostImportance"/></para>
    /// <para>
    /// Ghost 实体按 Chunk 复制，同一 Chunk 中的所有 Ghost 会一起复制
    /// Importance 及其缩放都应用于整个 Chunk
    /// </para>
    /// <para>
    /// 发送系统也可配置为每帧发送多个 Ghost 包，并使用大于 MaxMessageSize 的 Snapshot
    /// 此时 Snapshot 包会通过配置了 <see cref="FragmentationPipelineStage"/> 的另一条不可靠通道发送
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    [BurstCompile]
    public partial struct GhostSendSystem : ISystem
    {
        NativeParallelHashMap<RelevantGhostForConnection, int> m_GhostRelevancySet;

        EntityQuery ghostQuery;
        EntityQuery ghostSpawnQuery;
        EntityQuery ghostDespawnQuery;
        EntityQuery prespawnSharedComponents;

        EntityQueryMask internalGlobalRelevantQueryMask;
        EntityQueryMask netcodeEmptyQuery;

        EntityQuery connectionQuery;

        NativeQueue<int> m_FreeGhostIds;
        NativeArray<int> m_AllocatedGhostIds;
        NativeList<int> m_DestroyedPrespawns;
        NativeQueue<int> m_DestroyedPrespawnsQueue;
        NativeReference<NetworkTick> m_OldestPendingDespawnTickByAll;
#if UNITY_EDITOR
        NativeArray<uint> m_UpdateLen;
        NativeArray<uint> m_UpdateCounts;
#endif

        NativeList<ConnectionStateData> m_ConnectionStates;
        JobHandle m_ConnectionStatesJobHandle;

        /// <summary>
        /// PlayModeTool 的 ImportanceDrawerSystem 使用的内部 API
        /// 返回给定 connectionEntity 对应的 connectionStateData
        /// </summary>
        /// <param name="connectionEntity">要获取 connectionStateData 的 connectionEntity</param>
        /// <returns>
        /// 用于访问 ConnectionStateData 的 JobHandle
        /// 以及给定实体对应的 connectionStateData
        /// </returns>
        /// <exception cref="ArgumentException">connectionEntity 不在 GhostSendSystem 的连接状态数组中时抛出</exception>
        internal (JobHandle, ConnectionStateData) GetConnectionStateData(Entity connectionEntity) =>
            (m_ConnectionStatesJobHandle, m_ConnectionStates[m_ConnectionStateLookup[connectionEntity]]);

        NativeParallelHashMap<Entity, int> m_ConnectionStateLookup;
        StreamCompressionModel m_CompressionModel;
        NativeParallelHashMap<int, ulong> m_SceneSectionHashLookup;

        NativeList<ConnectionStateData> m_ConnectionsToProcess;
#if NETCODE_DEBUG
        EntityQuery m_PacketLogEnableQuery;
        ComponentLookup<PrefabDebugName> m_PrefabDebugNameFromEntity;
        FixedString512Bytes m_LogFolder;
#endif

        NativeParallelHashMap<SpawnedGhost, Entity> m_GhostMap;
        NativeQueue<SpawnedGhost> m_FreeSpawnedGhostQueue;

        static readonly Profiling.ProfilerMarker s_PrioritizeChunksMarker = new Profiling.ProfilerMarker("PrioritizeChunks");
        internal static readonly Profiling.ProfilerMarker s_GhostGroupMarker = new Profiling.ProfilerMarker("GhostGroup");
        internal static readonly Profiling.ProfilerMarker s_CanUseStaticOptimizationMarker = new Profiling.ProfilerMarker("CanUseStaticOptimization");
        internal static readonly Profiling.ProfilerMarker s_RelevancyMarker = new Profiling.ProfilerMarker("Relevancy");
        internal static readonly Profiling.ProfilerMarker s_GhostGroupRelevancyMarker = new Profiling.ProfilerMarker("GhostGroupRelevancy");
        static readonly Profiling.ProfilerMarker k_Scheduling = new Profiling.ProfilerMarker("GhostSendSystem_Scheduling");
        static readonly Profiling.ProfilerMarker s_TryGetChunkStateOrNewMarker = new Profiling.ProfilerMarker("TryGetChunkStateOrNew");

        GhostPreSerializer m_GhostPreSerializer;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
        ComponentLookup<NetworkSnapshotAck> m_SnapshotAckFromEntity;
        ComponentLookup<GhostType> m_GhostTypeFromEntity;
        ComponentLookup<NetworkStreamConnection> m_ConnectionFromEntity;
        ComponentLookup<GhostInstance> m_GhostFromEntity;
        ComponentLookup<NetworkStreamSnapshotTargetSize> m_SnapshotTargetFromEntity;
        ComponentLookup<EnablePacketLogging> m_EnablePacketLoggingFromEntity;
        ComponentLookup<OverrideGhostData> m_GhostOverrideFromEntity;

        ComponentTypeHandle<GhostCleanup> m_GhostSystemStateType;
        ComponentTypeHandle<PreSerializedGhost> m_PreSerializedGhostType;
        ComponentTypeHandle<GhostInstance> m_GhostComponentType;
        ComponentTypeHandle<GhostOwner> m_GhostOwnerComponentType;
        ComponentTypeHandle<GhostChildEntity> m_GhostChildEntityComponentType;
        ComponentTypeHandle<PreSpawnedGhostIndex> m_PrespawnedGhostIdType;
        ComponentTypeHandle<GhostType> m_GhostTypeComponentType;

        EntityTypeHandle m_EntityType;
        BufferTypeHandle<GhostGroup> m_GhostGroupType;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupType;
        BufferTypeHandle<PrespawnGhostBaseline> m_PrespawnGhostBaselineType;
        SharedComponentTypeHandle<SubSceneGhostComponentHash> m_SubsceneGhostComponentType;

        BufferLookup<PrespawnGhostIdRange> m_PrespawnGhostIdRangeFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostTypeCollectionFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentCollectionFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostComponentIndexFromEntity;
        BufferLookup<PrespawnSectionAck> m_PrespawnAckFromEntity;
        BufferLookup<PrespawnSceneLoaded> m_PrespawnSceneLoadedFromEntity;

        int m_CurrentCleanupConnectionState;
        uint m_SentSnapshots;
        ComponentTypeHandle<GhostImportance> m_GhostImportanceType;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
#if NETCODE_DEBUG
            m_LogFolder = NetDebug.LogFolderForPlatform();
            NetDebugInterop.Initialize();
#endif
            ghostQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostCleanup>());
            EntityQueryDesc filterSpawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostInstance)},
                None = new ComponentType[] {typeof(GhostCleanup), typeof(PreSpawnedGhostIndex)}
            };
            // TODO：如果所有 Ghost Prefab 都具有 GhostNeedsInitialization 等独立标签
            // 就可以在生成时让 Ghost 已带有 GhostCleanup，使序列化 Job 在同一帧检测到它
            // 从而避免服务器生成到实际发送之间额外约 16 毫秒延迟，这对导弹等时间敏感生成对象影响很大
            EntityQueryDesc filterDespawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostCleanup)},
                None = new ComponentType[] {typeof(GhostInstance)}
            };
            ghostSpawnQuery = state.GetEntityQuery(filterSpawn);
            ghostDespawnQuery = state.GetEntityQuery(filterDespawn);
            prespawnSharedComponents = state.GetEntityQuery(ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            internalGlobalRelevantQueryMask = state.GetEntityQuery(ComponentType.ReadOnly<PrespawnSceneLoaded>()).GetEntityQueryMask();
            netcodeEmptyQuery = state.GetEntityQuery(new EntityQueryDesc { None = new ComponentType[] { typeof(GhostInstance) } }).GetEntityQueryMask(); // default 会匹配全部实体，因此必须指定 None 才能表达未设置查询

            m_FreeGhostIds = new NativeQueue<int>(Allocator.Persistent);
            m_AllocatedGhostIds = new NativeArray<int>(2, Allocator.Persistent);
            m_AllocatedGhostIds[0] = 1; // 确保 0 始终无效
            m_AllocatedGhostIds[1] = 1; // 确保 0 始终无效

            m_DestroyedPrespawns = new NativeList<int>(Allocator.Persistent);
            m_DestroyedPrespawnsQueue = new NativeQueue<int>(Allocator.Persistent);
            m_OldestPendingDespawnTickByAll = new NativeReference<NetworkTick>(Allocator.Persistent);
#if UNITY_EDITOR
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            m_UpdateLen = new NativeArray<uint>(maxThreadCount, Allocator.Persistent);
            m_UpdateCounts = new NativeArray<uint>(maxThreadCount, Allocator.Persistent);
#endif

            connectionQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());

            m_ConnectionStates = new NativeList<ConnectionStateData>(256, Allocator.Persistent);
            m_ConnectionStateLookup = new NativeParallelHashMap<Entity, int>(256, Allocator.Persistent);
            m_CompressionModel = StreamCompressionModel.Default;
            m_SceneSectionHashLookup = new NativeParallelHashMap<int, ulong>(256, Allocator.Persistent);

            state.RequireForUpdate<GhostCollection>();

            m_GhostRelevancySet = new NativeParallelHashMap<RelevantGhostForConnection, int>(1024, Allocator.Persistent);
            m_ConnectionsToProcess = new NativeList<ConnectionStateData>(16, Allocator.Persistent);
            var relevancySingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostRelevancy>());
            state.EntityManager.SetName(relevancySingleton, "GhostRelevancy-Singleton");
            SystemAPI.SetSingleton(new GhostRelevancy(m_GhostRelevancySet));

            m_GhostMap = new NativeParallelHashMap<SpawnedGhost, Entity>(1024, Allocator.Persistent);
            m_FreeSpawnedGhostQueue = new NativeQueue<SpawnedGhost>(Allocator.Persistent);

            var spawnedGhostMap = state.EntityManager.CreateEntity(ComponentType.ReadWrite<SpawnedGhostEntityMap>());
            state.EntityManager.SetName(spawnedGhostMap, "SpawnedGhostEntityMapSingleton");

            SystemAPI.SetSingleton(new SpawnedGhostEntityMap{Value = m_GhostMap.AsReadOnly(), SpawnedGhostMapRW = m_GhostMap, ServerDestroyedPrespawns = m_DestroyedPrespawns, m_ServerAllocatedGhostIds = m_AllocatedGhostIds, m_ServerFreeGhostIds = m_FreeGhostIds });

#if NETCODE_DEBUG
            m_PacketLogEnableQuery = state.GetEntityQuery(ComponentType.ReadOnly<EnablePacketLogging>());
#endif

            m_GhostPreSerializer = new GhostPreSerializer(state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<PreSerializedGhost>()));

            var dataSingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostSendSystemData>());
            state.EntityManager.SetName(dataSingleton, "GhostSystemData-Singleton");
            var data = new GhostSendSystemData();
            data.Initialize();
            SystemAPI.SetSingleton(data);

#if UNITY_EDITOR
            SetupAnalyticsSingleton(state.EntityManager);
#endif

            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>();
            m_SnapshotAckFromEntity = state.GetComponentLookup<NetworkSnapshotAck>(false);
            m_GhostTypeFromEntity = state.GetComponentLookup<GhostType>(true);
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity = state.GetComponentLookup<PrefabDebugName>(true);
#endif
            m_ConnectionFromEntity = state.GetComponentLookup<NetworkStreamConnection>(true);
            m_GhostFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_SnapshotTargetFromEntity = state.GetComponentLookup<NetworkStreamSnapshotTargetSize>(true);
            m_EnablePacketLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>(false);
            m_GhostOverrideFromEntity = state.GetComponentLookup<OverrideGhostData>(true);

            m_GhostSystemStateType = state.GetComponentTypeHandle<GhostCleanup>(true);
            m_PreSerializedGhostType = state.GetComponentTypeHandle<PreSerializedGhost>(true);
            m_GhostComponentType = state.GetComponentTypeHandle<GhostInstance>();
            m_GhostOwnerComponentType = state.GetComponentTypeHandle<GhostOwner>(true);
            m_GhostChildEntityComponentType = state.GetComponentTypeHandle<GhostChildEntity>(true);
            m_PrespawnedGhostIdType = state.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_GhostTypeComponentType = state.GetComponentTypeHandle<GhostType>(true);
            m_GhostImportanceType = state.GetComponentTypeHandle<GhostImportance>();

            m_EntityType = state.GetEntityTypeHandle();
            m_GhostGroupType = state.GetBufferTypeHandle<GhostGroup>(true);
            m_LinkedEntityGroupType = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_PrespawnGhostBaselineType = state.GetBufferTypeHandle<PrespawnGhostBaseline>(true);
            m_SubsceneGhostComponentType = state.GetSharedComponentTypeHandle<SubSceneGhostComponentHash>();

            m_PrespawnGhostIdRangeFromEntity = state.GetBufferLookup<PrespawnGhostIdRange>();
            m_GhostTypeCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefab>(true);
            m_GhostComponentCollectionFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_PrespawnAckFromEntity = state.GetBufferLookup<PrespawnSectionAck>(true);
            m_PrespawnSceneLoadedFromEntity = state.GetBufferLookup<PrespawnSceneLoaded>(true);
        }

#if UNITY_EDITOR
        void SetupAnalyticsSingleton(EntityManager entityManager)
        {
            var analyticsSingleton = entityManager.CreateEntity(ComponentType.ReadWrite<GhostSendSystemAnalyticsData>());
            entityManager.SetName(analyticsSingleton, "GhostSystemAnalyticsData-Singleton");
            var analyticsData = new GhostSendSystemAnalyticsData
            {
                UpdateLenSums = m_UpdateLen,
                NumberOfUpdates = m_UpdateCounts,
            };
            SystemAPI.SetSingleton(analyticsData);
        }
#endif

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_GhostPreSerializer.Dispose();
            m_AllocatedGhostIds.Dispose();
            m_FreeGhostIds.Dispose();

            m_DestroyedPrespawns.Dispose();
            m_DestroyedPrespawnsQueue.Dispose();
            m_OldestPendingDespawnTickByAll.Dispose();
            foreach (var connectionState in m_ConnectionStates)
            {
                connectionState.Dispose();
            }
            m_ConnectionStates.Dispose();

            m_ConnectionStateLookup.Dispose();

            m_GhostRelevancySet.Dispose();
            m_ConnectionsToProcess.Dispose();

            state.Dependency.Complete(); // 等待完成以访问 Ghost 映射
            m_GhostMap.Dispose();
            m_FreeSpawnedGhostQueue.Dispose();
            m_SceneSectionHashLookup.Dispose();
#if UNITY_EDITOR
            m_UpdateLen.Dispose();
            m_UpdateCounts.Dispose();
#endif
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public NativeList<ArchetypeChunk> spawnChunks;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<GhostInstance> ghostComponentType;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;
            public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;

            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            [ReadOnly] public ComponentLookup<OverrideGhostData> ghostOverrideFromEntity;
            public NetworkTick serverTick;
            public byte forcePreSerialize;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            [ReadOnly] public ComponentLookup<PrefabDebugName> prefabNames;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [ReadOnly] public ComponentTypeHandle<GhostOwner> ghostOwnerComponentType;
#endif
            public void Execute()
            {
                // GhostSendSystem 的部分代码也可用于离线单 World Host
                // 但当前假设离开 NetworkStreamInGame 后会重置相关状态，没有连接时 Ghost 集合也会重置
                // 例如切换场景时，会在两次进入游戏之间重置全部内容，避免残留异常 Prefab 条目
                // 用户可以执行 World 迁移，过程更干净但维护成本很高
                // 可考虑将启用复制与因场景切换而重置 Ghost 集合拆分
                // 旧假设是 Ghost ID 只用于客户端与服务器间映射，没有连接时无需 Ghost ID
                // 但要访问存储在 GhostInstance 中的 GhostType，就必须先初始化 GhostInstance
                // 例如 BackupSystem 会将 GhostType 用于自身序列化
                // Ghost 生成后始终拥有 Ghost ID 也更一致，而不是仅在存在连接时才分配
                // 延迟分配节省的性能会被首个客户端连接时的集中工作抵消
                // 而且 Ghost ID 分配不应成为主要耗时，用户模拟逻辑通常成本更高
                // TODO：检查 NetworkStreamInGame 周围是否还有仅在存在客户端连接时才应执行的假设，以及其他性能影响
                // TODO：实现后检查进入游戏、退出并清空 Ghost 集合、切换场景、再次进入游戏并重建集合的流程
                // 需要确认 GhostInstance 中的 Ghost 类型是否仍一致，以及进程启动时未进入游戏的 DGS 是否正常工作

                // 以下检查无效，因为此 Job 仅在存在 GhostCollection 且 NetworkStreamInGame 为 true 时触发
                // 此检查很久以前由 Tim 在修复 LagCompensation 测试时添加
                // https://github.com/Unity-Technologies/netcode/commit/07560a4e66da43ecc88dea0d0dd81123bccf8982#diff-ecc6fdb6e44e3dc05cff13a9e5aa56ba02b1faa082c1adb80031105d79b23793
                // if (connectionState.Length == 0)
                //     return;

                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                for (int chunk = 0; chunk < spawnChunks.Length; ++chunk)
                {
                    var entities = spawnChunks[chunk].GetNativeArray(entityType);
                    var ghostTypeComponent = ghostTypeFromEntity[entities[0]];
                    int ghostType;
                    for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                    {
                        if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                            break;
                    }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (ghostType >= GhostCollection.Length)
                        throw new InvalidOperationException($"Could not find ghost type in the collection. GhostCollection length is {GhostCollection.Length}, was trying to find ghost type index {ghostType}");
#endif
                    if (ghostType >= GhostTypeCollection.Length)
                        continue; // 序列化数据尚未加载
                    var ghosts = spawnChunks[chunk].GetNativeArray(ref ghostComponentType);
                    for (var ent = 0; ent < entities.Length; ++ent)
                    {
                        var newEntitySpawnTick = serverTick;
                        var newEntityGhostId = 0;

                        if (ghostOverrideFromEntity.HasComponent(entities[ent]))
                        {
                            var overrideComponent = ghostOverrideFromEntity[entities[ent]];
                            newEntityGhostId = overrideComponent.GhostId;
                            newEntitySpawnTick = overrideComponent.SpawnTick;
                            commandBuffer.RemoveComponent<OverrideGhostData>(entities[ent]);
                        }
                        else
                        {
                            if (!freeGhostIds.TryDequeue(out newEntityGhostId))
                            {
                                newEntityGhostId = allocatedGhostIds[0];
                                allocatedGhostIds[0] = newEntityGhostId + 1;
                            }
                        }

                        if ( newEntityGhostId == 0 )
                        {
                            netDebug.LogError($"Assigning a GhostId of 0 to a Ghost. This should never happen. Has GhostId override = {ghostOverrideFromEntity.HasComponent(entities[ent])}");
                        }

                        // TODO-release：单 World Host 没有连接时不会执行此逻辑
                        // BackupSystem 假设每个实体的 GhostInstance 已初始化，用户系统通常也会有相同假设
                        // 即使没有客户端连接，服务器用户系统也可能认为已生成 Ghost 拥有初始化后的 GhostInstance
                        // 这主要影响支持离线模式的单 World Host，双 World 模式下 Host 无法真正离线，否则客户端 World 会停止更新
                        ghosts[ent] = new GhostInstance {ghostId = newEntityGhostId, ghostType = ghostType, spawnTick = newEntitySpawnTick };

                        var spawnedGhost = new SpawnedGhost
                        {
                            ghostId = newEntityGhostId,
                            spawnTick = newEntitySpawnTick
                        };
                        if (!ghostMap.TryAdd(spawnedGhost, entities[ent]))
                        {
                            netDebug.LogError(FixedString.Format("GhostID {0} already present in the ghost entity map", newEntityGhostId));
                            ghostMap[spawnedGhost] = entities[ent];
                        }

                        var ghostState = new GhostCleanup
                        {
                            ghostId = newEntityGhostId, despawnTick = NetworkTick.Invalid, spawnTick = newEntitySpawnTick
                        };
                        commandBuffer.AddComponent(entities[ent], ghostState);
                        if (forcePreSerialize == 1)
                            commandBuffer.AddComponent<PreSerializedGhost>(entities[ent]);
#if NETCODE_DEBUG
                        if (netDebug.LogLevel <= NetDebug.LogLevelType.Debug)
                        {
                            FixedString64Bytes prefabNameString = default;
                            if (prefabNames.HasComponent(GhostCollection[ghostType].GhostPrefab))
                                prefabNameString.CopyFromTruncated(prefabNames[GhostCollection[ghostType].GhostPrefab].PrefabName);
                            netDebug.DebugLog(FixedString.Format("[Spawn] GID:{0} Prefab:{1} TypeID:{2} spawnTick:{3}", newEntityGhostId, prefabNameString, ghostType, newEntitySpawnTick.ToFixedString()));
                        }
#endif
                    }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (GhostTypeCollection[ghostType].PredictionOwnerOffset != 0)
                    {
                        if (!spawnChunks[chunk].Has(ref ghostOwnerComponentType))
                        {
                            netDebug.LogError(FixedString.Format("Ghost type is owner predicted but does not have a GhostOwner {0}, {1}", ghostType, ghostTypeComponent.guid0));
                            continue;
                        }
                        if (GhostTypeCollection[ghostType].OwnerPredicted != 0)
                        {
                            // 验证实体具有 GhostOwner，且其中的值已经初始化
                            var ghostOwners = spawnChunks[chunk].GetNativeArray(ref ghostOwnerComponentType);
                            for (int ent = 0; ent < ghostOwners.Length; ++ent)
                            {
                               if (ghostOwners[ent].NetworkId == 0)
                               {
                                   netDebug.LogError("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwner.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1.");
                               }
                            }
                        }
                    }
#endif
                }
            }
        }

        [BurstCompile]
        struct SerializeJob : IJobParallelForDefer
        {
            public DynamicTypeList DynamicGhostCollectionComponentTypeList;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
            public ConcurrentDriverStore concurrentDriverStore;
            [ReadOnly] public NativeList<ArchetypeChunk> despawnChunks;
            [ReadOnly] public NativeList<ArchetypeChunk> ghostChunks;

            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            [NativeDisableParallelForRestriction] public ComponentLookup<NetworkSnapshotAck> ackFromEntity;
            [ReadOnly] public ComponentLookup<NetworkStreamConnection> connectionFromEntity;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdFromEntity;

            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostCleanup> ghostSystemStateType;
            [ReadOnly] public ComponentTypeHandle<PreSerializedGhost> preSerializedGhostType;
            [ReadOnly] public BufferTypeHandle<GhostGroup> ghostGroupType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntity> ghostChildEntityComponentType;
            [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnGhostIdType;
            [ReadOnly] public SharedComponentTypeHandle<SubSceneGhostComponentHash> subsceneHashSharedTypeHandle;

            public GhostRelevancyMode relevancyMode;
            [ReadOnly] public NativeParallelHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
            [ReadOnly] public EntityQueryMask userGlobalRelevantMask;
            [ReadOnly] public EntityQueryMask internalGlobalRelevantMask;

#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<UnsafeGhostStatsSnapshot> NetStatsSnapshotPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;
#endif
            [ReadOnly] public StreamCompressionModel compressionModel;

            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;

            public NetworkTick currentTick;
            public uint localTime;
            public float simulationTickRateIntervalMs;
            public int networkTickRateIntervalTicks;

            public PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleImportance;
            public PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate> ScaleGhostImportance;

            [ReadOnly] public DynamicSharedComponentTypeHandle ghostImportancePerChunkTypeHandle;
            [NativeDisableUnsafePtrRestriction] [ReadOnly] public IntPtr ghostImportanceDataIntPtr;
            [ReadOnly] public DynamicComponentTypeHandle ghostConnectionDataTypeHandle;
            public int ghostConnectionDataTypeSize;
            [ReadOnly] public ComponentLookup<NetworkStreamSnapshotTargetSize> snapshotTargetSizeFromEntity;
            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            [ReadOnly] public NativeArray<int> allocatedGhostIds;
            [ReadOnly] public NativeList<int> prespawnDespawns;

            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaselineTypeHandle;
            [ReadOnly] public NativeParallelHashMap<int, ulong> SubSceneHashSharedIndexMap;
            public uint CurrentSystemVersion;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            public PacketDumpLogger netDebugPacket;
            [ReadOnly] public ComponentLookup<PrefabDebugName> prefabNamesFromEntity;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<EnablePacketLogging> enableLoggingFromEntity;
            public FixedString128Bytes timestamp;
#endif

            public Entity prespawnSceneLoadedEntity;
            [ReadOnly] public BufferLookup<PrespawnSectionAck> prespawnAckFromEntity;
            [ReadOnly] public BufferLookup<PrespawnSceneLoaded> prespawnSceneLoadedFromEntity;

            Entity connectionEntity;
            ConnectionStateData.GhostStateList ghostStateData;
            int connectionIdx;

            public GhostSendSystemData systemData;

            [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotPreSerializeData;
#if UNITY_EDITOR
            [NativeDisableParallelForRestriction] public NativeArray<uint> UpdateLen;
            [NativeDisableParallelForRestriction] public NativeArray<uint> UpdateCounts;
#endif

            public unsafe void Execute(int idx)
            {
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicGhostCollectionComponentTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicGhostCollectionComponentTypeList.Length;
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                connectionIdx = idx;
                var curConnectionState = connectionState[connectionIdx];
                connectionEntity = curConnectionState.Entity;

                curConnectionState.EnsureGhostStateCapacity(allocatedGhostIds[0], allocatedGhostIds[1]);
                ghostStateData = curConnectionState.GhostStateData;
#if NETCODE_DEBUG
                netDebugPacket = curConnectionState.NetDebugPacket;
                EnablePacketLogging.InitAndFetch(connectionEntity, enableLoggingFromEntity, curConnectionState.NetDebugPacket);
#endif
                var connectionId = connectionFromEntity[connectionEntity].Value;
                var concurrent = concurrentDriverStore.GetConcurrentDriver(connectionFromEntity[connectionEntity].DriverId);
                var driver = concurrent.driver;
                var unreliablePipeline = concurrent.unreliablePipeline;
                var unreliableFragmentedPipeline = concurrent.unreliableFragmentedPipeline;
                if (driver.GetConnectionState(connectionId) != NetworkConnection.State.Connected)
                    return;

#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\n███ [GSS:SJ][{timestamp}] Connection {connectionId.ToFixedString()} on ServerTick:{currentTick.ToFixedString()}, {networkIdFromEntity[connectionEntity].ToFixedString()}\n");
#endif

                // 收集 Ghost Chunk
                s_PrioritizeChunksMarker.Begin();
                GatherGhostChunksBatch(out GhostChunksContext ctx);
                s_PrioritizeChunksMarker.End();

                // MaxIterateChunks 表示要处理的数量，即查询并尝试发送的 Chunk 数
                // MaxSendChunks 表示允许实际发送的 Chunk 数
                ctx.MaxChunksToIterate = ctx.SerialChunks->Length;
                if (systemData.MaxIterateChunks == 0 && systemData.MaxSendChunks > 0)
                    systemData.MaxIterateChunks = systemData.MaxSendChunks;
                if(systemData.MaxIterateChunks > 0)
                    ctx.MaxChunksToIterate = math.min(systemData.MaxIterateChunks, ctx.SerialChunks->Length);

#if NETCODE_DEBUG
                if (Hint.Unlikely(netDebugPacket.IsCreated))
                    netDebugPacket.Log((FixedString512Bytes) $"\tGatherGhostChunks gathered and sorted {ctx.SerialChunks->Length} of {ghostChunks.Length} chunks for ServerTick:{currentTick.ToFixedString()}! MSC:{systemData.MaxSendChunks}, MIC:{systemData.MaxIterateChunks} means iterating {ctx.MaxChunksToIterate} w/ RlvntGhosts:{ctx.TotalRelevantGhosts} (RMode:{(int) relevancyMode}), numZC:{ctx.NumZeroChangeChunks} {(int) ((float) ctx.NumZeroChangeChunks / ctx.SerialChunks->Length) * 100}%");
#endif

                // 序列化实体
                var maxMessageSize = driver.m_DriverSender.m_SendQueue.PayloadCapacity;
                int maxSnapshotSizeWithoutFragmentation = maxMessageSize - driver.MaxHeaderSize(unreliablePipeline);

                int targetSnapshotSize = maxSnapshotSizeWithoutFragmentation;
                if (snapshotTargetSizeFromEntity.TryGetComponent(connectionEntity, out var perConnectionTargetSnapshotSize))
                {
                    targetSnapshotSize = math.max(GhostSystemConstants.MinSnapshotPacketSize, perConnectionTargetSnapshotSize.Value);
                }
                else if (systemData.DefaultSnapshotPacketSize > 0)
                {
                    targetSnapshotSize = math.max(GhostSystemConstants.MinSnapshotPacketSize, systemData.DefaultSnapshotPacketSize);
                }

                if (prespawnSceneLoadedEntity != Entity.Null)
                {
                    PrespawnHelper.UpdatePrespawnAckSceneMap(ref curConnectionState,
                        prespawnSceneLoadedEntity, prespawnAckFromEntity, prespawnSceneLoadedFromEntity);
                }

                int attempt = 1;
                var serializeResult = default(SerializeEnitiesResult);
                while (serializeResult != SerializeEnitiesResult.Abort &&
                       serializeResult != SerializeEnitiesResult.Ok)
                {
                    // 请求的包大小大于 MaxMessageSize 时必须使用分片 Pipeline
                    var pipelineToUse = (targetSnapshotSize <= maxSnapshotSizeWithoutFragmentation) ? unreliablePipeline : unreliableFragmentedPipeline;
                    var result = driver.BeginSend(pipelineToUse, connectionId, out var dataStream, targetSnapshotSize);
                    if ((int)Networking.Transport.Error.StatusCode.Success == result)
                    {
                        serializeResult = SerializeEnitiesResult.Unknown;
                        try
                        {
                            ref var snapshotAck = ref ackFromEntity.GetRefRW(connectionEntity).ValueRW;
                            serializeResult = sendEntities(ref dataStream, snapshotAck, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, in ctx);
                            if (serializeResult == SerializeEnitiesResult.Ok)
                            {
                                if ((result = driver.EndSend(dataStream)) >= (int) Networking.Transport.Error.StatusCode.Success)
                                {
#if UNITY_EDITOR || NETCODE_DEBUG
                                    ref var netStatsSnapshots = ref NetStatsSnapshotPerThread.AsSpan()[ThreadIndex];
                                    netStatsSnapshots.SnapshotTotalSizeInBits += (uint)dataStream.LengthInBits;
#endif
                                    snapshotAck.CurrentSnapshotSequenceId++;
                                    snapshotAck.SnapshotPacketLoss.NumPacketsReceived++;
                                }
                                else
                                {
                                    netDebug.LogWarning($"Failed to send a snapshot to a client with EndSend error: {result}!");
                                }
                            }
                            else
                            {
                                driver.AbortSend(dataStream); // TODO：在此重置 Snapshot 统计
                            }
                        }
                        finally
                        {

                            // 非 Burst 代码的外层调用方 WorldUnmanaged 含有 try-catch
                            // 因此无论抛出何种异常，包括 InvalidProgramException，finally 都会执行
                            // Burst 代码中的 try-finally 存在限制，但此处仍会按正确顺序展开代码块
                            // 一般情况下，未处理错误和异常先由最外层 WorldUnmanaged 的 try-catch 捕获
                            // 再按逆序调用 try-finally，即执行栈展开
                            // Ghost 发送系统中有两层异常处理
                            // - 此处负责中止数据流
                            // - sendEntities 内部负责尝试恢复部分内部状态，例如 Ghost 销毁状态
                            // 最内层 finally 最先调用，并且不会中止数据流
                            if (serializeResult == SerializeEnitiesResult.Unknown)
                                driver.AbortSend(dataStream); // TODO：在此重置 Snapshot 统计
                        }
                    }
                    else
                    {
                        netDebug.LogError($"Failed to send a snapshot to a client with BeginSend error: {result}, attempt:{attempt}!");
                        if (result == (int)Networking.Transport.Error.StatusCode.NetworkPacketOverflow)
                        {
                            serializeResult = SerializeEnitiesResult.Abort;
                        }
                    }

                    if (serializeResult == SerializeEnitiesResult.Failed)
                    {
                        if (Hint.Likely(attempt < GhostSystemConstants.MaxSnapshotSendAttempts))
                        {
                            // TODO：此处仍会重新序列化全部内容，成本较高
                            // 如果当前 dataStream 连单个 Ghost 都容纳不下，不应丢弃全部已写数据
                            // 只需分配更大的 Writer 并将现有数据复制过去
                            if (Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                                netDebug.LogWarning($"PERFORMANCE: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} in attempt:{attempt} for {ctx.NetworkId.ToFixedString()}, increasing size to {targetSnapshotSize * 2} and trying again! Your configured `MaxMessageSize`:{maxMessageSize} and/or `DefaultSnapshotPacketSize`:{systemData.DefaultSnapshotPacketSize}, and/or `NetworkStreamSnapshotTargetSize`:{perConnectionTargetSnapshotSize.Value} is too small to fit even one ghost.");

                            UnityEngine.Debug.Assert(targetSnapshotSize > 0);
                            targetSnapshotSize += targetSnapshotSize;
#if NETCODE_DEBUG
                            if (Hint.Unlikely(netDebugPacket.IsCreated))
                                netDebugPacket.Log($"Send attempt {attempt} failed with targetSnapshotSize:{targetSnapshotSize}, retrying!\n");
#endif
                        }
                        else
                        {
#if NETCODE_DEBUG
                            if (Hint.Unlikely(netDebugPacket.IsCreated))
                                netDebugPacket.Log($"FATAL: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} after MaxSnapshotSendAttempts:{attempt} for {ctx.NetworkId.ToFixedString()}, aborting!\n");
#endif
                            netDebug.LogError($"FATAL: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} after MaxSnapshotSendAttempts:{attempt} for {ctx.NetworkId.ToFixedString()}, aborting!");
                            serializeResult = SerializeEnitiesResult.Abort;
                        }
                    }
                    attempt++;
                }
            }

            unsafe struct GhostChunksContext
            {
                public NetworkId NetworkId;
                public UnsafeList<PrioChunk>* SerialChunks;
                public int MaxChunksToIterate;
                public int MaxGhostsPerChunk;
                /// <summary>
                /// 估算相关 Ghost 的总数
                /// <br/>
                /// 注意：不统计尚未传入此 Job 的 Ghost Chunk，例如没有 <see cref="GhostCleanup"/> 的 Chunk
                /// 启用相关性时，也不统计尚未执行 <see cref="GhostChunkSerializer.UpdateGhostRelevancy"/> 步骤的 Ghost Chunk
                /// </summary>
                public int TotalRelevantGhosts;
                public int NumZeroChangeChunks;
            }

            unsafe SerializeEnitiesResult sendEntities(ref DataStreamWriter dataStream, NetworkSnapshotAck snapshotAckCopy,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength, in GhostChunksContext ctx)
            {
                var serializerState = new GhostSerializerState
                {
                    GhostFromEntity = ghostFromEntity
                };
                var ackTick = snapshotAckCopy.LastReceivedSnapshotByRemote;

                // Snapshot 包头
                dataStream.WriteByte((byte) NetworkStreamProtocol.Snapshot);

                dataStream.WriteUInt(localTime);
                uint returnTime = snapshotAckCopy.CalculateReturnTime(localTime);
                dataStream.WriteUInt(returnTime);
                dataStream.WriteInt(snapshotAckCopy.ServerCommandAge);
                dataStream.WriteByte(snapshotAckCopy.CurrentSnapshotSequenceId);
                dataStream.WriteUInt(currentTick.SerializedData);

                // 写入客户端尚未 Ack 的 Ghost Snapshot 列表
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                uint numLoadedPrefabs = snapshotAckCopy.NumLoadedPrefabs;
                if (numLoadedPrefabs > (uint)GhostCollection.Length)
                {
                    // 远端已接收 Ghost 的状态可能尚未更新
                    numLoadedPrefabs = 0;
                    // 覆盖 Snapshot Ack 副本，使 GhostChunkSerializer 可以跳过此检查
                    snapshotAckCopy.NumLoadedPrefabs = 0;
                }
                uint numNewPrefabs = math.min((uint)GhostCollection.Length - numLoadedPrefabs, GhostSystemConstants.MaxNewPrefabsPerSnapshot);
                dataStream.WritePackedUInt(numNewPrefabs, compressionModel);

#if NETCODE_DEBUG
                FixedString512Bytes debugLog = default;
                if (netDebugPacket.IsCreated)
                {
                    debugLog = $"\n\t[SendEntities] Protocol:{(byte) NetworkStreamProtocol.Snapshot} LocalTime:{localTime} ReturnTime:{returnTime} CommandAge:{snapshotAckCopy.ServerCommandAge} | ";
                    debugLog.Append((FixedString512Bytes)$"Tick:{currentTick.ToFixedString()} SSId:{snapshotAckCopy.CurrentSnapshotSequenceId} | NewPrefabs:{numNewPrefabs}, LoadedPrefabs:{numLoadedPrefabs}\n");
                }
#endif

                if (numNewPrefabs > 0)
                {
                    dataStream.WritePackedUInt(numLoadedPrefabs, compressionModel);
                    int prefabNum = (int)numLoadedPrefabs;
                    for (var i = 0; i < numNewPrefabs; ++i)
                    {
                        var ghostPrefab = GhostCollection[prefabNum];
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid0);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid1);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid2);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid3);
                        dataStream.WriteULong(ghostPrefab.Hash);
#if NETCODE_DEBUG
                        if (netDebugPacket.IsCreated)
                        {
                            debugLog.Append(FixedString.Format("\t NewPrefab:{0}-{1}-{2}-{3}",
                                ghostPrefab.GhostType.guid0, ghostPrefab.GhostType.guid1,
                                ghostPrefab.GhostType.guid2,
                                ghostPrefab.GhostType.guid3));
                            debugLog.Append(FixedString.Format(" Hash:{0} '{1}'\n", ghostPrefab.Hash, prefabNamesFromEntity[ghostPrefab.GhostPrefab].PrefabName));
                        }
#endif
                        ++prefabNum;
                    }
                }

                dataStream.WritePackedUInt((uint)ctx.TotalRelevantGhosts, compressionModel);
                var lenWriter = dataStream;
                dataStream.WriteUShort(0); // 为 despawnLen 预留空间
                dataStream.WriteUShort(0); // 为 totalSentEntities 预留空间

                // 写入最后一个已 Ack 包之后销毁的全部 Ghost 列表，并返回写入的 Ghost ID 数量
#if UNITY_EDITOR || NETCODE_DEBUG
                int startPos = dataStream.LengthInBits;
#endif
                var pendingGhostDespawns = connectionState[connectionIdx].PendingDespawns;
                uint despawnLen = PendingGhostDespawn.WriteDespawns(currentTick, ref *pendingGhostDespawns, ref ghostStateData,
                    despawnChunks, ref snapshotAckCopy, ghostSystemStateType, ref dataStream, ref compressionModel,
                    ref connectionState[connectionIdx].NewLoadedPrespawnRanges, ref prespawnDespawns,
                    ref systemData
#if NETCODE_DEBUG
                    , ref netDebugPacket
#endif
                    );

                if (dataStream.HasFailedWrites)
                {
                    RevertDespawnGhostState();
#if NETCODE_DEBUG
                    if(netDebugPacket.IsCreated)
                        netDebugPacket.Log((FixedString128Bytes)"█ >> Failed! HasFailedWrites before even serializing chunks!\n");
#endif
                    return SerializeEnitiesResult.Failed;
                }
#if UNITY_EDITOR || NETCODE_DEBUG

                ref var netStatsSnapshots = ref NetStatsSnapshotPerThread.AsSpan()[ThreadIndex];
                netStatsSnapshots.DespawnCount += despawnLen;
                netStatsSnapshots.DestroySizeInBits += (uint) (dataStream.LengthInBits - startPos);
                startPos = dataStream.LengthInBits;
#endif

                uint totalSentEntities = 0;
                uint totalSentChunks = 0;
                bool didFillPacket = false;
                var serializerData = new GhostChunkSerializer
                {
                    GhostComponentCollection = GhostComponentCollection,
                    GhostTypeCollection = GhostTypeCollection,
                    GhostComponentIndex = GhostComponentIndex,
                    PrespawnIndexType = prespawnGhostIdType,
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    prespawnBaselineTypeHandle = prespawnBaselineTypeHandle,
                    entityType = entityType,
                    ghostComponentType = ghostComponentType,
                    ghostSystemStateType = ghostSystemStateType,
                    preSerializedGhostType = preSerializedGhostType,
                    ghostChildEntityComponentType = ghostChildEntityComponentType,
                    ghostGroupType = ghostGroupType,
                    snapshotAck = snapshotAckCopy,
                    chunkSerializationData = *connectionState[connectionIdx].SerializationState,
                    pendingDespawns = pendingGhostDespawns,
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    ghostChunkComponentTypesLength = ghostChunkComponentTypesLength,
                    currentTick = currentTick,
                    // 由于只在此间隔发送 Snapshot，需要加入 networkTickRateIntervalTicks，这会人为增大预期 Snapshot RTT
                    expectedSnapshotRttInSimTicks = networkTickRateIntervalTicks + math.max(Mathf.CeilToInt(snapshotAckCopy.EstimatedRTT / simulationTickRateIntervalMs), networkTickRateIntervalTicks),
                    compressionModel = compressionModel,
                    serializerState = serializerState,
                    NetworkId = ctx.NetworkId.Value,
                    relevantGhostForConnection = relevantGhostForConnection,
                    relevancyMode = relevancyMode,
                    userGlobalRelevantMask = userGlobalRelevantMask,
                    internalGlobalRelevantMask = internalGlobalRelevantMask,
                    ghostStateData = ghostStateData,
                    CurrentSystemVersion = CurrentSystemVersion,

                    netDebug = netDebug,
#if NETCODE_DEBUG
                    netDebugPacket = netDebugPacket,
                    netDebugPacketResult = default,
                    netDebugPacketDebug = default,
#endif
                    systemData = systemData,
                    SnapshotPreSerializeData = SnapshotPreSerializeData,
                };
                // 临时流使用较大的初始值，以规避当前序列化逻辑的主要问题
                // Chunk 无法放入当前临时流时，会执行多轮完整序列化
                // 以下情况可能触发此问题
                // - 存在大型 Ghost，即组件或 Buffer 很大
                // - 每个 Chunk 包含大量中小型 Ghost，例如超过 30 到 40 个
                // 由于所有组件临时数据都按 32 位对齐，实际消耗可能达到临时流大小的 2 到 3 倍
                // 发生后会反复重新获取全部数据，包括子实体数据，并重新尝试
                // 此过程极慢，至少分配 8 或 16 KB 而不是 1 个 MTU，可确保问题不发生或极少发生
                // 许多场景中可直接获得 2 到 3 倍性能提升
                // 当前选择略大的 8 KB Buffer，在多种场景下整体提升良好
                // 可通过 GhostSendSystemData 调整此参数，以适配具体游戏
                var streamCapacity = systemData.UseCustomSerializer == 0
                    ? math.max(systemData.TempStreamInitialSize, dataStream.Capacity)
                    : dataStream.Capacity;
                serializerData.AllocateTempData(ctx.MaxGhostsPerChunk, streamCapacity);

                int pc = 0;
                for (; pc < ctx.MaxChunksToIterate; ++pc)
                {
                    var chunk = ctx.SerialChunks->ElementAt(pc).chunk;
                    var ghostType = ctx.SerialChunks->ElementAt(pc).ghostType;
#if NETCODE_DEBUG
                    serializerData.componentStats = netStatsSnapshots.PerGhostTypeStatsListRefRW.ElementAt(ghostType).PerComponentStatsList;
                    serializerData.ghostTypeName = default;
                    if (netDebugPacket.IsCreated)
                    {
                        if (prefabNamesFromEntity.HasComponent(GhostCollection[ghostType].GhostPrefab))
                            serializerData.ghostTypeName.Append(
                                prefabNamesFromEntity[GhostCollection[ghostType].GhostPrefab].PrefabName);
                    }
#endif

                    // 不发送客户端尚未 Ack 对应 Ghost 类型的实体
                    // TODO：考虑将此检查提前到 GatherGhostChunksBatch 阶段
                    if (ghostType >= numLoadedPrefabs)
                    {
#if NETCODE_DEBUG
                        if(netDebugPacket.IsCreated)
                            netDebugPacket.Log(FixedString.Format(
                                "\t\tSkipping {0} as client has not acked prefab.",
                                serializerData.ghostTypeName));
#endif
                        continue;
                    }

                    var serializeResult = default(SerializeEnitiesResult);
                    uint thisChunkSentEntities;
                    try
                    {
                        serializeResult = serializerData.SerializeChunk(ctx.SerialChunks->ElementAt(pc), ref dataStream,
                            out thisChunkSentEntities, ref didFillPacket);
                    }
                    finally
                    {
                        // 结果未知时，SerializeChunk 内部可能已抛出异常
                        if (serializeResult == SerializeEnitiesResult.Unknown)
                        {
                            // 不在此中止数据流，最外层循环会负责中止
                            RevertDespawnGhostState();
                        }
                    }

                    if (thisChunkSentEntities > 0)
                    {
                        totalSentChunks++;
                        totalSentEntities += thisChunkSentEntities;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(serializeResult == SerializeEnitiesResult.Ok);
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                        ref var perGhostTypeStats = ref netStatsSnapshots.PerGhostTypeStatsListRefRW.ElementAt(ghostType);
                        perGhostTypeStats.EntityCount += thisChunkSentEntities;
                        perGhostTypeStats.SizeInBits += (uint)(dataStream.LengthInBits - startPos);
                        perGhostTypeStats.ChunkCount += 1;
                        startPos = dataStream.LengthInBits;
#endif
                    }

#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                    {
                        serializerData.PacketDumpFlush();
                        netDebugPacket.Log((FixedString512Bytes)$"\n\t[{chunk.SequenceNumber}] {ToFixedString(serializeResult)} | +{thisChunkSentEntities} | pc:{pc}/{ctx.MaxChunksToIterate}/{ctx.SerialChunks->Length} | {serializerData.netDebugPacketResult}\n");
                        serializerData.netDebugPacketResult.Clear();
                    }
#endif

                    // 停止遍历 Chunk 的条件
                    if (serializeResult != SerializeEnitiesResult.Ok || didFillPacket)
                        break;
                    if (thisChunkSentEntities > 0 && systemData.MaxSendChunks > 0 && totalSentChunks >= systemData.MaxSendChunks)
                    {
#if NETCODE_DEBUG
                        if(netDebugPacket.IsCreated)
                            netDebugPacket.Log((FixedString512Bytes)$"\tHit MaxSendChunks!");
#endif
                        break;
                    }
                }

#if NETCODE_DEBUG
                if (systemData.MaxIterateChunks != 0 && pc >= systemData.MaxIterateChunks - 1 && netDebugPacket.IsCreated)
                    netDebugPacket.Log((FixedString64Bytes) $"\tHit MaxIterateChunks:{systemData.MaxIterateChunks}!");
#endif

                if (Hint.Unlikely(dataStream.HasFailedWrites))
                {
                    RevertDespawnGhostState();
                    netDebug.LogError("Size limitation on snapshot did not prevent all errors!");
#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                        netDebugPacket.Log((FixedString128Bytes) $"█ >> Aborted! Size limitation on snapshot did not prevent all errors!");
#endif
                    return SerializeEnitiesResult.Abort;
                }

                dataStream.Flush();
                lenWriter.WriteUShort((ushort)despawnLen);
                lenWriter.WriteUShort((ushort)totalSentEntities);
#if UNITY_EDITOR
                if (totalSentEntities > 0)
                {
                    UpdateLen[ThreadIndex] += totalSentEntities;
                    UpdateCounts[ThreadIndex] += 1;
                }
#endif
                if (didFillPacket && totalSentEntities == 0)
                {
                    RevertDespawnGhostState();
#if NETCODE_DEBUG
                    if(netDebugPacket.IsCreated)
                        netDebugPacket.Log($"█ >> Failed to even write ONE ghost to the snapshot!");
#endif
                    return SerializeEnitiesResult.Failed;
                }
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"█ >> {dataStream.Length}B on ServerTick:{currentTick.ToFixedString()} SSId:{snapshotAckCopy.CurrentSnapshotSequenceId} | TotalDespawns:{despawnLen} TotalUpdates:{totalSentEntities} via NumChunks:{totalSentChunks}, DidFill:{didFillPacket}, SSId:{snapshotAckCopy.CurrentSnapshotSequenceId}\n\n");
#endif
                return SerializeEnitiesResult.Ok;
            }

            unsafe void RevertDespawnGhostState()
            {
                // TODO：检查其他有状态数据是否也采用了正确的恢复方式
                PendingGhostDespawn.RevertSnapshotDespawnWrites(ref *connectionState[connectionIdx].PendingDespawns, currentTick);
            }

            int FindGhostTypeIndex(Entity ent)
            {
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                int ghostType;
                var ghostTypeComponent = ghostTypeFromEntity[ent];
                for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                {
                    if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                        break;
                }
                if (ghostType >= GhostCollection.Length)
                {
                    netDebug.LogError("Could not find ghost type in the collection");
                    return -1;
                }
                return ghostType;
            }

            static unsafe IntPtr GetComponentPtrInChunk(
                EntityStorageInfo storageInfo,
                DynamicComponentTypeHandle connectionDataTypeHandle,
                int typeSize)
            {
                var ptr = (byte*)storageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref connectionDataTypeHandle, typeSize).GetUnsafeReadOnlyPtr();
                ptr += typeSize * storageInfo.IndexInChunk;
                return (IntPtr)ptr;
            }

            unsafe bool TryGetChunkStateOrNew(ArchetypeChunk ghostChunk,
                ref UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState> chunkStates,
                out GhostChunkSerializationState chunkState)
            {
                using var __ = s_TryGetChunkStateOrNewMarker.Auto();

                if (chunkStates.TryGetValue(ghostChunk, out chunkState))
                {
                    if (chunkState.sequenceNumber == ghostChunk.SequenceNumber)
                    {
                        return true;
                    }

                    chunkState.FreeSnapshotData();
                    chunkStates.Remove(ghostChunk);
                }

                var ghosts = ghostChunk.GetComponentDataPtrRO(ref ghostComponentType);
                if (!TryGetChunkGhostType(ghostChunk, ghosts[0], out var chunkGhostType))
                {
                    return false;
                }

                chunkState.ghostType = chunkGhostType;
                chunkState.sequenceNumber = ghostChunk.SequenceNumber;
                ref readonly var prefabSerializer = ref GhostTypeCollection.ElementAtRO(chunkState.ghostType);
                int serializerDataSize = prefabSerializer.SnapshotSize;
                chunkState.baseImportance = (ushort) math.max(1, prefabSerializer.BaseImportance);
                chunkState.maxSendRateAsSimTickInterval = prefabSerializer.MaxSendRateAsSimTickInterval;
                chunkState.AllocateSnapshotData(serializerDataSize, ghostChunk.Capacity);
                var importanceTick = currentTick;
                // 首次发送不应受 MinSendImportance 或 MaxSendRate 阈值限制或延后
                // 可以合理假设每个新 Ghost 都希望立即复制
                // 因此 FirstSendImportanceMultiplier 主要控制低 Importance Ghost 类型首次发送的偏置程度
                // 例如树木的首次发送相对于玩家等高 Importance 现有 Ghost 的再次发送应提高多少优先级
                var maxResendIntervalTicks = math.max((uint) (systemData.MinSendImportance / chunkState.baseImportance), chunkState.maxSendRateAsSimTickInterval);
                importanceTick.Subtract((uint) (systemData.FirstSendImportanceMultiplier * systemData.IrrelevantImportanceDownScale * maxResendIntervalTicks));
                chunkState.SetLastFullUpdate(importanceTick);
                // 启用相关性时，首次估算的相关 Ghost 数必须为 0
                // 否则此连接看到的每个新 Chunk 都会让 GhostCount 单例在一个 Tick 内突增，即使其中没有相关 Ghost
                // 为规避此问题，写入流时必须使用 math.max(relevantGhostCount, actuallySentGhostCount)
                // 这会触发不相关内容的降权，因此上面已将其计入
                var relevancyEnabled = relevancyMode != GhostRelevancyMode.Disabled;
                var numRelevant = relevancyEnabled ? 0 : ghostChunk.Count;
                chunkState.SetNumRelevant(numRelevant, ghostChunk);

                chunkStates.TryAdd(ghostChunk, chunkState);
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tChunk {ghostChunk.SequenceNumber}, TypeID:{chunkState.ghostType} archetype changed, allocating new one! LastUp:{chunkState.GetLastUpdate().ToFixedString()}, MSR:{chunkState.maxSendRateAsSimTickInterval}!");
#endif
                return true;
            }

            bool TryGetChunkGhostType(ArchetypeChunk ghostChunk, in GhostInstance ghost, out int chunkGhostType)
            {
                chunkGhostType = ghost.ghostType;
                // 预生成 Ghost 可能尚无正确的 Ghost 类型索引，因此在此为其计算
                if (chunkGhostType < 0)
                {
                    var ghostEntity = ghostChunk.GetNativeArray(entityType)[0];
                    chunkGhostType = FindGhostTypeIndex(ghostEntity);
                    if (chunkGhostType < 0)
                    {
                        return false;
                    }
                }

                return chunkGhostType < GhostTypeCollection.Length;
            }

            static bool TryGetComponentPtrInChunk(EntityStorageInfo connectionChunkInfo, DynamicComponentTypeHandle typeHandle, int typeSize, out IntPtr componentPtrInChunk)
            {
                var connectionHasType = connectionChunkInfo.Chunk.Has(ref typeHandle);
                componentPtrInChunk = connectionHasType ? GetComponentPtrInChunk(connectionChunkInfo, typeHandle, typeSize) : default;
                return connectionHasType;
            }

            /// <summary>
            /// 收集所有可序列化并发送的 Chunk，并对列表排序，使其他系统按优先级获取
            /// 同时清理映射中过期的 Ghost 状态，并为新 Chunk 创建存储 Buffer
            /// 确保执行后所有 Chunk 均处于有效状态
            /// </summary>
            unsafe void GatherGhostChunksBatch(out GhostChunksContext ctx)
            {
                var prioChunksRef = connectionState[connectionIdx].PrioChunksPtr;
                prioChunksRef->Clear();
                ctx = new GhostChunksContext
                {
                    SerialChunks = prioChunksRef,
                    MaxGhostsPerChunk = 0,
                    TotalRelevantGhosts = 0,
                    NumZeroChangeChunks = 0,
                    NetworkId = networkIdFromEntity[connectionEntity],
                };
                var connectionChunkInfo = childEntityLookup[connectionEntity];
                var connectionHasConnectionData = TryGetComponentPtrInChunk(connectionChunkInfo, ghostConnectionDataTypeHandle, ghostConnectionDataTypeSize, out var connectionDataPtr);
                var chunkStates = connectionState[connectionIdx].SerializationState;

                for (int chunk = 0; chunk < ghostChunks.Length; ++chunk)
                {
                    var ghostChunk = ghostChunks[chunk];
                    if (!TryGetChunkStateOrNew(ghostChunk, ref *chunkStates, out var chunkState))
                    {
                        PacketDumpSkippedNoChunkState(ghostChunk);
                        continue;
                    }

                    chunkState.SetLastValidTick(currentTick);
                    ctx.TotalRelevantGhosts += chunkState.GetNumRelevant();
                    ctx.NumZeroChangeChunks += chunkState.GetFirstZeroChangeVersion() != 0 ? 1 : 0;
                    ctx.MaxGhostsPerChunk = math.max(ctx.MaxGhostsPerChunk, ghostChunk.Count);

                    // 注意：实体结构变更会使 Importance 和 MaxSendRate 状态完全失效
                    var ticksSinceLastSent = currentTick.TicksSince(chunkState.GetLastUpdate());
                    var allIrrelevant = chunkState.GetAllIrrelevant();
                    var maxSendRate = math.select(chunkState.maxSendRateAsSimTickInterval, chunkState.maxSendRateAsSimTickInterval * systemData.IrrelevantImportanceDownScale, allIrrelevant);
                    if (ticksSinceLastSent < maxSendRate)
                    {
                        PacketDumpSkippedMaxSendRate(ghostChunk, ticksSinceLastSent, maxSendRate);
                        continue;
                    }

                    // 只有客户端已加载并 Ack 对应 SubScene 时，才应考虑预生成 Ghost Chunk
                    if (ghostChunk.Has(ref prespawnGhostIdType))
                    {
                        var ackedPrespawnSceneMap = connectionState[connectionIdx].AckedPrespawnSceneMap;
                        // 根据 Shared Component 索引获取 SubScene Hash
                        var sharedComponentIndex = ghostChunk.GetSharedComponentIndex(subsceneHashSharedTypeHandle);
                        var hash = SubSceneHashSharedIndexMap[sharedComponentIndex];
                        // 客户端尚未 Ack 或请求流式加载该 SubScene 时跳过此 Chunk
                        if (!ackedPrespawnSceneMap.ContainsKey(hash))
                        {
                            PacketDumpSkippedPrespawnAndSceneLoadNotYetAcked(ghostChunk, hash);
                            continue;
                        }
                    }

                    if (ghostChunk.Has(ref ghostChildEntityComponentType))
                        continue;

                    var chunkPriority = chunkState.baseImportance * ticksSinceLastSent;
                    if (allIrrelevant)
                        chunkPriority /= systemData.IrrelevantImportanceDownScale;
                    if (chunkPriority < systemData.MinSendImportance)
                    {
                        PacketDumpSkippedMinSendImportance(ghostChunk, chunkPriority);
                        continue;
                    }

                    prioChunksRef->Add(new PrioChunk
                    {
                        chunk = ghostChunk,
                        priority = chunkPriority * systemData.m_ImportanceScalingMultiplier,
                        isRelevant = relevancyMode != GhostRelevancyMode.SetIsRelevant,
                        startIndex = chunkState.GetStartIndex(),
                        ghostType = chunkState.ghostType,
                    });
                }

                // Importance 缩放
#if NETCODE_DEBUG
                var numChunksCulled = 0;
#endif
                var hasBatched = BatchScaleImportance.Ptr.IsCreated;
                var hasNonBatched = ScaleGhostImportance.Ptr.IsCreated;
                var runImportanceScaling = connectionHasConnectionData && (hasBatched || hasNonBatched);
                if (runImportanceScaling)
                {
                    if (hasBatched)
                    {
                        var func = (delegate *unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, UnsafeList<PrioChunk>*, void>)BatchScaleImportance.Ptr.Value;
                        func(connectionDataPtr, ghostImportanceDataIntPtr,
                            GhostComponentSerializer.IntPtrCast(ref ghostImportancePerChunkTypeHandle),
                            prioChunksRef);
                    }
                    else
                    {
                        for (int i = 0; i < prioChunksRef->Length; ++i)
                        {
                            ref var serialChunk = ref prioChunksRef->ElementAt(i);
                            if (!serialChunk.chunk.Has(ref ghostImportancePerChunkTypeHandle)) continue;
                            IntPtr chunkTile = new IntPtr(serialChunk.chunk.GetDynamicSharedComponentDataAddress(ref ghostImportancePerChunkTypeHandle));
                            var func = (delegate *unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int>)ScaleGhostImportance.Ptr.Value;
                            serialChunk.priority = func(connectionDataPtr, ghostImportanceDataIntPtr, chunkTile, serialChunk.priority);
                        }
                    }

                    if (systemData.MinDistanceScaledSendImportance > 0)
                    {
                        var chunk = 0;

                        while(chunk < prioChunksRef->Length)
                        {
                            if (prioChunksRef->ElementAt(chunk).priority < systemData.MinDistanceScaledSendImportance)
                            {
                                prioChunksRef->RemoveAtSwapBack(chunk);
#if NETCODE_DEBUG
                                numChunksCulled++;
#endif
                            }
                            else
                            {
                                ++chunk;
                            }
                        }
                    }
                }

                prioChunksRef->Sort();

#if NETCODE_DEBUG
                PacketDumpAddedChunksAndGhostImportance(ctx.SerialChunks, runImportanceScaling, numChunksCulled, connectionHasConnectionData, hasBatched, hasNonBatched);
#endif
            }

#if NETCODE_DEBUG
            private static FixedString32Bytes ToFixedString(SerializeEnitiesResult serializeResult)
            {
                return serializeResult switch
                {
                    SerializeEnitiesResult.Ok => "Ok",
                    SerializeEnitiesResult.Failed => "Fail",
                    SerializeEnitiesResult.Abort => "Abort",
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
#endif
            [Conditional("NETCODE_DEBUG")]
            private unsafe void PacketDumpAddedChunksAndGhostImportance(in UnsafeList<PrioChunk>* serialChunks, bool runImportanceScaling, int numChunksCulled, bool connectionHasConnectionData, bool hasBatched, bool hasNonBatched)
            {
#if NETCODE_DEBUG
                if (netDebugPacket.IsCreated)
                {
                    for (int i = 0; i < serialChunks->Length; ++i)
                    {
                        netDebugPacket.Log($"\tAdded {serialChunks->ElementAt(i).chunk.SequenceNumber} TypeID:{serialChunks->ElementAt(i).ghostType} Priority:{serialChunks->ElementAt(i).priority}");
                    }

                    FixedString64Bytes res = runImportanceScaling ? $"ran & culled {numChunksCulled} chunks!" : "disabled!";
                    netDebugPacket.Log($"\n\tGhostImportance(connHasData:{connectionHasConnectionData}, batched:{hasBatched}, nonBatched:{hasNonBatched}) {res}");
                }
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedMinSendImportance(in ArchetypeChunk ghostChunk, int chunkPriority)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as chunkPriority:{chunkPriority} < minSendImportance:{systemData.MinSendImportance}");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedPrespawnAndSceneLoadNotYetAcked(in ArchetypeChunk ghostChunk, ulong hash)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as it's a prespawn, and scene {NetDebug.PrintHex(hash)} not yet acked by client");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedMaxSendRate(in ArchetypeChunk ghostChunk, int ticksSinceLastSent, int maxSendRate)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as {ticksSinceLastSent}<MSR:{maxSendRate}");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedNoChunkState(in ArchetypeChunk ghostChunk)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as no chunkState");
#endif
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (networkTime.NumPredictedTicksExpected == 0)
                // TODO：考虑在非 Tick 帧中交错发送
                // 例如 120 FPS、每秒 60 Tick、每秒发送 30 或 60 次时，可在非 Tick 帧发送二分之一或四分之一连接
                // 让发送负载随时间更平滑，轮询逻辑还需适配单 World Host
                return;
            var systemData = SystemAPI.GetSingleton<GhostSendSystemData>();
#if UNITY_EDITOR || NETCODE_DEBUG
            ref var snapshotStatsSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
            var numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs;
            snapshotStatsSingleton.ResetWriter(numLoadedPrefabs);
            snapshotStatsSingleton.MainStatsWrite.Tick = networkTime.ServerTick;
#endif
            // 计算本帧应发送多少次状态更新
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            var netTickInterval =
                (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            var sendThisTick = tickRate.SendSnapshotsForCatchUpTicks || !networkTime.IsCatchUpTick;
            if (sendThisTick)
                ++m_SentSnapshots;

            // 确保连接列表和连接状态为最新
            var connections = connectionQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var connectionHandle);

            var relevancySingleton = SystemAPI.GetSingleton<GhostRelevancy>();
            var relevancyMode = relevancySingleton.GhostRelevancyMode;
            EntityQueryMask userGlobalRelevantQueryMask = netcodeEmptyQuery;
            if (relevancySingleton.DefaultRelevancyQuery != default)
                userGlobalRelevantQueryMask = relevancySingleton.DefaultRelevancyQuery.GetEntityQueryMask();

            bool relevancyEnabled = (relevancyMode != GhostRelevancyMode.Disabled);
            // 查找所有客户端均已 Ack 的最新 Tick，并清理在此之前销毁的全部 Ghost
            var currentTick = networkTime.ServerTick;

            // 设置本帧需要清理的连接
            // 此逻辑使用上一帧的长度，因此部分情况下可以跳过连接更新
            if (m_ConnectionStates.Length > 0)
                m_CurrentCleanupConnectionState = (m_CurrentCleanupConnectionState + systemData.CleanupConnectionStatePerTick) % m_ConnectionStates.Length;
            else
                m_CurrentCleanupConnectionState = 0;

            // 查找所有连接均已接收的最新 Tick
            m_OldestPendingDespawnTickByAll.Value = currentTick;
            var connectionsToProcess = m_ConnectionsToProcess;
            connectionsToProcess.Clear();
            m_NetworkIdFromEntity.Update(ref state);
            k_Scheduling.Begin();
            state.Dependency = new UpdateConnectionsJob()
            {
                Connections = connections,
                ConnectionStateLookup = m_ConnectionStateLookup,
                ConnectionStates = m_ConnectionStates,
                ConnectionsToProcess = connectionsToProcess,
                OldestPendingDespawnTickByAll = m_OldestPendingDespawnTickByAll,
                NetTickInterval = netTickInterval,
                NetworkIdFromEntity = m_NetworkIdFromEntity,
                SendThisTick = sendThisTick ? (byte)1 : (byte)0,
                SentSnapshots = m_SentSnapshots,
            }.Schedule(JobHandle.CombineDependencies(state.Dependency, connectionHandle));
            k_Scheduling.End();

#if NETCODE_DEBUG
            FixedString128Bytes packetDumpTimestamp = default;
            if (!m_PacketLogEnableQuery.IsEmptyIgnoreFilter)
            {
                state.CompleteDependency();
                NetDebugInterop.GetTimestampWithTick(currentTick, out packetDumpTimestamp);
                FixedString128Bytes worldNameFixed = state.WorldUnmanaged.Name;

                foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>()
                    .WithAll<EnablePacketLogging, NetworkStreamConnection, NetworkStreamInGame>()
                    .WithAll<NetworkStreamInGame>().WithEntityAccess())
                {
                    if (!m_ConnectionStateLookup.ContainsKey(entity))
                        continue;

                    var conState = m_ConnectionStates[m_ConnectionStateLookup[entity]];
                    if (conState.NetDebugPacket.IsCreated)
                        continue;

                    NetDebugInterop.InitDebugPacketIfNotCreated(ref conState.NetDebugPacket, m_LogFolder, worldNameFixed, id.ValueRO.Value);
                    m_ConnectionStates[m_ConnectionStateLookup[entity]] = conState;
                    // 在传给序列化 Job 的列表中查找连接状态，并替换为更新后的版本
                    for (int i = 0; i < connectionsToProcess.Length; ++i)
                    {
                        if (connectionsToProcess[i].Entity != entity)
                        {
                            continue;
                        }
                        connectionsToProcess[i] = conState;
                        break;
                    }
                }
            }
#endif

            // 准备 Command Buffer
            EntityCommandBuffer commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var commandBufferConcurrent = commandBuffer.AsParallelWriter();

            // 设置 Ghost 销毁 Tick，并清理已经销毁且被所有连接 Ack 的 Ghost
            var freeGhostIds = m_FreeGhostIds.AsParallelWriter();
            var prespawnDespawn = m_DestroyedPrespawnsQueue.AsParallelWriter();
            var freeSpawnedGhosts = m_FreeSpawnedGhostQueue.AsParallelWriter();
            m_PrespawnGhostIdRangeFromEntity.Update(ref state);
            var prespawnIdRanges = m_PrespawnGhostIdRangeFromEntity[SystemAPI.GetSingletonEntity<PrespawnGhostIdRange>()];
            k_Scheduling.Begin();
            state.Dependency = new GhostDespawnParallelJob
            {
                CommandBufferConcurrent = commandBufferConcurrent,
                CurrentTick = currentTick,
                OldestPendingDespawnTickByAll = m_OldestPendingDespawnTickByAll,
                FreeGhostIds = freeGhostIds,
                FreeSpawnedGhosts = freeSpawnedGhosts,
                GhostMap = m_GhostMap,
                PrespawnDespawn = prespawnDespawn,
                PrespawnIdRanges = prespawnIdRanges,
            }.ScheduleParallel(ghostDespawnQuery, state.Dependency);
            k_Scheduling.End();

            // 将 Ghost 清理过程写入并行队列的已销毁实体复制到单一列表
            // 并从映射释放已销毁 Ghost
            k_Scheduling.Begin();
            state.Dependency = new GhostDespawnSingleJob
            {
                DespawnList = m_DestroyedPrespawns,
                DespawnQueue = m_DestroyedPrespawnsQueue,
                FreeSpawnQueue = m_FreeSpawnedGhostQueue,
                GhostMap = m_GhostMap,
            }.Schedule(state.Dependency);
            k_Scheduling.End();

            // Ghost 集合尚未初始化时，发送系统无法处理任何 Ghost
            if (!SystemAPI.GetSingleton<GhostCollection>().IsInGame)
            {
                return;
            }

            // 提取所有新生成 Ghost 并设置其 Ghost ID
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();
            var spawnChunks = ghostSpawnQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var spawnChunkHandle);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity.Update(ref state);
#endif
            m_GhostTypeFromEntity.Update(ref state);
            m_GhostComponentType.Update(ref state);
            m_GhostOwnerComponentType.Update(ref state);
            m_EntityType.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_GhostOverrideFromEntity.Update(ref state);
            // SpawnJob 分配 Ghost ID 和 Tick，并通过 Cleanup Component 跟踪 Ghost
            // 如果 Ghost Chunk 的 GhostType 尚未由 GhostCollectionSystem 处理，则跳过该 Chunk
            // 但这会使实体暂时处于数据尚未设置的中间状态
            // 因此序列化 Job 通常必须检查 Chunk 是否已经添加 Cleanup Component
            // 以确保数据已正确设置
            var spawnJob = new SpawnGhostJob
            {
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                spawnChunks = spawnChunks,
                entityType = m_EntityType,
                ghostComponentType = m_GhostComponentType,
                freeGhostIds = m_FreeGhostIds,
                allocatedGhostIds = m_AllocatedGhostIds,
                commandBuffer = commandBuffer,
                ghostMap = m_GhostMap,
                ghostTypeFromEntity = m_GhostTypeFromEntity,
                ghostOverrideFromEntity = m_GhostOverrideFromEntity,
                serverTick = currentTick,
                forcePreSerialize = (byte) (systemData.ForcePreSerialize ? 1 : 0),
                netDebug = netDebug,
#if NETCODE_DEBUG
                prefabNames = m_PrefabDebugNameFromEntity,
#endif
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ghostOwnerComponentType = m_GhostOwnerComponentType
#endif
            };
            k_Scheduling.Begin();
            state.Dependency = spawnJob.Schedule(JobHandle.CombineDependencies(state.Dependency, spawnChunkHandle));
            k_Scheduling.End();

            // 为 Ghost 和已销毁 Ghost 创建 Chunk 数组
            var despawnChunks = ghostDespawnQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var despawnChunksHandle);
            var ghostChunks = ghostQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var ghostChunksHandle);
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, despawnChunksHandle, ghostChunksHandle);

            SystemAPI.TryGetSingletonEntity<PrespawnSceneLoaded>(out var prespawnSceneLoadedEntity);
            PrespawnHelper.PopulateSceneHashLookupTable(prespawnSharedComponents, state.EntityManager, m_SceneSectionHashLookup);

            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            // 存在需要发送数据的连接时，并行序列化各连接的数据
            UpdateSerializeJobDependencies(ref state);
            var serializeJob = new SerializeJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = m_GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = m_GhostComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                SubSceneHashSharedIndexMap = m_SceneSectionHashLookup,
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                despawnChunks = despawnChunks,
                ghostChunks = ghostChunks,
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                ackFromEntity = m_SnapshotAckFromEntity,
                connectionFromEntity = m_ConnectionFromEntity,
                networkIdFromEntity = m_NetworkIdFromEntity,
                entityType = m_EntityType,
                ghostSystemStateType = m_GhostSystemStateType,
                preSerializedGhostType = m_PreSerializedGhostType,
                prespawnGhostIdType = m_PrespawnedGhostIdType,
                ghostComponentType = m_GhostComponentType,
                ghostGroupType = m_GhostGroupType,
                ghostChildEntityComponentType = m_GhostChildEntityComponentType,
                relevantGhostForConnection = m_GhostRelevancySet,
                userGlobalRelevantMask = userGlobalRelevantQueryMask,
                internalGlobalRelevantMask = internalGlobalRelevantQueryMask,
                relevancyMode = relevancyMode,
#if UNITY_EDITOR || NETCODE_DEBUG
                NetStatsSnapshotPerThread = snapshotStatsSingleton.allGhostStatsParallelWrites.AsArray(),
#endif
                compressionModel = m_CompressionModel,
                ghostFromEntity = m_GhostFromEntity,
                currentTick = currentTick,
                localTime = NetworkTimeSystem.TimestampMS,
                simulationTickRateIntervalMs = (tickRate.SimulationFixedTimeStep * 1000f),
                networkTickRateIntervalTicks = tickRate.CalculateNetworkSendRateInterval(),

                snapshotTargetSizeFromEntity = m_SnapshotTargetFromEntity,
                ghostTypeFromEntity = m_GhostTypeFromEntity,
                allocatedGhostIds = m_AllocatedGhostIds,
                prespawnDespawns = m_DestroyedPrespawns,
                childEntityLookup = state.GetEntityStorageInfoLookup(),
                linkedEntityGroupType = m_LinkedEntityGroupType,
                prespawnBaselineTypeHandle = m_PrespawnGhostBaselineType,
                subsceneHashSharedTypeHandle = m_SubsceneGhostComponentType,
                prespawnSceneLoadedEntity = prespawnSceneLoadedEntity,
                prespawnAckFromEntity = m_PrespawnAckFromEntity,
                prespawnSceneLoadedFromEntity = m_PrespawnSceneLoadedFromEntity,

                CurrentSystemVersion = state.GlobalSystemVersion,
#if NETCODE_DEBUG
                prefabNamesFromEntity = m_PrefabDebugNameFromEntity,
                enableLoggingFromEntity = m_EnablePacketLoggingFromEntity,
                timestamp = packetDumpTimestamp,
#endif
                netDebug = netDebug,
                systemData = systemData,
#if UNITY_EDITOR
                UpdateLen = m_UpdateLen,
                UpdateCounts = m_UpdateCounts,
#endif
            };
            if (!SystemAPI.TryGetSingleton<GhostImportance>(out var importance))
            {
                serializeJob.BatchScaleImportance = default;
                serializeJob.ScaleGhostImportance = default;
            }
            else
            {
                serializeJob.BatchScaleImportance = importance.BatchScaleImportanceFunction;
                serializeJob.ScaleGhostImportance = importance.ScaleImportanceFunctionSuppressedWarning;
            }

            // 不向 TypeHandle 分配默认值，否则会触发安全错误
            if (SystemAPI.TryGetSingletonEntity<GhostImportance>(out var singletonEntity))
            {
                m_GhostImportanceType.Update(ref state);

                var entityStorageInfoLookup = SystemAPI.GetEntityStorageInfoLookup();
                var entityStorageInfo = entityStorageInfoLookup[singletonEntity];

                var ghostImportanceTypeHandle = m_GhostImportanceType;
                GhostImportance config;
                unsafe
                {
                    config = entityStorageInfo.Chunk.GetComponentDataPtrRO(ref ghostImportanceTypeHandle)[entityStorageInfo.IndexInChunk];
                }
                var ghostConnectionDataTypeRO = config.GhostConnectionComponentType;
                var ghostImportancePerChunkDataTypeRO = config.GhostImportancePerChunkDataType;
                var ghostImportanceDataTypeRO = config.GhostImportanceDataType;
                ghostConnectionDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                ghostImportanceDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                ghostImportancePerChunkDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                serializeJob.ghostConnectionDataTypeHandle = state.GetDynamicComponentTypeHandle(ghostConnectionDataTypeRO);
                serializeJob.ghostImportancePerChunkTypeHandle = state.GetDynamicSharedComponentTypeHandle(ghostImportancePerChunkDataTypeRO);
                serializeJob.ghostConnectionDataTypeSize = TypeManager.GetTypeInfo(ghostConnectionDataTypeRO.TypeIndex).TypeSize;

                // 尝试从同一个 GhostImportance 单例获取用户 Importance 数据
                // 如果不存在则不报错，只传递空指针，因此该数据视为可选
                if (ghostImportanceDataTypeRO.TypeIndex != default && !config.GhostImportanceDataType.IsZeroSized)
                {
                    var ghostImportanceDataTypeSize = TypeManager.GetTypeInfo(ghostImportanceDataTypeRO.TypeIndex).TypeSize;
                    var ghostImportanceDynamicTypeHandle = state.GetDynamicComponentTypeHandle(ghostImportanceDataTypeRO);

                    var hasGhostImportanceTypeInSingletonChunk = entityStorageInfo.Chunk.Has(ref ghostImportanceTypeHandle);
                    unsafe
                    {
                        serializeJob.ghostImportanceDataIntPtr = hasGhostImportanceTypeInSingletonChunk
                            ? (IntPtr) entityStorageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostImportanceDynamicTypeHandle, ghostImportanceDataTypeSize).GetUnsafeReadOnlyPtr()
                            : IntPtr.Zero;
                    }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (!hasGhostImportanceTypeInSingletonChunk)
                        throw new InvalidOperationException($"You configured your `GhostImportance` singleton to expect that the type '{ghostImportanceDataTypeRO.ToFixedString()}' would also be added to this singleton entity, but the singleton entity does not contain this type. Either remove this requirement, or add this component to the singleton.");
#endif
                }
                else
                {
                    serializeJob.ghostImportanceDataIntPtr = IntPtr.Zero;
                }
            }
            else
            {
                serializeJob.ghostImportancePerChunkTypeHandle = state.GetDynamicSharedComponentTypeHandle(new ComponentType { TypeIndex = TypeIndex.Null, AccessModeType = ComponentType.AccessMode.ReadOnly });
            }

            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(ghostCollectionSingleton);
            m_GhostTypeComponentType.Update(ref state);

            k_Scheduling.Begin();
            state.Dependency = m_GhostPreSerializer.Schedule(state.Dependency,
                serializeJob.GhostComponentCollectionFromEntity,
                serializeJob.GhostTypeCollectionFromEntity,
                serializeJob.GhostComponentIndexFromEntity,
                serializeJob.GhostCollectionSingleton,
                serializeJob.GhostCollectionFromEntity,
                serializeJob.linkedEntityGroupType,
                serializeJob.childEntityLookup,
                serializeJob.ghostComponentType,
                m_GhostTypeComponentType,
                serializeJob.entityType,
                serializeJob.ghostFromEntity,
                serializeJob.connectionState,
                serializeJob.netDebug,
                currentTick,
                systemData.m_UseCustomSerializer ? 1 : 0,
                ref state,
                ghostComponentCollection);
            k_Scheduling.End();
            serializeJob.SnapshotPreSerializeData = m_GhostPreSerializer.SnapshotData;

            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref serializeJob.DynamicGhostCollectionComponentTypeList);

            k_Scheduling.Begin();
            var jobHandle = serializeJob.ScheduleByRef(m_ConnectionsToProcess, 1, state.Dependency);
            m_ConnectionStatesJobHandle = jobHandle;
            state.Dependency = jobHandle;
            k_Scheduling.End();

            var serializeHandle = state.Dependency;
            // 调度清理连接的 Job
            k_Scheduling.Begin();
            var cleanupHandle = new CleanupGhostSerializationStateJob
            {
                CleanupConnectionStatePerTick = systemData.CleanupConnectionStatePerTick,
                CurrentCleanupConnectionState = m_CurrentCleanupConnectionState,
                ConnectionStates = m_ConnectionStates,
                GhostChunks = ghostChunks,
            }.Schedule(state.Dependency);
            var flushHandle = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(serializeHandle);
            k_Scheduling.End();
            state.Dependency = JobHandle.CombineDependencies(flushHandle, cleanupHandle);
#if NETCODE_DEBUG && !USING_UNITY_LOGGING
            state.Dependency = new FlushNetDebugPacket
            {
                EnablePacketLogging = m_EnablePacketLoggingFromEntity,
                ConnectionStates = m_ConnectionsToProcess.AsDeferredJobArray(),
            }.Schedule(m_ConnectionsToProcess, 1, state.Dependency);
#endif
        }

        void UpdateSerializeJobDependencies(ref SystemState state)
        {
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity.Update(ref state);
#endif
            m_GhostTypeFromEntity.Update(ref state);
            m_SnapshotTargetFromEntity.Update(ref state);
            m_GhostGroupType.Update(ref state);
            m_GhostComponentType.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_SnapshotAckFromEntity.Update(ref state);
            m_ConnectionFromEntity.Update(ref state);
            m_GhostFromEntity.Update(ref state);
            m_SnapshotTargetFromEntity.Update(ref state);
            m_EnablePacketLoggingFromEntity.Update(ref state);
            m_GhostSystemStateType.Update(ref state);
            m_PreSerializedGhostType.Update(ref state);
            m_GhostChildEntityComponentType.Update(ref state);
            m_PrespawnedGhostIdType.Update(ref state);
            m_GhostGroupType.Update(ref state);
            m_EntityType.Update(ref state);
            m_LinkedEntityGroupType.Update(ref state);
            m_PrespawnGhostBaselineType.Update(ref state);
            m_SubsceneGhostComponentType.Update(ref state);
            m_GhostComponentCollectionFromEntity.Update(ref state);
            m_GhostComponentIndexFromEntity.Update(ref state);
            m_PrespawnAckFromEntity.Update(ref state);
            m_PrespawnSceneLoadedFromEntity.Update(ref state);
        }

        [BurstCompile]
        struct UpdateConnectionsJob : IJob
        {
            [ReadOnly] public ComponentLookup<NetworkId> NetworkIdFromEntity;
            public NativeList<Entity> Connections;
            public NativeParallelHashMap<Entity, int> ConnectionStateLookup;
            public NativeList<ConnectionStateData> ConnectionStates;
            public NativeList<ConnectionStateData> ConnectionsToProcess;
            public NativeReference<NetworkTick> OldestPendingDespawnTickByAll;
            public byte SendThisTick;
            public int NetTickInterval;
            public uint SentSnapshots;

            public void Execute()
            {
                var existing = new NativeParallelHashSet<Entity>(Connections.Length, Allocator.Temp);
                int maxConnectionId = 0;
                var oldestPendingByAll = OldestPendingDespawnTickByAll.Value;
                foreach (var connection in Connections)
                {
                    existing.Add(connection);
                    if (!ConnectionStateLookup.TryGetValue(connection, out var stateIndex))
                    {
                        stateIndex = ConnectionStates.Length;
                        ConnectionStates.Add(ConnectionStateData.Create(connection));
                        ConnectionStateLookup.TryAdd(connection, stateIndex);
                    }
                    maxConnectionId = math.max(maxConnectionId, NetworkIdFromEntity[connection].Value);

                    var oldestPendingDespawnTick = ConnectionStates[stateIndex].GhostStateData.OldestPendingDespawnTick;
                    if (!oldestPendingDespawnTick.IsValid)
                        oldestPendingByAll = NetworkTick.Invalid;
                    else if (oldestPendingByAll.IsValid && oldestPendingByAll.IsNewerThan(oldestPendingDespawnTick))
                        oldestPendingByAll = oldestPendingDespawnTick;
                }
                OldestPendingDespawnTickByAll.Value = oldestPendingByAll;

                for (int i = 0; i < ConnectionStates.Length; ++i)
                {
                    if (existing.Contains(ConnectionStates[i].Entity))
                    {
                        continue;
                    }

                    ConnectionStateLookup.Remove(ConnectionStates[i].Entity);
                    ConnectionStates[i].Dispose();
                    if (i != ConnectionStates.Length - 1)
                    {
                        ConnectionStates[i] = ConnectionStates[ConnectionStates.Length - 1];
                        ConnectionStateLookup.Remove(ConnectionStates[i].Entity);
                        ConnectionStateLookup.TryAdd(ConnectionStates[i].Entity, i);
                    }

                    ConnectionStates.RemoveAtSwapBack(ConnectionStates.Length - 1);
                    --i;
                }

                if (SendThisTick == 0)
                    return;
                var sendPerFrame = (ConnectionStates.Length + NetTickInterval - 1) / NetTickInterval;
                var sendStartPos = sendPerFrame * (int) (SentSnapshots % NetTickInterval);

                if (sendStartPos + sendPerFrame > ConnectionStates.Length)
                    sendPerFrame = ConnectionStates.Length - sendStartPos;
                for (int i = 0; i < sendPerFrame; ++i)
                    ConnectionsToProcess.Add(ConnectionStates[sendStartPos + i]);
            }
        }

#if NETCODE_DEBUG && !USING_UNITY_LOGGING
        struct FlushNetDebugPacket : IJobParallelForDefer
        {
            [ReadOnly] public ComponentLookup<EnablePacketLogging> EnablePacketLogging;
            [ReadOnly] public NativeArray<ConnectionStateData> ConnectionStates;
            public void Execute(int index)
            {
                var state = ConnectionStates[index];
                if (EnablePacketLogging.HasComponent(state.Entity))
                {
                    state.NetDebugPacket.Flush();
                }
            }
        }
#endif

        [BurstCompile]
        struct CleanupGhostSerializationStateJob : IJob
        {
            public int CleanupConnectionStatePerTick;
            public int CurrentCleanupConnectionState;
            [ReadOnly] public NativeList<ConnectionStateData> ConnectionStates;
            [ReadOnly] public NativeList<ArchetypeChunk> GhostChunks;

            public unsafe void Execute()
            {
                var conCount = math.min(CleanupConnectionStatePerTick, ConnectionStates.Length);
                var existingChunks = new UnsafeHashMap<ArchetypeChunk, ulong>(GhostChunks.Length, Allocator.Temp);
                foreach (var chunk in GhostChunks)
                {
                    existingChunks.TryAdd(chunk, chunk.SequenceNumber);
                }
                for (int con = 0; con < conCount; ++con)
                {
                    var conIdx = (con + CurrentCleanupConnectionState) % ConnectionStates.Length;
                    var chunkSerializationData = ConnectionStates[conIdx].SerializationState;
                    var oldChunks = chunkSerializationData->GetKeyArray(Allocator.Temp);
                    foreach (var oldChunk in oldChunks)
                    {
                        if (existingChunks.TryGetValue(oldChunk, out var sequence) && sequence == oldChunk.SequenceNumber)
                        {
                            continue;
                        }
                        GhostChunkSerializationState chunkState;
                        chunkSerializationData->TryGetValue(oldChunk, out chunkState);
                        chunkState.FreeSnapshotData();
                        chunkSerializationData->Remove(oldChunk);
                    }
                }
            }
        }

        [BurstCompile]
        struct GhostDespawnSingleJob : IJob
        {
            public NativeQueue<SpawnedGhost> FreeSpawnQueue;
            public NativeQueue<int> DespawnQueue;
            public NativeList<int> DespawnList;
            public NativeParallelHashMap<SpawnedGhost, Entity> GhostMap;

            public void Execute()
            {
                while (DespawnQueue.TryDequeue(out int destroyed))
                {
                    if (!DespawnList.Contains(destroyed))
                    {
                        DespawnList.Add(destroyed);
                    }
                }

                while (FreeSpawnQueue.TryDequeue(out var spawnedGhost))
                {
                    GhostMap.Remove(spawnedGhost);
                }
            }
        }

        [BurstCompile]
        partial struct GhostDespawnParallelJob : IJobEntity
        {
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity> GhostMap;
            [ReadOnly] public NativeReference<NetworkTick> OldestPendingDespawnTickByAll;
            [ReadOnly] public DynamicBuffer<PrespawnGhostIdRange> PrespawnIdRanges;
            public EntityCommandBuffer.ParallelWriter CommandBufferConcurrent;
            public NativeQueue<int>.ParallelWriter PrespawnDespawn;
            public NativeQueue<int>.ParallelWriter FreeGhostIds;
            public NativeQueue<SpawnedGhost>.ParallelWriter FreeSpawnedGhosts;
            public NetworkTick CurrentTick;

            public void Execute(Entity entity, [EntityIndexInQuery]int entityIndexInQuery, ref GhostCleanup ghost)
            {
                var oldestPendingByAll = OldestPendingDespawnTickByAll.Value;
                if (!ghost.despawnTick.IsValid)
                {
                    ghost.despawnTick = CurrentTick;
                }
                else if (oldestPendingByAll.IsValid && oldestPendingByAll.IsNewerThan(ghost.despawnTick))
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                        FreeGhostIds.Enqueue(ghost.ghostId);
                    CommandBufferConcurrent.RemoveComponent<GhostCleanup>(entityIndexInQuery, entity);
                }
                // 无论客户端是否 Ack，都尽快从映射移除 Ghost
                var spawnedGhost = new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick};
                if (!GhostMap.ContainsKey(spawnedGhost))
                {
                    return;
                }
                FreeSpawnedGhosts.Enqueue(spawnedGhost);
                // 如果不存在已分配范围，则不加入队列，这表示预生成 Ghost 所属 SubScene 已卸载
                if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId) && PrespawnIdRanges.GhostIdRangeIndex(ghost.ghostId) >= 0)
                    PrespawnDespawn.Enqueue(ghost.ghostId);
            }
        }

    }
}
