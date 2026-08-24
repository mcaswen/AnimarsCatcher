using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 路径请求从排队、计算到成功或失败的通用状态
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
    /// 路径请求无法完成的具体原因
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
        TimedOut,
    }

    /// <summary>
    /// A*、分层寻路和 Flow Field 共用的格子寻路请求参数
    /// </summary>
    public struct NavigationPathRequest : IComponentData
    {
        // 起点和终点使用世界坐标，各种寻路服务会统一检查范围并纠正到可站立格子
        public float3 StartPosition;
        public float3 EndPosition;

        // 基础角色半径已经参与烘焙，运行时只检查超出的体型空间
        public float AgentRadius;
        public float ClearanceMargin;

        // 狭窄空间惩罚只影响路线偏好，不会把原本不可站立的格子变成可用
        public float ClearancePenaltyWeight;

        // 路径平滑后的成本最多可比原始 A* 分段增加这个比例
        public float SmoothingCostTolerance;

        // 端点搜索半径以格子为单位；0 表示只接受坐标直接落入的格子
        public int MaximumProjectionRadiusInCells;

        // 调用方每次替换请求都递增版本号，用于忽略迟到的异步旧结果
        public uint Version;

        /// <summary>
        /// 使用导航服务的推荐参数创建一条路径请求
        /// </summary>
        /// <param name="startPosition">请求起点世界坐标</param>
        /// <param name="endPosition">请求终点世界坐标</param>
        /// <param name="agentRadius">Agent 世界半径</param>
        /// <param name="version">调用方维护的请求版本</param>
        /// <param name="clearanceMargin">额外安全边距</param>
        /// <param name="maximumProjectionRadiusInCells">端点投影最大搜索半径</param>
        /// <param name="clearancePenaltyWeight">低 Clearance 路径惩罚权重</param>
        /// <param name="smoothingCostTolerance">平滑路径允许增加的成本比例</param>
        /// <returns>可以直接提交给寻路系统的请求</returns>
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
