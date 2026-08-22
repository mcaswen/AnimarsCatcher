using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 区分历史基线、阶段六输入校验和后续万人移动工作负载
    /// </summary>
    public enum NavigationGridBenchmarkWorkload : byte
    {
        PathAndField = 0,
        StrictFormationBaseline = 1,
        ScaleInputDeterminism = 2,
        FreeCohortMovement = 3,
        Avoidance = 4,
        Collision = 5
    }

    /// <summary>
    /// 统一导航基准场景的请求规模、回放时间和结果标识配置
    /// </summary>
    public struct NavigationGridBenchmarkConfig : IComponentData
    {
        public NavigationGridBenchmarkWorkload Workload;

        // 本次创建的独立寻路或 Flow Field 请求数
        public int AgentCount;

        // 生成位置和回放数据使用的固定随机种子
        public int RandomSeed;

        // 正式采样前预热的服务器帧数
        public int WarmupTicks;

        // 正式采样持续的服务器帧数
        public int SampleTicks;

        // 是否记录 R6 队伍和成员逐帧轨迹；默认关闭，避免影响性能基线
        public byte RecordMovementTrace;

        // 请求起点阵列每行最多放置的数量
        public int SpawnColumnCount;

        // 相邻请求起点的世界距离
        public float SpawnSpacing;

        // 请求起点阵列和目标偏移共用的世界坐标原点
        public float3 SpawnOrigin;

        // 所有基准请求采用的角色半径
        public float AgentRadius;

        // 结果对应的 Git 提交标识
        public FixedString64Bytes GitCommit;

        // 基准场景内容哈希
        public FixedString128Bytes MapSceneHash;

        // 目标回放序列内容哈希
        public FixedString128Bytes ReplayScriptHash;

        // 批处理结束后是否自动退出应用
        public byte AutoQuit;
    }

    /// <summary>
    /// 寻路与 Flow Field 基准在多帧回放期间的进度和累计结果
    /// </summary>
    public struct NavigationGridBenchmarkState : IComponentData
    {
        // 从配置载入后累计的服务器帧数
        public int Tick;

        // 下一条尚未提交的回放命令索引
        public int NextCommandIndex;

        // 回放期间实际提交的请求版本数
        public int SubmittedRequestCount;

        // 已完成统计的请求版本数，每个版本只计一次
        public int CompletedRequestCount;

        // 直接复用 Flow Field 缓存的请求数
        public int CacheHitCount;

        // 成功生成宏观通道和 Flow Field 的请求数
        public int SucceededRequestCount;

        // 失败的请求版本数
        public int FailedRequestCount;

        // 分层寻路累计展开的抽象节点数
        public int TotalAbstractExpandedNodeCount;

        // Integration Field 累计展开的格子数
        public int TotalIntegrationExpandedCellCount;

        // 表示请求 Entity 是否已经按配置创建
        public byte Initialized;

        // 采样窗口和所有异步请求是否都已结束
        public byte Completed;

        // 结果是否已经写入磁盘
        public byte ResultExported;

        // 当前帧 Flow Field 系统主线程计时的起点
        public long FlowFieldStartTimestamp;

        // 当前帧是否正在采样 Flow Field 主线程耗时
        public byte RecordFlowFieldTiming;
    }

    /// <summary>
    /// 队伍移动基准的回放进度、计时状态和最终结果
    /// </summary>
    public struct NavigationGridMovementBenchmarkState : IComponentData
    {
        // 回放已经推进的服务器帧数
        public int Tick;

        // 下一条尚未转成队伍指令的回放命令索引
        public int NextCommandIndex;

        // 已提交的队伍 MoveTo 指令数
        public int AppliedCommandCount;

        // 队伍首次完成的帧，仅用于诊断，不会提前结束固定采样窗口
        public int CompletionTick;

        // 固定结束帧已到达的成员数
        public int FinalArrivalCount;

        // 固定结束帧仍然有效的成员总数
        public int FinalMemberCount;

        // 下一个回放指令序号；0 保留为未初始化
        public uint NextCommandSequence;

        // 当前完整服务器帧的计时起点
        public long FrameStartTimestamp;

        // 当前帧开始时本线程累计的托管内存分配量
        public long FrameStartAllocatedBytes;

        // 是否已经创建基准 Ani 并提交第一条回放指令
        public byte Initialized;

        // 是否已到固定结束帧，正等待下一轮更新后导出
        public byte Completed;

        // 报告是否已经写入磁盘
        public byte ResultExported;

        // 本帧结束时是否需要记录计时样本
        public byte RecordCurrentTick;

        // 固定时间窗口结束时功能是否仍未完成
        public byte Failed;

        // 失败报告路径，供批处理运行器返回非零退出码
        public FixedString128Bytes FailureReason;
    }

    /// <summary>
    /// 阶段六规模输入校验的进度、确定性 Hash 和内存统计
    /// </summary>
    public struct NavigationGridScaleInputBenchmarkState : IComponentData
    {
        // 已推进的服务器帧数
        public int Tick;

        // 按硬上限切分后的 Cohort 数量
        public int CohortCount;

        // 输入中生成的唯一 Field Key 数量
        public int UniqueFieldKeyCount;

        // Cohort 成员归属的确定性 Hash
        public ulong CohortPartitionHash;

        // 目标区域采样结果的确定性 Hash
        public ulong GoalRegionHash;

        // 请求 Key 序列的确定性 Hash
        public ulong RequestKeyHash;

        // Benchmark 自有 DynamicBuffer 的 Native 负载字节数
        public long TrackedNativeBytes;

        // 是否已经生成全部规模输入
        public byte Initialized;

        // 是否已经完成固定采样窗口
        public byte Completed;

        // 报告是否已经写入磁盘
        public byte ResultExported;

        // 同一轮重复计算是否发现不一致
        public byte Failed;

        // 失败原因由批处理运行器写入最终日志
        public FixedString128Bytes FailureReason;
    }

    /// <summary>
    /// 一帧完整服务器模拟的耗时和托管内存分配样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkTimingSample : IBufferElementData
    {
        // 完整服务器帧的实际耗时
        public double ServerSimulationMilliseconds;

        // 同一帧主线程新增的托管内存分配量
        public long MainThreadAllocatedBytes;
    }

    /// <summary>
    /// 阶段六报告中单个导航阶段的主线程、Worker 和排队样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridBenchmarkStageTimingSample : IBufferElementData
    {
        // 指标所属的导航阶段
        public NavigationGridBenchmarkStage Stage;

        // 样本对应的服务器帧，负数表示初始化阶段
        public int Tick;

        // 当前阶段在主线程上的耗时
        public double MainThreadMilliseconds;

        // 当前阶段等待并完成 Worker Job 的耗时
        public double WorkerMilliseconds;

        // 请求从入队到开始处理所等待的服务器帧数
        public int QueueWaitTicks;

        // 当前阶段可明确归属的 Native 内存字节数
        public long TrackedNativeBytes;
    }

    /// <summary>
    /// 标识阶段六报告中的导航处理阶段
    /// </summary>
    public enum NavigationGridBenchmarkStage : byte
    {
        ScaleInputBuild,
        CommandIngress,
        CohortPartition,
        OverlayAndTargetResolve,
        FieldRequestCollect,
        FieldBuildAndPublish,
        GoalRegionAssignment,
        NeighborGrid,
        PreferredVelocity,
        Avoidance,
        Collision,
        CommitAndProgress
    }

    /// <summary>
    /// R6 可选诊断中记录的一帧队伍状态
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkStateTrace : IBufferElementData
    {
        // 对应的服务器帧
        public int Tick;

        // 当前队伍编号
        public uint SquadId;

        // 当前队伍路径状态
        public byte PathStatus;

        // 采样时基准是否已经失败
        public byte BenchmarkFailed;

        // 当前队伍使用的 Flow Field 请求版本
        public uint ActiveRequestVersion;

        // 连续满足到达条件的帧数
        public int SettledTicks;

        // 当前队伍成员数
        public int MemberCount;

        // 索引有效的槽位数
        public int AssignedSlotCount;

        // 越界或无效的槽位数
        public int InvalidSlotCount;

        // 同时满足位置和速度阈值的成员数
        public int ArrivedCount;

        // 队伍锚点是否进入目标停止范围
        public byte AnchorArrived;

        // 所有成员是否都满足到达条件
        public byte MembersArrived;

        // 队伍锚点到当前目标的水平距离
        public float AnchorDistanceToTarget;

        // 所有成员中最大的槽位误差
        public float MaximumMemberDistance;

        // 所有成员中最大的实际速度平方
        public float MaximumMemberSpeedSquared;

        // 所有成员累计的 Transform 提交次数
        public long TransformWriteCount;

        // 本帧采用的目标位置
        public float3 TargetPosition;

        // 本帧队伍锚点的世界坐标
        public float3 AnchorPosition;

        // 本帧队伍锚点的水平速度
        public float3 AnchorVelocity;

        // 本帧锚点所在的有效格子索引
        public int AnchorCellIndex;
    }

    /// <summary>
    /// R6 可选诊断中记录的一帧成员槽位和实际移动结果
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkAgentTrace : IBufferElementData
    {
        // 对应的服务器帧
        public int Tick;

        // 基准 Ani 的固定序号
        public int AgentIndex;

        // 成员当前占用的阵型槽位
        public int SlotIndex;

        // 成员槽位的世界坐标目标
        public float3 SlotTargetPosition;

        // 移动提交系统写回后的实际位置
        public float3 TransformPosition;

        // 本帧实际采用的水平速度
        public float3 AppliedVelocity;

        // 移动后到槽位的水平距离
        public float DistanceToSlot;

        // 该成员累计的 Transform 提交次数
        public uint CommitCount;
    }

    /// <summary>
    /// 一次 Flow Field 系统主线程更新的耗时样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridBenchmarkTimingSample : IBufferElementData
    {
        // Flow Field 系统从开始到结束的主线程耗时
        public double FlowFieldMilliseconds;
    }

    /// <summary>
    /// 在回放的指定帧提交一个新的目标位置
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationGridBenchmarkCommand : IBufferElementData
    {
        // 相对采样窗口起点的提交帧
        public int Tick;

        // 相对 SpawnOrigin 的目标位置偏移
        public float3 TargetOffset;
    }

    /// <summary>
    /// 标识由 Grid Benchmark 创建的请求 Entity 及其最近计数版本
    /// </summary>
    public struct NavigationGridBenchmarkRequestTag : IComponentData
    {
        // 用于计算阵列起点位置的固定序号
        public int AgentIndex;

        // 最近一次已统计的请求版本，防止后续帧重复计数
        public uint CountedVersion;
    }

    /// <summary>
    /// 标记由队伍移动基准创建的 Ani
    /// </summary>
    public struct NavigationGridMovementBenchmarkAni : IComponentData
    {
        public int AgentIndex;
    }

    /// <summary>
    /// 保存阶段六万人入口生成的稳定成员、Cohort 和目标区域输入
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridScaleInputMember : IBufferElementData
    {
        // 与 Entity 创建顺序无关的成员编号
        public int StableId;

        // 按固定容量得到的 Cohort 编号
        public int CohortIndex;

        // 由共享 Benchmark 算法生成的出生位置
        public float3 SpawnPosition;

        // 目标区域中的稳定采样位置，不代表固定阵型槽位
        public float3 GoalPosition;

        // 当前 Cohort 对应的规划请求 Key
        public ulong RequestKey;
    }
}
