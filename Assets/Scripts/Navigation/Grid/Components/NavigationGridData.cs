using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 定义 Cell 可连接的八个固定方向
    /// </summary>
    [Flags]
    public enum NavigationNeighborMask : byte
    {
        None = 0,
        North = 1 << 0,
        NorthEast = 1 << 1,
        East = 1 << 2,
        SouthEast = 1 << 3,
        South = 1 << 4,
        SouthWest = 1 << 5,
        West = 1 << 6,
        NorthWest = 1 << 7,
    }

    /// <summary>
    /// 保存可序列化并可在 Inspector 中检查的单个 Cell 数据
    /// </summary>
    [Serializable]
    public struct NavigationGridCellData
    {
        /// <summary>
        /// 地面命中点的世界高度
        /// </summary>
        public float Height;

        /// <summary>
        /// 地面命中点的世界空间法线
        /// </summary>
        public Vector3 SurfaceNormal;

        /// <summary>
        /// 地面法线相对世界向上的坡度角
        /// </summary>
        public float SlopeDegrees;

        /// <summary>
        /// 地形对后续路径搜索施加的基础成本
        /// </summary>
        public float TerrainCost;

        /// <summary>
        /// Cell 中心到最近静态阻挡边界的世界距离
        /// </summary>
        public float Clearance;

        /// <summary>
        /// 静态连通区域标识 阻挡 Cell 使用零
        /// </summary>
        public int RegionId;

        /// <summary>
        /// 后续 HPA 星分块使用的稳定标识
        /// </summary>
        public int ClusterId;

        /// <summary>
        /// 可直接到达的八方向邻接位掩码
        /// </summary>
        public NavigationNeighborMask NeighborMask;

        /// <summary>
        /// 当前 Cell 是否满足基础地面和静态占用条件
        /// </summary>
        public bool Walkable;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的紧凑 Cell 数据
    /// </summary>
    public struct NavigationGridCell
    {
        public float Height;
        public float3 SurfaceNormal;
        public float SlopeDegrees;
        public float TerrainCost;
        public float Clearance;
        public int RegionId;
        public int ClusterId;
        public byte NeighborMask;
        public byte Walkable;
    }

    /// <summary>
    /// 保存所有 World 共享的静态 Grid 数据
    /// </summary>
    public struct NavigationGridBlob
    {
        public float3 BoundsMinimum;
        public float3 BoundsMaximum;
        public float CellSize;
        public float BaseAgentRadius;
        public float BaseAgentHeight;
        public int Width;
        public int Height;
        public int ClusterSizeInCells;
        public int RegionCount;
        public int DataVersion;
        public Unity.Entities.Hash128 GeometryHash;
        public Unity.Entities.Hash128 ParameterHash;
        public Unity.Entities.Hash128 DataHash;
        public BlobArray<NavigationGridCell> Cells;
    }

    /// <summary>
    /// 将共享的 Navigation Grid Blob 附加到运行时实体
    /// </summary>
    public struct NavigationGridReference : IComponentData
    {
        public BlobAssetReference<NavigationGridBlob> Value;
    }
}
