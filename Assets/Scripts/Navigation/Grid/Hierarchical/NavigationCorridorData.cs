using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 宏观路线经过的一个寻路分块
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationCorridorCluster : IBufferElementData
    {
        // 对应静态导航网格中的分块索引
        public int ClusterId;
    }

    /// <summary>
    /// 宏观路线穿过的一个分块入口
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct NavigationCorridorPortal : IBufferElementData
    {
        // 对应静态导航网格中的入口索引
        public int PortalIndex;
    }

    /// <summary>
    /// 由纠正后端点或分块入口代表格子构成的宏观路点
    /// </summary>
    [InternalBufferCapacity(16)]
    public struct NavigationHierarchicalWaypoint : IBufferElementData
    {
        // 路点对应的格子索引
        public int CellIndex;

        // 路点贴合烘焙地面后的世界坐标
        public float3 Position;
    }
}
