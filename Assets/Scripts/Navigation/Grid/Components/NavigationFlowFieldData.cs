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
    /// 描述一次 HPA 星 Corridor 和局部 Flow Field 请求
    /// </summary>
    public struct NavigationFlowFieldRequest : IComponentData
    {
        // 保存复用端点投影、体型和代价参数的底层路径请求
        public NavigationPathRequest PathRequest;

        /// <summary>
        /// 从普通路径请求创建分层路径与局部场请求
        /// </summary>
        /// <param name="pathRequest">包含端点、体型和请求版本的路径契约</param>
        /// <returns>等待 Flow Field 系统处理的请求组件</returns>
        public static NavigationFlowFieldRequest Create(NavigationPathRequest pathRequest)
        {
            return new NavigationFlowFieldRequest { PathRequest = pathRequest };
        }
    }

    /// <summary>
    /// 保存分层搜索、局部场和缓存命中的可观察状态
    /// </summary>
    public struct NavigationFlowFieldState : IComponentData
    {
        // 表示请求当前所处的统一路径生命周期状态
        public NavigationPathStatus Status;

        // 保存失败时可稳定检查的原因，成功与处理中为 None
        public NavigationPathFailureReason FailureReason;

        // 标识本状态对应的请求版本，用于拒绝异步旧结果
        public uint RequestVersion;

        // 标识命中的 Field 缓存版本，未命中时由新建缓存分配
        public uint CacheVersion;

        // 保存起点投影后的 Cell 索引，投影失败时为负数
        public int ProjectedStartCellIndex;

        // 保存终点投影后的 Cell 索引，投影失败时为负数
        public int ProjectedEndCellIndex;

        // 记录写回到 Corridor Cluster Buffer 的元素数量
        public int CorridorClusterCount;

        // 记录写回到 Corridor Portal Buffer 的元素数量
        public int CorridorPortalCount;

        // 记录宏观路线写回的层级路点数量
        public int HierarchicalWaypointCount;

        // 记录局部 Field 写回的 Cell 数量
        public int FieldCellCount;

        // 记录 HPA 星抽象搜索展开的节点数量
        public int AbstractExpandedNodeCount;

        // 记录 Integration 搜索展开的 Cell 数量
        public int IntegrationExpandedCellCount;

        // 保存投影端点之间层级路线的累计静态成本
        public float TotalCost;

        // 表示结果是否直接复用了已生成的局部 Field
        public byte CacheHit;
        public uint DynamicOverlayVersion;

        /// <summary>
        /// 创建尚未调度的状态并清空所有投影结果
        /// </summary>
        /// <param name="requestVersion">需要与请求组件保持一致的版本</param>
        /// <returns>可由 Flow Field 系统认领的 Pending 状态</returns>
        public static NavigationFlowFieldState CreatePending(uint requestVersion)
        {
            return new NavigationFlowFieldState
            {
                Status = NavigationPathStatus.Pending,
                FailureReason = NavigationPathFailureReason.None,
                RequestVersion = requestVersion,
                ProjectedStartCellIndex = -1,
                ProjectedEndCellIndex = -1,
            };
        }
    }

    /// <summary>
    /// 按宏观路线顺序保存请求经过的 Cluster
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationCorridorCluster : IBufferElementData
    {
        // 引用 Grid Blob 中的 Cluster 索引
        public int ClusterId;
    }

    /// <summary>
    /// 按跨越顺序保存 Corridor 经过的 Portal
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationCorridorPortal : IBufferElementData
    {
        // 引用 Grid Blob 中的 Portal 索引
        public int PortalIndex;
    }

    /// <summary>
    /// 保存端点投影与 Portal 代表点构成的宏观路点
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct NavigationHierarchicalWaypoint : IBufferElementData
    {
        // 引用路点对应的 Grid Cell 索引
        public int CellIndex;

        // 保存 Cell 地面高度上的世界空间位置
        public float3 Position;
    }

    /// <summary>
    /// 保存 Corridor 内单个 Cell 的 Integration 成本和下降方向
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationFlowFieldCell : IBufferElementData
    {
        // 引用 Grid Blob 中的 Cell 索引
        public int CellIndex;

        // 表示从当前 Cell 到投影目标的累计移动成本
        public float IntegrationCost;

        // 表示 XZ 平面上指向更低 Integration 成本邻居的单位方向
        public float2 Direction;
    }

    /// <summary>
    /// 保存跨请求复用的局部场缓存元数据
    /// </summary>
    public struct NavigationFlowFieldCacheEntry
    {
        // 保存缓存对应的投影目标 Cell
        public int TargetCellIndex;

        // 保存所需 Clearance 浮点值的位表示，避免近似比较污染缓存键
        public int RequiredClearanceBits;

        // 保存 Clearance 惩罚权重的位表示
        public int ClearancePenaltyWeightBits;

        // 保存按顺序计算的 Corridor Cluster Hash
        public uint CorridorHash;

        // 指向系统持有的缓存 Corridor 列表切片起点
        public int CorridorOffset;

        // 保存缓存 Corridor 切片长度
        public int CorridorCount;

        // 指向系统持有的缓存 Field 列表切片起点
        public int FieldOffset;

        // 保存缓存 Field 切片长度
        public int FieldCount;

        // 标识该缓存项的稳定递增版本
        public uint CacheVersion;

        // 保存 Corridor 内 Cluster Overlay 版本的确定性签名
        // 只要 Corridor 外的 Cluster 变化，该缓存仍可复用
        public uint DynamicOverlaySignature;
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
}
