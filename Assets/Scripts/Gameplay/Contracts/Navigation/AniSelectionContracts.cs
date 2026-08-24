using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 指定一次选择集更新如何作用于服务器已发布的成员
    /// </summary>
    public enum AniSelectionUpdateMode : byte
    {
        // 用提交内容完整替换玩家当前选择集
        Replace,

        // 将提交内容并入玩家当前选择集
        Add,

        // 从玩家当前选择集中移除提交内容
        Remove,

        // 清空玩家当前选择集，且不携带成员
        Clear
    }

    /// <summary>
    /// 集中保存选择集协议容量、超时和确定性 Hash 规则
    /// </summary>
    public static class AniSelectionProtocol
    {
        // 单块保留 120 个 int，避免把 FixedList512Bytes 的 127 个理论槽位全部占满
        public const int MemberIdsPerChunk = 120;

        // 正式协议允许一个玩家同时选择的 Ani 上限
        public const int MaximumMemberCount = 10000;

        // 由成员上限和单块容量推导，避免客户端与服务器各自维护魔法数字
        public const int MaximumChunkCount =
            (MaximumMemberCount + MemberIdsPerChunk - 1) / MemberIdsPerChunk;

        // 未收齐的版本最多保留 180 次服务器更新，防止断包长期占用 Buffer
        public const uint AssemblyTimeoutTicks = 180;

        // 选择集和分块统一使用 64 位 FNV-1a，保证两端以相同步骤得到结果
        private const ulong HashOffset = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;

        /// <summary>
        /// 计算排序后完整选择集的版本化 Hash
        /// </summary>
        public static ulong ComputeSelectionHash(uint version, NativeArray<int> sortedGhostIds)
        {
            // 版本和成员数参与 Hash，避免相同成员被错误地当成同一次提交
            ulong hash = BeginSelectionHash(version, sortedGhostIds.Length);

            // 调用方必须先排序，成员顺序因此可以作为完整性协议的一部分
            for (int index = 0; index < sortedGhostIds.Length; index++)
            {
                AddInt(ref hash, sortedGhostIds[index]);
            }

            return hash;
        }

        /// <summary>
        /// 计算服务器已发布成员 Buffer 的版本化 Hash
        /// </summary>
        public static ulong ComputeSelectionHash(
            uint version,
            DynamicBuffer<ServerAniSelectionMember> members)
        {
            // 服务器发布 Buffer 后使用同一算法复核，避免复制过程中改变成员语义
            ulong hash = BeginSelectionHash(version, members.Length);
            for (int index = 0; index < members.Length; index++)
            {
                AddInt(ref hash, members[index].GhostId);
            }

            return hash;
        }

        /// <summary>
        /// 计算单个分块的 Hash，用于识别重复块和内容冲突
        /// </summary>
        public static ulong ComputeChunkHash(
            uint version,
            ushort chunkIndex,
            ushort chunkCount,
            FixedList512Bytes<int> ghostIds)
        {
            // 块位置和总块数也属于内容，不能把另一位置的相同成员当成合法重传
            ulong hash = HashOffset;
            AddUInt(ref hash, version);
            AddInt(ref hash, chunkIndex);
            AddInt(ref hash, chunkCount);
            AddInt(ref hash, ghostIds.Length);
            for (int index = 0; index < ghostIds.Length; index++)
            {
                AddInt(ref hash, ghostIds[index]);
            }

            return hash;
        }

        private static ulong BeginSelectionHash(uint version, int memberCount)
        {
            // 完整选择集先写入固定头部，再按 GhostId 升序追加成员
            ulong hash = HashOffset;
            AddUInt(ref hash, version);
            AddInt(ref hash, memberCount);
            return hash;
        }

        private static void AddInt(ref ulong hash, int value)
        {
            // GhostId 按无符号位模式参与计算，保留 int 的全部比特
            AddUInt(ref hash, unchecked((uint)value));
        }

        private static void AddUInt(ref ulong hash, uint value)
        {
            // 显式按低位到高位写入，避免平台字节序影响协议结果
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= HashPrime;
            }
        }
    }

    /// <summary>
    /// 记录服务器 GhostId 索引的发布版本和异常计数
    /// </summary>
    public struct ServerAniGhostIdIndex : IComponentData
    {
        // 每次真正重新发布索引时递增，便于检测无意义重建
        public uint Version;

        // 当前可安全解析的唯一 GhostId 数量
        public int EntryCount;

        // 本轮因编号冲突而整体排除的索引项数量
        public int DuplicateGhostIdCount;
    }

    /// <summary>
    /// 保存按 GhostId 升序发布的服务器 Ani 索引项
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ServerAniGhostIdIndexEntry : IBufferElementData
    {
        // NetCode 分配给 Ani Ghost 的网络编号，也是 Buffer 排序键
        public int GhostId;

        // 服务器 World 中可直接访问的 Ani Entity
        public Entity Ani;

        // GhostOwner 的网络连接编号，用于执行选择权限校验
        public int OwnerNetworkId;
    }

    /// <summary>
    /// 保存某个玩家最后一次通过完整性和权限校验的选择集
    /// </summary>
    public struct ServerAniSelectionSet : IComponentData
    {
        // 提交该选择集的连接，用于断线清理和发送确认
        public Entity SourceConnection;

        // 玩家网络编号，是服务器选择集的稳定业务键
        public int OwnerNetworkId;

        // 玩家最近一次完整发布的非零递增版本
        public uint Version;

        // 版本、成员数和有序 GhostId 共同生成的完整性 Hash
        public ulong CompletenessHash;

        // 与成员 Buffer 长度保持一致，供命令入口快速检查
        public int MemberCount;
    }

    /// <summary>
    /// 保存权威选择集内唯一且按 GhostId 升序排列的 Ani
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ServerAniSelectionMember : IBufferElementData
    {
        // 按严格升序保存，确保跨分块到达顺序仍得到相同快照
        public int GhostId;

        // 发布时解析出的服务器 Ani Entity
        public Entity Ani;
    }

    /// <summary>
    /// 记录尚未收齐的选择集版本及其协议元数据
    /// </summary>
    public struct ServerAniSelectionAssembly : IComponentData
    {
        // 首块来源连接，后续用于断线判断和确认回执
        public Entity SourceConnection;

        // 同一玩家同时只保留一个正在组装的选择版本
        public int OwnerNetworkId;

        // 当前正在收集的客户端提交版本
        public uint Version;

        // 指明 payload 应替换、增加、移除还是清空现有选择
        public AniSelectionUpdateMode Mode;

        // 客户端声明并经过包络校验的总分块数
        public ushort ChunkCount;

        // 已接收的唯一分块数，达到总数后才允许发布
        public ushort ReceivedChunkCount;

        // 所有分块携带成员的声明总数
        public int PayloadMemberCount;

        // 应用更新模式后预期得到的最终成员数
        public int ResultMemberCount;

        // 客户端对最终有序选择集计算的完整性 Hash
        public ulong ResultHash;

        // 最近一次收到新块的服务器更新序号，用于超时回收
        public uint LastReceivedTick;
    }

    /// <summary>
    /// 记录组装期间已经收到的分块及其内容 Hash
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ServerAniSelectionAssemblyChunk : IBufferElementData
    {
        // 唯一标识该版本内的块位置
        public ushort ChunkIndex;

        // 用于区分幂等重传和相同位置的内容冲突
        public ulong ChunkHash;
    }

    /// <summary>
    /// 保存分块中的成员和块内顺序，供重复检查与最终排序使用
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ServerAniSelectionAssemblyMember : IBufferElementData
    {
        // 保留来源块位置，支持乱序接收和重复块核对
        public ushort ChunkIndex;

        // 保留块内位置，确保重复块必须逐项完全一致
        public ushort MemberIndex;

        // 分块实际携带的 Ani GhostId
        public int GhostId;
    }

    /// <summary>
    /// 汇总选择集协议的完成、拒绝和清理次数，供验收与运行时诊断读取
    /// </summary>
    public struct ServerAniSelectionMetrics : IComponentData
    {
        // 成功发布并返回确认的选择版本数量
        public int CompletedSelectionCount;

        // 包络、元数据或最终 Hash 不一致的请求数量
        public int RejectedMalformedCount;

        // 成员重复或同位置分块内容冲突的请求数量
        public int RejectedDuplicateCount;

        // 连接失效、成员不存在或成员不属于玩家的请求数量
        public int RejectedUnauthorizedCount;

        // 版本不高于已发布版本或低于正在组装版本的请求数量
        public int RejectedStaleCount;

        // 被新版本替代或等待超时仍未收齐的组装数量
        public int RejectedIncompleteCount;

        // 内容完全一致的重复块数量，这类重传不会使组装失败
        public int IgnoredDuplicateChunkCount;
    }

    /// <summary>
    /// 保存服务器从选择集快照生成的高层移动命令
    /// </summary>
    public struct AniMovementOrder : IComponentData
    {
        // 服务器分配的命令序号，供后续 Cohort 保持稳定处理顺序
        public uint Sequence;

        // 发出命令的玩家网络编号
        public int OwnerNetworkId;

        // 生成成员快照时使用的权威选择集版本
        public uint SelectionVersion;

        // 与选择版本共同阻止移动命令借用错误成员
        public ulong SelectionHash;

        // 记录请求进入服务器模拟的 Tick，供超时、诊断和确定性排序使用
        public uint CreatedTick;

        // 新命令替换旧归属时使用的版本，后续取消链路不需要改写成员 Buffer
        public uint CancellationVersion;

        // 高优先级请求可以在后续预算调度中优先取得寻路名额
        public byte Priority;

        // 对下游表达 MoveTo、Follow 或其他高层移动语义
        public AniSquadCommandMode Mode;

        // 地面命令或目标 Entity 当前对应的世界位置
        public float3 TargetPosition;

        // Follow 等命令引用的服务器目标，纯地面命令为空
        public Entity TargetEntity;

        // 下游判断到达时使用的业务停止距离
        public float TargetStoppingDistance;

        // 控制目标格子可容纳的 Ani 数量，默认值 1 表示采用真实几何容量
        public float GoalCellCapacityScale;

        // 进入该范围后从共享 Flow Direction 平滑转向自己的目标落点
        public float GoalInfluenceRadius;
    }

    /// <summary>
    /// 标记尚未由阶段六移动链路消费的 MovementOrder Entity
    /// </summary>
    public struct AniMovementOrderRequest : IComponentData
    {
    }

    /// <summary>
    /// 保存 MovementOrder 创建时冻结的唯一成员快照
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct AniMovementOrderMember : IBufferElementData
    {
        // 成员按 GhostId 升序冻结，避免之后选择变化污染已下达命令
        public int GhostId;

        // 命令生成时再次通过权限和存活校验的 Ani Entity
        public Entity Ani;

        // 移动能力在请求创建时冻结，Cohort 不再回读易变的玩法属性
        public float MaxSpeed;
        public float MaxAcceleration;
        public float AgentRadius;

        // 相同通行配置的成员才能共用 Cohort 寻路上下文
        public uint AgentProfile;
    }
}
