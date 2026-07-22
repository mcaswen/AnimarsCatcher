using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含 Ghost 所需全部元数据的 BlobAsset
    /// </summary>
    internal struct GhostPrefabBlobMetaData
    {
        public enum GhostMode
        {
            Interpolated = 1,
            Predicted = 2,
            Both = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComponentInfo
        {
            /// <summary>
            /// Component 的 StableTypeHash
            /// </summary>
            public ulong StableHash;
            /// <summary>
            /// 要使用的 Serializer Variant，为 0 时使用该类型的默认 Variant
            /// 注意：此值也表示是否应发送给 Child
            /// </summary>
            public ulong Variant;
            /// <summary>
            /// 不等于 -1 时用于覆盖 Component SendMask
            /// </summary>
            public int SendMaskOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComponentReference
        {
            public ComponentReference(int index, ulong hash)
            {
                EntityIndex = index;
                StableHash = hash;
            }
            /// <summary>
            /// LinkedEntityGroup 中的 Entity 索引
            /// </summary>
            public int EntityIndex;
            /// <summary>
            /// Component 的稳定 Hash
            /// </summary>
            public ulong StableHash;
        }

        public int Importance;
        public byte MaxSendRate;
        public GhostMode SupportedModes;
        public GhostMode DefaultMode;
        public bool StaticOptimization;
        public bool PredictedSpawnedGhostRollbackToSpawnTick;
        public bool RollbackPredictionOnStructuralChanges;
        public bool UseSingleBaseline;
        public BlobString Name;
        /// <summary>
        /// 层级中每个 Child 的 Component 数组
        /// </summary>
        public BlobArray<ComponentInfo> ServerComponentList;
        public BlobArray<int> NumServerComponentsPerEntity;
        /// <summary>
        /// 在仅服务器 World，即 Binary World，中使用 Prefab 时应移除的 Child Index 与 Component 对列表
        /// 主要用于支持 ClientAndServer 数据
        /// </summary>
        public BlobArray<ComponentReference> RemoveOnServerOnlyWorld;

        /// <summary>
        /// Single World Host 和 Binary World Server 共用的 Component 集合
        /// Single World Host 应直接使用，Binary World Server 则应在其基础上补充其他 Component
        /// Single World Host 同时也是服务器，因此不能剥离全部 Component
        /// </summary>
        internal BlobArray<ComponentReference> RemoveOnAllServerWorldsSharedList;

        /// <summary>
        /// 在客户端上使用 Prefab 时应移除的 Child Index 与 Component 对列表
        /// 主要用于支持 ClientAndServer 数据
        /// </summary>
        public BlobArray<ComponentReference> RemoveOnClientWorlds;
        /// <summary>
        /// 使用 Prefab 实例化 Predicted Ghost 时应禁用的 Child Index 与 Component 对列表
        /// 用于让客户端只需维护一个 Prefab
        /// </summary>
        public BlobArray<ComponentReference> DisableOnPredictedClient;
        /// <summary>
        /// 使用 Prefab 实例化 Interpolated Ghost 时应禁用的 Child Index 与 Component 对列表
        /// 用于让客户端只需维护一个 Prefab
        /// </summary>
        public BlobArray<ComponentReference> DisableOnInterpolatedClient;
    }

    /// <summary>
    /// 添加到所有 Ghost Prefab 的 Component，包含将 Prefab 用作 Ghost 所需的元数据
    /// </summary>
    [DontSupportPrefabOverrides]
    [GhostComponent(SendDataForChildEntity = false)]
    internal struct GhostPrefabMetaData : IComponentData
    {
        public BlobAssetReference<GhostPrefabBlobMetaData> Value;
    }

    /// <summary>
    /// 添加到使用前需要在运行时剥离 Component 的 Ghost Prefab
    /// 完成运行时剥离后会移除此 Component
    /// </summary>
    internal struct GhostPrefabRuntimeStrip : IComponentData
    {}

    /// <summary>
    /// 用于跟踪已加载或创建的新 Ghost Prefab 的内部 Component
    /// </summary>
    internal struct GhostPrefabTracking : ICleanupComponentData
    {
        /// <summary>
        /// GhostCollectionPrefab 列表中的索引
        /// </summary>
        public int GhostCollectionPrefabIndex;
        /// <summary>
        /// 与该 Prefab 关联的 <see cref="GhostType"/>
        /// </summary>
        public GhostType GhostType;
    }
    /// <summary>
    /// 用于标识持有 Ghost Collection 列表与数据的 Singleton 的 Component
    /// 该 Singleton 包含 GhostCollectionPrefab、GhostCollectionPrefabSerializer、
    /// GhostCollectionComponentIndex 和 GhostComponentSerializer.State Buffer
    /// </summary>
    public struct GhostCollection : IComponentData
    {
        /// <summary>
        /// <para>
        /// 已加载到 <see cref="GhostCollectionPrefab"/> 集合中的 Prefab 数量
        /// 用于确定服务器可以向客户端流式传输哪些 Ghost 类型
        /// </para>
        /// <para>
        /// 服务器通过 Snapshot Protocol 向客户端报告已加载 Prefab 列表及其 <see cref="GhostTypeComponent"/> GUID
        /// 该列表是动态的，服务器可以在运行时添加或加载新 Prefab，并将新增项报告给客户端
        /// </para>
        /// <para>
        /// 客户端通过 Command Protocol 向服务器报告已加载 Prefab 数量
        /// 客户端收到 Ghost Snapshot 时会处理 Ghost Prefab 列表，
        /// 并将集合中尚不存在的新 Ghost 类型加入 <see cref="GhostCollectionPrefab"/>
        /// </para>
        /// <para>
        /// 客户端初始化 World 时无需加载 <see cref="GhostCollectionPrefab"/> 中的全部 Prefab 类型
        /// 它们可以动态加载或加入 World，例如流式加载 SubScene 时
        /// 此时应使用 <see cref="GhostCollectionPrefab.Loading"/> 状态，
        /// 告知 <see cref="GhostCollection"/> 指定 Prefab 正在加载到 World
        /// </para>
        /// </summary>
        public int NumLoadedPrefabs;
        #if UNITY_EDITOR || NETCODE_DEBUG
        /// <summary>
        /// 仅供调试使用，表示预测错误名称列表的当前长度，由 <see cref="GhostPredictionDebugSystem"/> 使用
        /// </summary>
        internal int NumPredictionErrors;
        #endif
        /// <summary>
        /// 指定 <see cref="GhostType"/> 对应 Prefab 在 <see cref="GhostCollectionPrefab"/> 列表中的索引
        /// <see cref="GhostReceiveSystem"/> 从服务器收到新 Prefab Hash 时填充此映射，
        /// 用于跟踪哪些 Prefab 需要映射或加载
        /// </summary>
        /// <remarks>
        /// 仅应由客户端使用，服务器上的映射始终为空
        /// 还包含一个 default(GhostType) 特殊 Key，用于表示列表自上次处理后是否发生变化
        /// </remarks>
        public NativeHashMap<GhostType, int> PendingGhostPrefabAssignment;
        /// <summary>
        /// 指定 <see cref="GhostType"/> 在 <see cref="GhostCollectionPrefab"/> 列表中的索引
        /// </summary>
        public NativeHashMap<GhostType, int>.ReadOnly GhostTypeToColletionIndex;
        /// <summary>
        /// 至少存在一条已进入游戏的 <see cref="NetworkStreamConnection"/> 时设置的标志
        /// </summary>
        public bool IsInGame;
    }

    /// <summary>
    /// 可用作 Ghost 的全部 Prefab 列表
    /// 服务器会用全部 Ghost Prefab 填充该列表并将其发送给客户端
    /// Prefab 出现在列表中并不保证已经存在对应 Serializer
    /// 此 Buffer 添加到 GhostCollection Singleton Entity
    /// </summary>
    /// <remarks>
    /// 列表按 <see cref="GhostType"/> GUID 值排序
    /// </remarks>
    [InternalBufferCapacity(0)]
    public struct GhostCollectionPrefab : IBufferElementData
    {
        /// <summary>
        /// Ghost Prefab 从 SubScene 加载或在运行时动态创建后，可以立即动态加入 Ghost Collection
        /// 客户端使用此枚举通知 Ghost Collection System，
        /// <see cref="GhostCollectionPrefab"/> 类型正在加载到 World
        /// </summary>
        public enum LoadingState
        {
            /// <summary>
            /// 默认状态，Prefab 尚未加载或不存在，即 <see cref="GhostCollectionPrefab.GhostPrefab"/> 引用为 <see cref="Entity.Null"/>
            /// </summary>
            NotLoading = 0,
            /// <summary>
            /// 表示客户端已开始加载 Entity Prefab，例如客户端正在流式加载 SubScene 内容
            /// <see cref="GhostCollectionSystem"/> 将开始监控该资源状态，参见 <see cref="GhostCollectionPrefab.GhostPrefab"/>
            /// </summary>
            LoadingActive,
            /// <summary>
            /// Prefab 当前正在加载，但可能是 Prefab Entity 不存在，或 Prefab 尚未处理
            /// 此状态只能由 <see cref="GhostCollectionSystem"/> 设置，
            /// 并且仅当 <see cref="GhostCollectionPrefab.Loading"/> 当前为 <see cref="LoadingActive"/> 时设置
            /// </summary>
            LoadingNotActive
        }
        /// <inheritdoc cref="NetCode.GhostType"/>
        public GhostType GhostType;
        /// <summary>
        /// 对 Prefab Entity 的引用，初始值为 <see cref="Entity.Null"/>
        /// <see cref="GhostCollectionSystem"/> 处理 Prefab 时为其赋值
        /// </summary>
        public Entity GhostPrefab;
        /// <summary>
        /// 由 <see cref="GhostCollectionSystem"/> 在运行时计算并用于一致性检查
        /// 特别用于验证 Ghost 的序列化与反序列化方式一致
        /// </summary>
        internal ulong Hash;
        /// <summary>
        /// Prefab 正在加载时，游戏代码应将此值设为 LoadingActive
        /// Collection System 每帧都会将其设为 LoadingNotActive，
        /// 因此 Prefab 仍在加载期间，游戏代码必须每帧重新设为 LoadingActive
        /// </summary>
        public LoadingState Loading;
    }
    /// <summary>
    /// GhostCollectionPrefab 中各 Prefab 的全部 Serializer Data 列表
    /// 如果部分 Serializer 尚未创建，此列表可能更短
    /// 此 Buffer 添加到 GhostCollection Singleton Entity
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostCollectionPrefabSerializer : IBufferElementData
    {
        /// <summary>
        /// Prefab 的 Stable Type Hash，用于获取 GhostCollectionPrefabSerializer 实例
        /// 该 Hash 由名称和全部 Component Serializer 的 Hash 组合而成
        /// </summary>
        public ulong TypeHash;
        /// <summary>
        /// <see cref="GhostCollectionComponentIndex"/> 中第一个待用 Component 序列化规则的索引
        /// </summary>
        public int FirstComponent;
        /// <summary>
        /// 已序列化 Component 总数，包括 Root Entity 和 Child Entity
        /// </summary>
        public int NumComponents;
        /// <summary>
        /// 仅存在于 Child Entity 中的已序列化 Component 总数
        /// </summary>
        public int NumChildComponents;
        /// <summary>
        /// 整个 Ghost 类型的总字节大小，包括 Enable Bit 和 ChangeMask 所需空间
        /// </summary>
        public int SnapshotSize;
        /// <summary>
        /// 整个 Ghost 类型的 ChangeMask BitArray 使用的位数
        /// </summary>
        public int ChangeMaskBits;
        /// <summary>
        /// <para>仅当 Entity Prefab 上存在 <see cref="GhostOwner"/> 时设置
        /// 表示相对 Snapshot Data 起始位置的字节 Offset，可从该位置获取拥有此 Entity 的客户端 Network ID
        /// </para>
        /// <code>
        /// var ghostOwner = *(uint*)(snapshotDataPtr + PredictionOwnerOffset)
        /// </code>
        /// </summary>
        public int PredictionOwnerOffset;
        /// <summary>
        /// 表示 Ghost 复制模式是否设为 Owner Predicted 的标志
        /// </summary>
        public int OwnerPredicted;
        /// <summary>
        /// Ghost 包含具有不同 <see cref="GhostComponentSerializer.SendMask"/> 的 Component 时设为 1
        /// 根据 Ghost 复制模式是 Interpolated 还是 Predicted，部分 Component 不应复制，
        /// 该决策必须由 <see cref="GhostSendSystem"/> 在运行时序列化 Entity 时作出
        /// </summary>
        public byte PartialComponents;
        /// <summary>
        /// Ghost 中部分 Component 的 <see cref="GhostComponentAttribute.OwnerSendType"/>
        /// 不等于 <see cref="SendToOwnerType.All"/> 时设为 1
        /// 设置后，<see cref="GhostSendSystem"/> 会执行必要的 Ghost Owner 检查
        /// </summary>
        public byte PartialSendToOwner;
        /// <summary>
        /// <see cref="GhostAuthoringComponent"/> 中的 <see cref="GhostOptimizationMode"/>
        /// 设为 <see cref="GhostOptimizationMode.Static"/> 时为 true
        /// </summary>
        public byte StaticOptimization;
        /// <summary>
        /// 允许 Predicted Spawned Ghost 回滚其初始 Spawn 状态并重新预测，直至收到服务器的权威 Spawn
        /// </summary>
        public byte PredictedSpawnedGhostRollbackToSpawnTick;
        /// <summary>
        /// 客户端 CPU 优化，发生结构性变更时强制 Predicted Ghost 始终尝试从上次预测继续
        /// 默认为 true，因为移除已复制 Component 时可能引入问题
        /// </summary>
        public byte RollbackPredictionOnStructuralChanges;
        /// <summary>
        /// 指示 <see cref="GhostSendSystem"/> 始终为此 Ghost Archetype 使用单个 Baseline
        /// </summary>
        public byte UseSingleBaseline;
        /// <inheritdoc cref="GhostPrefabCreation.Config.Importance"/>
        public int BaseImportance;
        /// <summary>
        /// 以 <see cref="ClientServerTickRate.SimulationTickRate"/> 间隔表示的
        /// <see cref="GhostPrefabCreation.Config.MaxSendRate"/>，即距离下次允许发送的 Tick 数
        /// </summary>
        /// <seealso cref="GhostPrefabCreation.Config.MaxSendRate"/>
        public byte MaxSendRateAsSimTickInterval;
        /// <summary>
        /// 没有其他用户定义系统对新 Ghost 的生成方式进行分类时，
        /// 由 <see cref="GhostSpawnClassificationSystem"/> 用于分配该 Ghost 使用的 <see cref="GhostSpawnBuffer.Type"/>
        /// </summary>
        public GhostSpawnBuffer.Type FallbackPredictionMode;
        /// <summary>
        /// 表示 Ghost Prefab 是否包含 <see cref="GhostGroup"/> Component，且能否作为 Group Root 的标志
        /// </summary>
        /// <seealso cref="GhostChildEntity"/>
        public int IsGhostGroup;
        /// <summary>
        /// 存储全部 Enableable Ghost Component 启用状态所需的位数，这些 Component 由 <see cref="GhostEnabledBitAttribute"/> 标记
        /// </summary>
        public int EnableableBits;
        /// <summary>
        /// 此 Ghost 中最大已复制 <see cref="IBufferElementData"/> 的大小
        /// 用于计算容纳已复制 Buffer Data 所需的 <see cref="SnapshotDynamicDataBuffer"/> 容量
        /// </summary>
        public int MaxBufferSnapshotSize;
        /// <summary>
        /// 此 Ghost 中已复制 <see cref="IBufferElementData"/> 的总数
        /// </summary>
        public int NumBuffers;
        /// <summary>
        /// 用于跟踪序列化性能的 Profiler Marker
        /// </summary>
        public Profiling.ProfilerMarker profilerMarker;
        /// <summary>
        /// 用于序列化 Chunk 的自定义 Serializer 函数，仅限服务器
        /// </summary>
        public PortableFunctionPointer<GhostPrefabCustomSerializer.ChunkSerializerDelegate> CustomSerializer;
        /// <summary>
        /// 用于预序列化 Chunk 的函数指针，仅限服务器
        /// </summary>
        public PortableFunctionPointer<GhostPrefabCustomSerializer.ChunkPreserializeDelegate> CustomPreSerializer;
        /// <summary>
        /// 静态优化不支持遍历多个 Chunk，因此排除以下情况
        /// - GhostGroup
        /// - 带已复制 Child 的 Ghost
        /// </summary>
        /// <returns>Entity 满足静态优化条件时为 `true`</returns>
        public readonly bool CanBeStaticOptimized() => StaticOptimization != 0 && IsGhostGroup == 0 && NumChildComponents == 0;
    }

    /// <summary>
    /// 包含支持序列化的唯一 Component 集合，用于在 Job 中将 DynamicComponentTypeHandle 映射到具体 ComponentType
    /// 此 Buffer 添加到 GhostCollection Singleton Entity
    /// </summary>
    [InternalBufferCapacity(0)]
    internal struct GhostCollectionComponentType : IBufferElementData
    {
        /// <summary>
        /// Component 类型，必须是 <see cref="IComponentData"/> 或 <see cref="IBufferElementData"/>
        /// </summary>
        public ComponentType Type;
        /// <summary>
        /// 此 Component 类型在 <see cref="GhostComponentSerializer"/> 集合中的第一个 Serializer 索引
        /// </summary>
        public int FirstSerializer;
        /// <summary>
        /// 此 Component 类型在 <see cref="GhostComponentSerializer"/> 集合中的最后一个 Serializer 索引，包含该索引
        /// </summary>
        public int LastSerializer;
    }

    /// <summary>
    /// 包含 GhostCollectionPrefabSerializer 全部序列化规则对应的 Entity 与 Component 集合
    /// GhostCollectionPrefabSerializer 通过 FirstComponent 和 NumComponents 标识此数组中要使用的 Component 集合
    /// 此 Buffer 添加到 GhostCollection Singleton Entity
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostCollectionComponentIndex : IBufferElementData
    {
        /// <summary>
        /// 此规则适用的 Ghost Entity 索引
        /// </summary>
        public int EntityIndex;
        /// <summary>
        /// <see cref="GhostCollectionComponentIndex"/> 中的索引，用于从 DynamicTypeHandle 获取 Component 类型
        /// </summary>
        public int ComponentIndex;
        /// <summary>
        /// <see cref="GhostComponentSerializer.State"/> 集合中的索引，用于获取要使用的 Serializer 类型
        /// </summary>
        public int SerializerIndex;
        /// <summary>
        /// Component 的 <see cref="Unity.Entities.TypeIndex"/>
        /// </summary>
        public int TypeIndex;
        /// <summary>
        /// Component 或 Buffer Element 的大小
        /// </summary>
        public int ComponentSize;
        /// <summary>
        /// Component 在 Snapshot Buffer 中的大小，没有序列化 Ghost Field 时为 0
        /// </summary>
        public int SnapshotSize;
        /// <summary>
        /// 此 Component 的当前 Send Mask，用于在部分配置下禁止收发 Component
        /// </summary>
        public GhostSendType SendMask;
        /// <summary>
        /// 此 Component 的当前 Owner Mask，用于在部分配置下禁止收发 Component
        /// </summary>
        public SendToOwnerType SendToOwner;
#if UNITY_EDITOR || NETCODE_DEBUG
        internal int PredictionErrorBaseIndex;
        #endif
    }

    /// <summary>
    /// 允许为指定 Ghost Prefab 关联手写的自定义序列化函数
    /// 此方法支持按 Archetype 序列化，通常可以获得更好的向量化与优化效果
    /// 但编写序列化代码并不简单，需要深入理解底层 <see cref="GhostChunkSerializer"/> 实现、数据和 Wire Format
    /// </summary>
    public struct GhostPrefabCustomSerializer
    {
        /// <summary>
        /// 包含执行 Chunk 序列化所需的全部数据
        /// </summary>
        public struct Context
        {
            /// <summary>
            /// 指向包含 Snapshot Data 的 Buffer
            /// Buffer 大小由 Archetype 固定，因为 Prefab 经 <see cref="GhostCollectionSystem"/> 注册和预处理后，Component 集合不可变
            /// </summary>
            [NoAlias]public IntPtr snapshotDataPtr;
            /// <summary>
            /// 指向包含动态 Buffer Snapshot Data 的 Buffer，这是可变大小 Buffer
            /// </summary>
            [NoAlias]public IntPtr snapshotDynamicDataPtr;
            /// <summary>
            /// <see cref="GhostCollectionPrefabSerializer"/> Buffer 中的索引
            /// </summary>
            public int ghostType;
            /// <summary>
            /// Component Data 存储位置相对 <see cref="snapshotDataPtr"/> 起点的 Offset
            /// 该 Offset 取决于 Component 数量、其 ChangeMask，以及是否存在需要复制的 Enable Bit
            /// </summary>
            public int snapshotOffset;
            /// <summary>
            /// Component ChangeMask Bit 存储位置相对 <see cref="snapshotDataPtr"/> Buffer 起点的字节 Offset
            /// </summary>
            public int changeMaskOffset;
            /// <summary>
            /// Component Enable Bit 状态存储位置相对 <see cref="snapshotDataPtr"/> Buffer 起点的字节 Offset
            /// </summary>
            public int enablebBitsOffset;
            /// <summary>
            /// 动态 Buffer Data 存储位置相对 <see cref="snapshotDynamicDataPtr"/> 起点的 Offset
            /// </summary>
            public int dynamicDataOffset;
            /// <summary>
            /// Snapshot Data 的字节大小
            /// Entity Component Data 按 snapshotSize 作为 Stride 存储，Snapshot Buffer 格式大致如下
            /// |ent1       | ... |ent n|
            /// |c1, c2.. cn| ... |c1, c2.. cn|
            /// </summary>
            public int snapshotStride;
            /// <summary>
            /// 动态 Snapshot Data 的字节容量，预先计算并主要用于边界检查
            /// </summary>
            public int dynamicDataCapacity;
            /// <summary>
            /// 当前使用的全部已注册可序列化 Component 类型的 Dynamic TypeHandle
            /// </summary>
            [NoAlias][ReadOnly] public IntPtr ghostChunkComponentTypesPtr;
            /// <summary>
            /// <see cref="ghostChunkComponentTypesPtr"/> 列表长度
            /// </summary>
            public int ghostChunkComponentTypesPtrLen;
            /// <summary>
            /// 序列化 Child Component 时用于获取 Chunk 信息，包括 Chunk 和索引的 Lookup
            /// </summary>
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            /// <summary>
            /// 用于从 Chunk 获取 <see cref="LinkedEntityGroup"/> Buffer 的 TypeHandle
            /// </summary>
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupTypeHandle;
            /// <summary>
            /// 用于将 Component Data 转换为 Snapshot Data 的 <see cref="GhostSerializerState"/> 数据
            /// </summary>
            public GhostSerializerState serializerState;
            /// <summary>
            /// Chunk 中第一个 Relevant Entity 的索引
            /// </summary>
            public int startIndex;
            /// <summary>
            /// Chunk 中最后一个 Relevant Entity 的结束索引
            /// 遍历 Chunk Entity 时应使用此值，不要使用 chunk.Count
            /// </summary>
            public int endIndex;
            /// <summary>
            /// 连接的 <see cref="NetworkId"/>
            /// </summary>
            public int networkId;
            /// <summary>
            /// 指示自定义 Serializer 不要将数据复制到 Snapshot，因为数据已经预序列化
            /// </summary>
            public int hasPreserializedData;
            /// <summary>
            /// 指示自定义 Serializer 始终为此 Ghost Archetype 使用单个 Baseline
            /// </summary>
            public int useSingleBaseline;
            /// <summary>
            /// [输出] 存储 Ghost Data 压缩大小及其在临时 Data Stream 中起始位的 Buffer
            /// 每个 Entity 的每个 Component 存储两个 int
            /// [第 1 个] Writer 中该 Component 写入起点的位 Offset
            /// [第 2 个] 该 Component 写入的位数
            /// </summary>
            [NoAlias]public IntPtr entityStartBit;
            /// <summary>
            /// 从 Chunk 获取 Component Data 时必须使用的只读 <see cref="DynamicComponentTypeHandle"/> 列表
            /// </summary>
            [NoAlias]public IntPtr ghostChunkComponentTypes;
            /// <summary>
            /// 序列化 Entity 时使用的 Baseline，每个 Entity 包含 4 个
            /// 索引 0 到 2 为 Snapshot Baseline，索引 3 为动态 Buffer Baseline
            /// </summary>
            [NoAlias]public IntPtr baselinePerEntityPtr;
            /// <summary>
            /// 包含每个 Entity 连续区段要使用的 Run-Length Encoded Baseline 索引
            /// 可用于判断 Entity 是否为 Irrelevant
            /// </summary>
            [NoAlias]public IntPtr sameBaselinePerEntityPtr;
            /// <summary>
            /// [输出] 存储 Chunk 中每个 Entity 动态 Buffer Data 总大小的 Buffer
            /// </summary>
            [NoAlias]public IntPtr dynamicDataSizePerEntityPtr;
            /// <summary>
            /// 包含全零字节的只读 Buffer，最大 8KB
            /// </summary>
            [NoAlias]public IntPtr zeroBaseline;
            /// <summary>
            /// 指向 Chunk 中 <see cref="GhostInstance"/> 数据的指针
            /// </summary>
            [NoAlias]public IntPtr ghostInstances;
        }

        /// <summary>
        /// 序列化 Chunk 时调用的函数指针
        /// </summary>
        public PortableFunctionPointer<ChunkSerializerDelegate> SerializeChunk;
        /// <summary>
        /// 用于序列化 Chunk 的自定义 Serializer 函数，仅限服务器
        /// </summary>
        public PortableFunctionPointer<ChunkPreserializeDelegate> PreSerializeChunk;
        ///<summary>
        /// 用于指定已序列化 Component 自定义顺序的委托
        /// </summary>
        /// <param name="componentTypes">已序列化 Component 类型</param>
        /// <param name="componentCount">Component 数量</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CollectComponentDelegate(IntPtr componentTypes, IntPtr componentCount);
        ///<summary>
        /// 自定义 Chunk Serializer 的委托
        /// </summary>
        /// <param name="chunk">目标 Chunk</param>
        /// <param name="typeData">类型数据</param>
        /// <param name="componentIndices">Component 索引</param>
        /// <param name="context">上下文</param>
        /// <param name="tempWriter">临时 Data Stream Writer</param>
        /// <param name="compressionModel">压缩模型</param>
        /// <param name="lastSerializedEntity">最后一个已序列化 Entity</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ChunkSerializerDelegate(
            ref ArchetypeChunk chunk,
            in GhostCollectionPrefabSerializer typeData,
            in DynamicBuffer<GhostCollectionComponentIndex> componentIndices,
            ref Context context,
            ref DataStreamWriter tempWriter,
            in StreamCompressionModel compressionModel,
            ref int lastSerializedEntity);
        ///<summary>
        /// 自定义 Chunk 预序列化函数的委托
        /// </summary>
        /// <param name="chunk">目标 Chunk</param>
        /// <param name="typeData">类型数据</param>
        /// <param name="componentIndices">Component 索引</param>
        /// <param name="context">上下文</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ChunkPreserializeDelegate(
            in ArchetypeChunk chunk,
            in GhostCollectionPrefabSerializer typeData,
            in DynamicBuffer<GhostCollectionComponentIndex> componentIndices,
            ref Context context);
    }

    /// <summary>
    /// 保存自定义 Chunk Serializer 列表的 Singleton Component
    /// </summary>
    public struct GhostCollectionCustomSerializers : IComponentData
    {
        /// <summary>
        /// 为指定 Prefab GUID 或 <see cref="GhostType"/> 关联 <see cref="GhostPrefabCustomSerializer"/>
        /// 可通过显式转换运算符从 <see cref="GhostType"/> 得到 Hash128
        /// </summary>
        public NativeHashMap<Hash128, GhostPrefabCustomSerializer> Serializers;
    }
}
