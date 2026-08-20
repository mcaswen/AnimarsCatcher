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
        public int Tick;
        public int NextCommandIndex;
        public int AppliedCommandCount;
        public int FinalArrivalCount;
        public int FinalMemberCount;
        public uint NextCommandSequence;
        public long FrameStartTimestamp;
        public long FrameStartAllocatedBytes;
        public byte Initialized;
        public byte Completed;
        public byte ResultExported;
        public byte RecordCurrentTick;
        public byte Failed;
    }

    /// <summary>
    /// 保存阶段四完整 Server Simulation Tick 样本
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationGridMovementBenchmarkTimingSample : IBufferElementData
    {
        public double ServerSimulationMilliseconds;
        public long MainThreadAllocatedBytes;
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
