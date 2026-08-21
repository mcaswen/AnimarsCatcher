using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 区分阶段三纯路径工作负载和阶段四群体移动工作负载
    /// </summary>
    public enum NavigationGridBenchmarkWorkload : byte
    {
        SquadMovement,
        PathAndField
    }

    /// <summary>
    /// 由统一 Benchmark 场景加载器注册的 Grid 路径与 Field 工作负载
    /// </summary>
    public struct NavigationGridBenchmarkConfig : IComponentData
    {
        public NavigationGridBenchmarkWorkload Workload;

        // 指定本次生成的独立路径与 Field 请求数量
        public int AgentCount;

        // 固定生成位置和回放使用的随机种子
        public int RandomSeed;

        // 指定回放采样前等待的 Server Tick 数量
        public int WarmupTicks;

        // 指定执行确定性回放的采样 Tick 数量
        public int SampleTicks;

        // 表示是否记录 R6 群体移动诊断轨迹，默认关闭以保持性能基线不受影响
        public byte RecordMovementTrace;

        // 指定请求起点阵列每行的最大数量
        public int SpawnColumnCount;

        // 指定相邻请求起点之间的世界空间距离
        public float SpawnSpacing;

        // 指定请求起点阵列和目标偏移共同使用的世界空间原点
        public float3 SpawnOrigin;

        // 指定全部 Benchmark 请求使用的 Agent 半径
        public float AgentRadius;

        // 保存结果所属的 Git 提交标识
        public FixedString64Bytes GitCommit;

        // 保存统一 Benchmark 场景的内容 Hash
        public FixedString128Bytes MapSceneHash;

        // 保存确定性目标回放的内容 Hash
        public FixedString128Bytes ReplayScriptHash;

        // 表示批处理完成后是否由系统退出应用
        public byte AutoQuit;
    }

    /// <summary>
    /// 保存 Grid Benchmark 跨 Tick 执行状态与累计统计
    /// </summary>
    public struct NavigationGridBenchmarkState : IComponentData
    {
        // 保存从配置注册开始累计的 Server Tick
        public int Tick;

        // 指向下一条尚未提交的回放命令
        public int NextCommandIndex;

        // 累计回放阶段向请求实体提交的版本数量
        public int SubmittedRequestCount;

        // 累计已经进入终态且只计数一次的请求版本数量
        public int CompletedRequestCount;

        // 累计直接复用 Field 缓存的请求版本数量
        public int CacheHitCount;

        // 累计成功生成 Corridor 与 Field 的请求版本数量
        public int SucceededRequestCount;

        // 累计以稳定原因失败的请求版本数量
        public int FailedRequestCount;

        // 累计 HPA 星抽象搜索展开节点数量
        public int TotalAbstractExpandedNodeCount;

        // 累计 Integration 搜索展开 Cell 数量
        public int TotalIntegrationExpandedCellCount;

        // 表示请求实体是否已经按配置创建
        public byte Initialized;

        // 表示采样和全部请求写回是否已经结束
        public byte Completed;

        // 表示 Benchmark 结果是否已经写入磁盘
        public byte ResultExported;

        // 保存 Flow Field 系统当前 Tick 的主线程计时起点
        public long FlowFieldStartTimestamp;

        // 表示当前 Tick 是否处于 Flow Field 主线程采样窗口
        public byte RecordFlowFieldTiming;
    }

    /// <summary>
    /// 保存阶段四群体移动 Benchmark 的回放进度和结果状态
    /// </summary>
    public struct NavigationGridMovementBenchmarkState : IComponentData
    {
        // 保存统一回放已经推进的 Server Tick 数量
        public int Tick;

        // 指向下一条尚未转发到 Squad 生命周期的回放命令
        public int NextCommandIndex;

        // 记录已提交的 Squad MoveTo 命令数量
        public int AppliedCommandCount;

        // 记录首次满足终态的 Tick，固定终止前只作为诊断信息
        public int CompletionTick;

        // 保存固定终止 Tick 的到达成员数量
        public int FinalArrivalCount;

        // 保存固定终止 Tick 的有效成员数量
        public int FinalMemberCount;

        // 为回放命令分配不会使用零值的稳定序号
        public uint NextCommandSequence;

        // 保存当前完整 Server Tick 的计时起点
        public long FrameStartTimestamp;

        // 保存当前完整 Server Tick 的托管分配起点
        public long FrameStartAllocatedBytes;

        // 表示 Ani 和第一条回放命令是否已经创建
        public byte Initialized;

        // 表示固定终止 Tick 已到达并等待下一轮导出
        public byte Completed;

        // 表示报告是否已经写入磁盘
        public byte ResultExported;

        // 表示本 Tick 末尾是否需要追加计时样本
        public byte RecordCurrentTick;

        // 表示固定窗口未满足功能终态
        public byte Failed;

        // 保存失败报告供批处理 Runner 返回非零退出码
        public FixedString128Bytes FailureReason;
    }

    /// <summary>
    /// 保存阶段四完整 Server Simulation Tick 样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkTimingSample : IBufferElementData
    {
        // 保存完整 Server Simulation Tick 的墙钟耗时
        public double ServerSimulationMilliseconds;

        // 保存同一 Tick 主线程托管分配的增量
        public long MainThreadAllocatedBytes;
    }

    /// <summary>
    /// 保存 R6 每个 Server Tick 的 Squad 状态诊断轨迹
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkStateTrace : IBufferElementData
    {
        // 轨迹采样对应的统一 Server Tick
        public int Tick;

        // 当前 Squad 的稳定身份
        public uint SquadId;

        // 记录 Squad Path State 的枚举值
        public byte PathStatus;

        // 表示采样时 Benchmark 已进入失败状态
        public byte BenchmarkFailed;

        // 记录当前 Squad 消费的 Flow Field 请求版本
        public uint ActiveRequestVersion;

        // 记录连续满足到达条件的 Tick 数量
        public int SettledTicks;

        // 保存当前 Squad 成员总数
        public int MemberCount;

        // 保存通过范围检查的槽位数量
        public int AssignedSlotCount;

        // 保存越界或无效的槽位数量
        public int InvalidSlotCount;

        // 保存同时满足成员位置和速度门限的数量
        public int ArrivedCount;

        // 表示 Anchor 是否进入指令停止半径
        public byte AnchorArrived;

        // 表示全部成员是否同时进入到达门限
        public byte MembersArrived;

        // 保存 Anchor 到解析目标的水平距离
        public float AnchorDistanceToTarget;

        // 保存成员到各自槽位的最大距离
        public float MaximumMemberDistance;

        // 保存成员应用速度平方的最大值
        public float MaximumMemberSpeedSquared;

        // 保存所有成员累计的唯一 Transform 提交次数
        public long TransformWriteCount;

        // 保存该 Tick 使用的解析目标位置
        public float3 TargetPosition;

        // 保存该 Tick 的 Anchor 世界位置
        public float3 AnchorPosition;

        // 保存该 Tick 的 Anchor 水平速度
        public float3 AnchorVelocity;

        // 保存该 Tick 的合法 Grid Cell 索引
        public int AnchorCellIndex;
    }

    /// <summary>
    /// 保存 R6 每个 Server Tick 的成员槽位和 Transform 提交轨迹
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkAgentTrace : IBufferElementData
    {
        // 轨迹采样对应的统一 Server Tick
        public int Tick;

        // 保存跨查询稳定的 Benchmark Ani 序号
        public int AgentIndex;

        // 保存成员当前分配的阵型槽位
        public int SlotIndex;

        // 保存该成员的世界槽位目标
        public float3 SlotTargetPosition;

        // 保存唯一 Commit 写回后的 Transform 位置
        public float3 TransformPosition;

        // 保存该 Tick 实际应用的水平速度
        public float3 AppliedVelocity;

        // 保存提交后到槽位的水平距离
        public float DistanceToSlot;

        // 保存该成员累计的唯一 Transform 提交次数
        public uint CommitCount;
    }

    /// <summary>
    /// 保存单个 Grid Flow Field 主线程更新的墙钟样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridBenchmarkTimingSample : IBufferElementData
    {
        // 保存从 Flow Field 系统开始到结束的主线程耗时
        public double FlowFieldMilliseconds;
    }

    /// <summary>
    /// 描述采样期指定 Tick 需要广播的新目标偏移
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationGridBenchmarkCommand : IBufferElementData
    {
        // 指定相对采样期起点的提交 Tick
        public int Tick;

        // 指定相对 SpawnOrigin 的目标位置偏移
        public float3 TargetOffset;
    }

    /// <summary>
    /// 标识由 Grid Benchmark 创建的请求实体及其最近计数版本
    /// </summary>
    public struct NavigationGridBenchmarkRequestTag : IComponentData
    {
        // 保存用于稳定计算阵列起点的位置序号
        public int AgentIndex;

        // 保存最近一次纳入统计的请求版本，防止跨 Tick 重复计数
        public uint CountedVersion;
    }

    /// <summary>
    /// 标识 Grid 群体移动 Benchmark 创建的 Ani
    /// </summary>
    public struct NavigationGridMovementBenchmarkAni : IComponentData
    {
        public int AgentIndex;
    }
}
