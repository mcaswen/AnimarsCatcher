using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 请求先用分层寻路确定大致通道，再为通道内格子生成 Flow Field
    /// </summary>
    public struct NavigationFlowFieldRequest : IComponentData
    {
        // 复用普通路径请求中的起终点、角色体型、成本参数和版本号
        public NavigationPathRequest PathRequest;

        // 预算不足时优先处理数值更高的请求
        public byte Priority;

        // 请求被后续版本替换后，调度器用该版本拒绝迟到结果
        public uint CancellationVersion;

        /// <summary>
        /// 根据普通路径请求创建 Flow Field 请求
        /// </summary>
        /// <param name="pathRequest">包含起终点、角色体型和版本号的路径请求</param>
        /// <returns>可提交给 Flow Field 系统的请求组件</returns>
        public static NavigationFlowFieldRequest Create(
            NavigationPathRequest pathRequest,
            byte priority = 0,
            uint cancellationVersion = 0)
        {
            return new NavigationFlowFieldRequest
            {
                PathRequest = pathRequest,
                Priority = priority,
                CancellationVersion = cancellationVersion,
            };
        }
    }

    /// <summary>
    /// 标识可以共享同一份 Corridor 与 Flow Field 的确定性请求键
    /// </summary>
    public struct NavigationFlowFieldKey : IEquatable<NavigationFlowFieldKey>
    {
        // 使用投影后的起终点，世界坐标微小误差不会拆成不同缓存项
        public int StartCellIndex;
        public int EndCellIndex;

        // 浮点参数按位参与相等比较，避免近似比较破坏哈希容器约定
        public int RequiredClearanceBits;
        public int ClearancePenaltyWeightBits;

        public bool Equals(NavigationFlowFieldKey other)
        {
            return StartCellIndex == other.StartCellIndex &&
                   EndCellIndex == other.EndCellIndex &&
                   RequiredClearanceBits == other.RequiredClearanceBits &&
                   ClearancePenaltyWeightBits == other.ClearancePenaltyWeightBits;
        }

        public override bool Equals(object obj)
        {
            return obj is NavigationFlowFieldKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)math.hash(new int4(
                StartCellIndex,
                EndCellIndex,
                RequiredClearanceBits,
                ClearancePenaltyWeightBits));
        }
    }

    /// <summary>
    /// 让 Cohort 引用共享 Store 中的记录而不是持有完整 Flow Field 副本
    /// </summary>
    public struct NavigationFlowFieldHandle : IComponentData
    {
        // 共享 Entity 真正持有 Corridor 和 Field Buffer，Handle 只保存它的引用
        public Entity Record;

        // 两级版本分别防止 Record 换代和迟到请求误用旧结果
        public uint RecordVersion;
        public uint RequestVersion;
    }

    /// <summary>
    /// 记录一份共享 Flow Field 的键、有效版本、内存占用和引用情况
    /// </summary>
    public struct NavigationSharedFlowFieldRecord : IComponentData
    {
        // Key 决定可共享范围，RecordVersion 标识这一份不可变结果
        public NavigationFlowFieldKey Key;
        public uint RecordVersion;

        // 签名只覆盖实际 Corridor，Source 版本用于报告构建时看到的 Overlay
        public uint DynamicOverlaySignature;
        public uint SourceOverlayVersion;

        // 引用数保护活动消费者，使用时间和字节数参与缓存淘汰
        public int ReferenceCount;
        public int LastUsedTick;
        public int ByteSize;

        // 保留求解成本和搜索规模，Handle 命中时可直接恢复 Cohort 状态
        public int AbstractExpandedNodeCount;
        public int IntegrationExpandedCellCount;
        public float TotalCost;
    }

    /// <summary>
    /// 保存 Cohort 请求在预算队列中的版本与等待时间
    /// </summary>
    public struct NavigationFlowFieldQueueState : IComponentData
    {
        // 请求和取消版本任一变化都会开启新的排队生命周期
        public uint RequestVersion;
        public uint CancellationVersion;

        // 负值表示尚未进入对应阶段，完成后 QueueWaitTicks 固化报告样本
        public int EnqueuedTick;
        public int StartedTick;
        public int CompletedTick;
        public int QueueWaitTicks;
    }

    /// <summary>
    /// 配置共享 Field 的并发数、每 Tick 预算、超时和内存上限
    /// </summary>
    public struct NavigationFlowFieldSchedulerSettings : IComponentData
    {
        // 并发数限制工作区数量，每 Tick 上限控制构建尖峰
        public int MaximumConcurrentBuilds;
        public int MaximumBuildsPerTick;

        // 超时只约束排队等待，字节预算只淘汰没有活动引用的 Record
        public int RequestTimeoutTicks;
        public long StoreByteBudget;

        /// <summary>
        /// 创建适合普通服务器运行的共享 Field 调度默认值
        /// </summary>
        public static NavigationFlowFieldSchedulerSettings CreateDefault()
        {
            return new NavigationFlowFieldSchedulerSettings
            {
                MaximumConcurrentBuilds = 4,
                MaximumBuildsPerTick = 4,
                RequestTimeoutTicks = 120,
                StoreByteBudget = 256L * 1024L * 1024L,
            };
        }
    }

    /// <summary>
    /// 汇总共享 Store、预算队列和构建任务的运行时指标
    /// </summary>
    public struct NavigationFlowFieldSchedulerState : IComponentData
    {
        // 当前值用于观察调度器压力，不跨运行周期累积
        public int Tick;
        public int QueueLength;
        public int ActiveBuildCount;
        public int StoreRecordCount;
        public long StoreByteCount;
        public int LastPublishedBuildCount;

        // 累计值用于比较唯一构建、共享收益和失败路径
        public int CumulativeUniqueBuildCount;
        public int CumulativeSharedHitCount;
        public int CumulativeCancelledCount;
        public int CumulativeTimeoutCount;
        public int CumulativeEvictedCount;
    }

    /// <summary>
    /// 保存已结束请求的排队时长，供 Benchmark 计算 P50、P95 和 P99
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationFlowFieldQueueWaitSample : IBufferElementData
    {
        // 等待时间只统计入队到开工，Outcome 区分成功、取消和超时
        public int WaitTicks;
        public NavigationPathStatus Outcome;
    }

    /// <summary>
    /// 记录分层路线、局部 Flow Field 和缓存的处理结果
    /// </summary>
    public struct NavigationFlowFieldState : IComponentData
    {
        // 请求当前处于等待、计算、成功还是失败状态
        public NavigationPathStatus Status;

        // 失败原因；处理过程中或成功时为 None
        public NavigationPathFailureReason FailureReason;

        // 该状态对应的请求版本，用来忽略迟到的旧结果
        public uint RequestVersion;

        // 使用的 Flow Field 缓存版本；未命中时会为新缓存项分配版本
        public uint CacheVersion;

        // 起点纠正后的格子索引；纠正失败时为负数
        public int ProjectedStartCellIndex;

        // 终点纠正后的格子索引；纠正失败时为负数
        public int ProjectedEndCellIndex;

        // 宏观通道经过的分块数
        public int CorridorClusterCount;

        // 宏观通道穿过的分块入口数
        public int CorridorPortalCount;

        // 分层路线包含的宏观路点数
        public int HierarchicalWaypointCount;

        // 局部 Flow Field 覆盖的格子数
        public int FieldCellCount;

        // 分层寻路实际展开的抽象节点数
        public int AbstractExpandedNodeCount;

        // Integration Field 计算实际展开的格子数
        public int IntegrationExpandedCellCount;

        // 纠正后起终点之间宏观路线的静态成本
        public float TotalCost;

        // 是否直接复用了已有的 Flow Field 缓存
        public byte CacheHit;
        public uint DynamicOverlayVersion;

        /// <summary>
        /// 为指定版本创建等待处理的初始状态
        /// </summary>
        /// <param name="requestVersion">与请求组件一致的版本号</param>
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
    /// Flow Field 中一个格子的剩余成本，以及通往目标的下一步方向
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct NavigationFlowFieldCell : IBufferElementData
    {
        // 对应静态导航网格中的格子
        public int CellIndex;

        // 从该格子到目标的最低累计成本
        public float IntegrationCost;

        // 在 XZ 平面上指向下一格的单位方向
        public float2 Direction;
    }

    /// <summary>
    /// 一项可供后续请求复用的 Flow Field 缓存索引
    /// </summary>
    public struct NavigationFlowFieldCacheEntry
    {
        // 缓存对应的目标格子
        public int TargetCellIndex;

        // 所需空间的精确浮点位；不同体型的请求不能因近似比较而共用缓存
        public int RequiredClearanceBits;

        // 狭窄空间惩罚权重的精确浮点位
        public int ClearancePenaltyWeightBits;

        // 按经过顺序计算的通道分块哈希
        public uint CorridorHash;

        // 该通道在全局缓存列表中的起始位置
        public int CorridorOffset;

        // 通道包含的分块数
        public int CorridorCount;

        // 该 Flow Field 在全局缓存列表中的起始位置
        public int FieldOffset;

        // Flow Field 包含的格子数
        public int FieldCount;

        // 缓存项的递增版本号
        public uint CacheVersion;

        // 通道内各分块动态障碍版本的签名；通道外发生变化不会让该缓存失效
        public uint DynamicOverlaySignature;
    }
}
