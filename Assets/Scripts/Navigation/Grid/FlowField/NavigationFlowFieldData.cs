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

        /// <summary>
        /// 根据普通路径请求创建 Flow Field 请求
        /// </summary>
        /// <param name="pathRequest">包含起终点、角色体型和版本号的路径请求</param>
        /// <returns>可提交给 Flow Field 系统的请求组件</returns>
        public static NavigationFlowFieldRequest Create(NavigationPathRequest pathRequest)
        {
            return new NavigationFlowFieldRequest { PathRequest = pathRequest };
        }
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
