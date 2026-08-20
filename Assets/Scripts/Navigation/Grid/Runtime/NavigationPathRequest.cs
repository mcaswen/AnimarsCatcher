using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 定义所有路径服务从等待到完成的共享生命周期状态
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
    /// 描述 A*、HPA 和 Flow Field 共用的确定性 Grid 请求
    /// </summary>
    public struct NavigationPathRequest : IComponentData
    {
        // 起终点保留世界坐标，由路径服务统一执行 Bounds 检查和端点投影
        public float3 StartPosition;
        public float3 EndPosition;

        // BaseAgentRadius 已参与烘焙占用采样，运行时只扣除超出的半径
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
        /// 使用 Grid 路径服务的推荐默认值创建路径请求
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
}
