using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存路径请求的投影结果、搜索统计和失败状态
    /// </summary>
    public struct NavigationPathState : IComponentData
    {
        // Pending 由调用方提交，Searching 由路径系统独占，Succeeded 和 Failed 为终态
        public NavigationPathStatus Status;
        public NavigationPathFailureReason FailureReason;

        // 记录状态对应的请求版本，避免只比较组件当前值
        public uint RequestVersion;

        // 投影索引用于定位输入问题和 Region 预拒绝结果
        public int ProjectedStartCellIndex;
        public int ProjectedEndCellIndex;

        // 与 Buffer 长度保持一致，便于只读查询和统计
        public int WaypointCount;

        // 搜索统计只用于正确性对照和性能基线
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
        // CellIndex 是确定性结果，Position 是供下游移动使用的烘焙表面坐标
        public int CellIndex;
        public float3 Position;
    }
}
