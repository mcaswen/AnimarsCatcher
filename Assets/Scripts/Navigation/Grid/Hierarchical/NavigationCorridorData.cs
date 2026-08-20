using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
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
}
