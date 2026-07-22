using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 场景中全部 Ghost Component 数据的 Hash
    /// 可用于对 SubScene 排序，使预生成场景对象的 GhostId 以确定性方式排列
    /// </summary>
    public struct SubSceneGhostComponentHash : ISharedComponentData
    {
        /// <summary>
        /// 由 <see cref="Unity.NetCode.PreSpawnedGhostsConversion"/> System 计算的唯一 Hash 值
        /// </summary>
        public ulong Value;
    }

    /// <summary>
    /// 在 SubScene 内唯一，用于为预生成 Ghost Entity 确定性地分配 GhostId
    /// </summary>
    public struct PreSpawnedGhostIndex : IComponentData
    {
        /// <summary>
        /// 转换期间分配给 Ghost 的预排序 Prespawn 索引
        /// </summary>
        public int Value;
    }

    /// <summary>
    /// 所有 Baseline 和预生成 Ghost 处理完成后添加到 SubScene Entity
    /// </summary>
    internal struct PrespawnsSceneInitialized : IComponentData
    {
    }

    /// <summary>
    /// 强制创建 PrespawnSceneList Entity Prefab
    /// 而不是等待包含预生成 Ghost 的 Entity Scene 加载
    /// </summary>
    internal struct ForcePrespawnListPrefabCreate : IComponentData
    {
    }

    /// <summary>
    /// 转换期间添加到所有包含预生成 Ghost 的 SubScene
    /// </summary>
    public struct SubSceneWithPrespawnGhosts : IComponentData
    {
        /// <summary>
        /// 用于查询属于该场景的所有 Ghost 的确定性唯一 Hash
        /// </summary>
        public ulong SubSceneHash;

        /// <summary>
        /// 处理场景时在运行时计算
        /// </summary>
        public ulong BaselinesHash;

        /// <summary>
        /// 场景中的 Prespawn 总数
        /// </summary>
        public int PrespawnCount;
    }

#if UNITY_EDITOR
    // 打开 SubScene 进行编辑时，Entity 上不存在 SubSceneSectionData
    // 此时改为添加该 Component，以跟踪 Entity 所引用的 Section
    // 当预生成 Ghost 因相关性等原因重新生成时，需要 SceneGUID 和 Section 才能正确添加 SceneSection Component
    internal struct LiveLinkPrespawnSectionReference : IComponentData
    {
        /// <summary>
        /// 场景 GUID
        /// </summary>
        public Hash128 SceneGUID;
        /// <summary>
        /// Section 索引
        /// </summary>
        public int Section;
    }
#endif

    /// <summary>
    /// Prespawn Baseline 序列化完成后添加到 SubScene Entity 的标签 Component
    /// </summary>
    internal struct SubScenePrespawnBaselineResolved : IComponentData
    {
    }

    /// <summary>
    /// 转换期间添加到所有具有 PrespawnId Component 的 Ghost 上的 Buffer
    /// 其中包含 PrespawnGhostBaselineSystem 处理 Entity 时生成的预序列化 Ghost Snapshot
    /// Prespawn Baseline 用于优化晚加入玩家的带宽
    /// 服务器只向新客户端发送相对于该 Baseline 已发生变化的 Prespawn Ghost
    /// </summary>
    [InternalBufferCapacity(0)]
    internal struct PrespawnGhostBaseline : IBufferElementData
    {
        public byte Value;
    }

    /// <summary>
    /// 服务器为每个已加载并处理的 SubScene 权威填充该 Buffer
    /// 客户端通过 Snapshot 流接收它，并使用其中信息正确处理匹配的已加载 SubScene
    /// 客户端使用 Hash 验证本地数据与服务器一致，以正确使用 Prefab Baseline 优化
    /// InternalBufferCapacity 原本可以分配到接近占满 Chunk 内存
    /// </summary>
    [InternalBufferCapacity(0)]
    [GhostComponent(PrefabType = GhostPrefabType.All)]
    internal struct PrespawnSceneLoaded : IBufferElementData
    {
        /// <summary>
        /// 已加载 <see cref="SubSceneWithPrespawnGhosts"/> 的唯一 SubScene Hash
        /// </summary>
        [GhostField]public ulong SubSceneHash;
        /// <summary>
        /// 所有预生成 Ghost Baseline 的 Hash，用于验证数据一致性
        /// </summary>
        [GhostField]public ulong BaselineHash;
        /// <summary>
        /// 服务器为该场景内 Ghost 分配的首个 GhostId
        /// </summary>
        [GhostField]public int FirstGhostId;
        /// <summary>
        /// 场景中的 Ghost 数量，用于一致性检查
        /// </summary>
        [GhostField]public int PrespawnCount;
    }

    /// <summary>
    /// 添加到 PrespawnGhostIdAllocator Singleton Entity
    /// 这是 Prespawn 对象的 GhostId 分配 Map，服务器用它跟踪与包含预生成 Ghost 的场景关联的 GhostId 子集
    /// InternalBufferCapacity 设置为近似占满 Chunk
    /// </summary>
    [InternalBufferCapacity(0)]
    internal struct PrespawnGhostIdRange : IBufferElementData
    {
        // 该范围所应用的场景
        public ulong SubSceneHash;
        // 范围内的首个 GhostId
        public int FirstGhostId;
        // Prespawn 数量
        public short Count;
        // 范围已保留时为 1，可复用时为 0
        public short Reserved;
    }

    /// <summary>
    /// 添加到所有包含 Ghost 的 SubScene 上的 Cleanup Component
    /// 用于在客户端和服务器跟踪 SubScene 的卸载
    /// </summary>
    internal struct SubSceneWithGhostCleanup : ICleanupComponentData
    {
        /// <summary>
        /// <see cref="SubSceneWithPrespawnGhosts"/> 的 SubScene Hash
        /// </summary>
        public ulong SubSceneHash;
        /// <summary>
        /// Unity 场景 GUID
        /// </summary>
        public Hash128 SceneGUID;
        /// <summary>
        /// 包含预生成 Ghost 的 Scene Section
        /// </summary>
        public int SectionIndex;
        /// <summary>
        /// 分配给场景内 Ghost 的首个 GhostId
        /// </summary>
        public int FirstGhostId;
        /// <summary>
        /// 场景内的 Ghost 数量
        /// </summary>
        public int PrespawnCount;
        /// <summary>
        /// 仅供客户端使用，请求开始或停止场景流式传输的开关标志
        /// </summary>
        public int Streaming;
    }

    /// <summary>
    /// 由服务器添加到 NetworkStream Entity 的 Component
    /// 用于跟踪客户端已加载并确认的 Prespawn Ghost Section
    /// 服务器只为客户端已通知就绪的 Section 流式传输预生成 Ghost
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PrespawnSectionAck : IBufferElementData
    {
        /// <summary>
        /// 每个包含预生成 Ghost 的 SubScene 所对应的确定性唯一 Hash
        /// 参见 <see cref="SubSceneWithPrespawnGhosts"/>
        /// </summary>
        public ulong SceneHash;
    }
}
