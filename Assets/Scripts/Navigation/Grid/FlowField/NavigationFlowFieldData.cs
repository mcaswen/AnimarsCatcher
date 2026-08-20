using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
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
}
