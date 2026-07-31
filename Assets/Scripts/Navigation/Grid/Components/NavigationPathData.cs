using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 定义路径请求从等待到完成的生命周期状态
    /// </summary>
    public enum NavigationPathStatus : byte
    {
        None,
        Pending,
        Searching,
        Succeeded,
        Failed,
        Cancelled,
    }

    /// <summary>
    /// 定义路径搜索失败或终止的稳定原因
    /// </summary>
    public enum NavigationPathFailureReason : byte
    {
        None,
        InvalidGrid,
        InvalidRequest,
        StartProjectionFailed,
        EndProjectionFailed,
        RegionMismatch,
        NoPath,
        Cancelled,
    }

    /// <summary>
    /// 描述一次确定性 Grid 路径请求
    /// </summary>
    public struct NavigationPathRequest : IComponentData
    {
        // 起终点保留世界坐标，由路径服务统一执行 Bounds 检查和端点投影
        public float3 StartPosition;
        public float3 EndPosition;

        // BaseAgentRadius 已在烘焙时参与占用采样，运行时只额外扣除超出部分
        public float AgentRadius;
        public float ClearanceMargin;

        // ClearancePenaltyWeight 只改变偏好，不会把不可占用 Cell 变为可用
        public float ClearancePenaltyWeight;

        // 平滑只能在此比例内增加原始 A 星分段成本
        public float SmoothingCostTolerance;

        // 投影半径以 Cell 为单位，零表示只接受位置直接对应的 Cell
        public int MaximumProjectionRadiusInCells;

        // 调用方每次替换请求时递增版本，用于拒绝异步返回的旧结果
        public uint Version;

        /// <summary>
        /// 使用阶段二推荐默认值创建路径请求
        /// </summary>
        /// <param name="startPosition">请求起点世界坐标</param>
        /// <param name="endPosition">请求终点世界坐标</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="version">调用方维护的请求版本</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <param name="maximumProjectionRadiusInCells">端点投影最大搜索半径</param>
        /// <param name="clearancePenaltyWeight">低 Clearance 路径惩罚权重</param>
        /// <param name="smoothingCostTolerance">平滑路径允许增加的成本比例</param>
        /// <returns>状态系统可直接消费的路径请求</returns>
        public static NavigationPathRequest Create(
            float3 startPosition,
            float3 endPosition,
            float agentRadius,
            uint version,
            float clearanceMargin = 0f,
            int maximumProjectionRadiusInCells = 8,
            float clearancePenaltyWeight = 0.2f,
            float smoothingCostTolerance = 0.02f)
        {
            return new NavigationPathRequest
            {
                StartPosition = startPosition,
                EndPosition = endPosition,
                AgentRadius = math.max(0f, agentRadius),
                ClearanceMargin = math.max(0f, clearanceMargin),
                ClearancePenaltyWeight = math.max(0f, clearancePenaltyWeight),
                SmoothingCostTolerance = math.max(0f, smoothingCostTolerance),
                MaximumProjectionRadiusInCells = math.max(
                    0,
                    maximumProjectionRadiusInCells),
                Version = version,
            };
        }
    }

    /// <summary>
    /// 保存路径请求的投影结果、搜索统计和失败状态
    /// </summary>
    public struct NavigationPathState : IComponentData
    {
        // Pending 由调用方提交 Searching 由路径系统独占 Succeeded 和 Failed 为终态
        public NavigationPathStatus Status;
        public NavigationPathFailureReason FailureReason;

        // RequestVersion 记录当前状态对应的请求，避免只比较 Component 当前值
        public uint RequestVersion;

        // 投影索引便于调试调用方输入和 Region 预拒绝结果
        public int ProjectedStartCellIndex;
        public int ProjectedEndCellIndex;

        // WaypointCount 与 Buffer 长度保持一致，便于只读查询和统计
        public int WaypointCount;

        // ExpandedNodeCount 和 TotalCost 用于正确性对照与后续性能基线
        public int ExpandedNodeCount;
        public float TotalCost;

        /// <summary>
        /// 创建等待指定版本请求的初始状态
        /// </summary>
        /// <param name="requestVersion">当前路径请求版本</param>
        /// <returns>可加入请求 Entity 的等待状态</returns>
        public static NavigationPathState CreatePending(uint requestVersion)
        {
            return new NavigationPathState
            {
                Status = NavigationPathStatus.Pending,
                FailureReason = NavigationPathFailureReason.None,
                RequestVersion = requestVersion,
                ProjectedStartCellIndex = -1,
                ProjectedEndCellIndex = -1,
                WaypointCount = 0,
                ExpandedNodeCount = 0,
                TotalCost = 0f,
            };
        }
    }

    /// <summary>
    /// 保存平滑后路径中的稳定 Cell 和对应世界坐标
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct NavigationPathWaypoint : IBufferElementData
    {
        // CellIndex 是确定性结果 Position 是便于下游移动消费的烘焙表面坐标
        public int CellIndex;
        public float3 Position;
    }
}
