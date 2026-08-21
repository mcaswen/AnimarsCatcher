using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 一条路径请求当前的处理状态、纠正后端点和搜索统计
    /// </summary>
    public struct NavigationPathState : IComponentData
    {
        // 调用方提交 Pending；寻路系统改为 Searching；完成后进入 Succeeded 或 Failed
        public NavigationPathStatus Status;
        public NavigationPathFailureReason FailureReason;

        // 记录当前状态对应的请求版本，防止与后来替换的新请求混淆
        public uint RequestVersion;

        // 纠正后的格子索引用于排查端点无效或静态区域不连通的问题
        public int ProjectedStartCellIndex;
        public int ProjectedEndCellIndex;

        // 路径点数量与结果缓冲区长度保持一致，便于查询和统计
        public int WaypointCount;

        // 搜索统计只用于验证结果和建立性能基线
        public int ExpandedNodeCount;
        public float TotalCost;

        /// <summary>
        /// 为指定版本的路径请求创建等待处理的初始状态
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
    /// 平滑路径中的一个格子及其地面世界坐标
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct NavigationPathWaypoint : IBufferElementData
    {
        // CellIndex 用于复核路径结果，Position 供移动系统直接使用
        public int CellIndex;
        public float3 Position;
    }
}
