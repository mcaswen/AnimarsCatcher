using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 用位标记记录一个格子可以通往哪些相邻方向
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
    /// 单个格子的烘焙数据，可序列化到资产并在 Inspector 中查看
    /// </summary>
    [Serializable]
    public struct NavigationGridCellData
    {
        // 格子中心采样到的地面高度
        public float Height;

        // 采样点的地面法线
        public Vector3 SurfaceNormal;

        // 地面的坡度角
        public float SlopeDegrees;

        // 经过该地形时使用的基础寻路成本
        public float TerrainCost;

        // 格子中心到最近静态障碍边界的距离，用于判断角色能否安全通过
        public float Clearance;

        // 所属的静态连通区域；不可行走时为 0
        public int RegionId;

        // 所属的寻路分块
        public int ClusterId;

        // 可以直接到达的相邻方向
        public NavigationNeighborMask NeighborMask;

        // 角色是否可以站在这个格子上
        public bool Walkable;
    }

    /// <summary>
    /// 单个格子的运行时数据，采用紧凑格式存放在只读 Blob 中
    /// </summary>
    public struct NavigationGridCell
    {
        // 格子中心的地面高度
        public float Height;

        // 格子中心的地面法线
        public float3 SurfaceNormal;

        // 地面的坡度角
        public float SlopeDegrees;

        // 进入该格子时使用的地形成本
        public float TerrainCost;

        // 格子中心周围可安全容纳角色的半径
        public float Clearance;

        // 所属的静态连通区域；0 表示不可行走
        public int RegionId;

        // 所属的寻路分块
        public int ClusterId;

        // 可以直接到达的相邻方向
        public byte NeighborMask;

        // 是否可以站立；使用 byte 以便存入 Blob
        public byte Walkable;
    }

    /// <summary>
    /// 两个相邻分块之间的一段连续通道，用作分层寻路的出入口
    /// </summary>
    [Serializable]
    public struct NavigationGridPortalData
    {
        // 通道一侧的分块
        public int ClusterA;

        // 通道另一侧的分块
        public int ClusterB;

        // 通道所属的静态连通区域
        public int RegionId;

        // A 侧通道起点格子的索引
        public int FirstCellA;

        // A 侧通道终点格子的索引
        public int LastCellA;

        // B 侧通道起点格子的索引
        public int FirstCellB;

        // B 侧通道终点格子的索引
        public int LastCellB;

        // A 侧用于分层寻路的代表格子
        public int RepresentativeCellA;

        // B 侧用于分层寻路的代表格子
        public int RepresentativeCellB;

        // 整段通道最窄处的可用空间
        public float MinimumClearance;

        // 从 A 侧穿到 B 侧的预计算成本
        public float StaticCostAtoB;

        // 从 B 侧穿到 A 侧的预计算成本
        public float StaticCostBtoA;
    }

    /// <summary>
    /// 运行时使用的分块通道数据
    /// </summary>
    public struct NavigationGridPortal
    {
        // 通道一侧的分块
        public int ClusterA;

        // 通道另一侧的分块
        public int ClusterB;

        // 通道所属的静态连通区域
        public int RegionId;

        // A 侧通道起点格子的索引
        public int FirstCellA;

        // A 侧通道终点格子的索引
        public int LastCellA;

        // B 侧通道起点格子的索引
        public int FirstCellB;

        // B 侧通道终点格子的索引
        public int LastCellB;

        // A 侧用于分层寻路的代表格子
        public int RepresentativeCellA;

        // B 侧用于分层寻路的代表格子
        public int RepresentativeCellB;

        // 整段通道最窄处的可用空间
        public float MinimumClearance;

        // 从 A 侧穿到 B 侧的预计算成本
        public float StaticCostAtoB;

        // 从 B 侧穿到 A 侧的预计算成本
        public float StaticCostBtoA;
    }

    /// <summary>
    /// 一个寻路分块的格子范围，以及它在全局通道节点表中的位置
    /// </summary>
    [Serializable]
    public struct NavigationGridClusterData
    {
        // 分块包含的最小 X 坐标
        public int MinimumX;

        // 分块包含的最小 Z 坐标
        public int MinimumZ;

        // 分块不包含的最大 X 坐标
        public int MaximumXExclusive;

        // 分块不包含的最大 Z 坐标
        public int MaximumZExclusive;

        // 该分块在 ClusterPortalNodeIndices 中的起始位置
        public int PortalNodeOffset;

        // 该分块连接的通道节点数量
        public int PortalNodeCount;
    }

    /// <summary>
    /// 运行时使用的寻路分块数据
    /// </summary>
    public struct NavigationGridCluster
    {
        // 分块包含的最小 X 坐标
        public int MinimumX;

        // 分块包含的最小 Z 坐标
        public int MinimumZ;

        // 分块不包含的最大 X 坐标
        public int MaximumXExclusive;

        // 分块不包含的最大 Z 坐标
        public int MaximumZExclusive;

        // 该分块在 ClusterPortalNodeIndices 中的起始位置
        public int PortalNodeOffset;

        // 该分块连接的通道节点数量
        public int PortalNodeCount;
    }

    /// <summary>
    /// 表示一个分块从某条通道进出时使用的分层寻路节点
    /// </summary>
    [Serializable]
    public struct NavigationGridPortalNodeData
    {
        // 对应的通道索引
        public int PortalIndex;

        // 该节点所在的分块
        public int ClusterId;

        // 该节点在格子地图中的代表位置
        public int CellIndex;

        // 该节点在 AbstractEdges 中的出边起始位置
        public int EdgeOffset;

        // 该节点的出边数量
        public int EdgeCount;
    }

    /// <summary>
    /// 运行时使用的分块通道节点
    /// </summary>
    public struct NavigationGridPortalNode
    {
        // 对应的通道索引
        public int PortalIndex;

        // 该节点所在的分块
        public int ClusterId;

        // 该节点在格子地图中的代表位置
        public int CellIndex;

        // 该节点在 AbstractEdges 中的出边起始位置
        public int EdgeOffset;

        // 该节点的出边数量
        public int EdgeCount;
    }

    /// <summary>
    /// 分层寻路图中的一条有向连接
    /// </summary>
    [Serializable]
    public struct NavigationGridAbstractEdgeData
    {
        // 连接到的通道节点
        public int ToNodeIndex;

        // 穿过通道或在分块内部移动的预计算成本
        public float StaticCost;

        // 这段路线最窄处的可用空间
        public float MinimumClearance;

        // 是否直接穿过通道进入相邻分块
        public bool CrossesPortal;
    }

    /// <summary>
    /// 运行时使用的分层寻路图连接
    /// </summary>
    public struct NavigationGridAbstractEdge
    {
        // 连接到的通道节点
        public int ToNodeIndex;

        // 穿过通道或在分块内部移动的预计算成本
        public float StaticCost;

        // 这段路线最窄处的可用空间
        public float MinimumClearance;

        // 是否直接穿过通道；使用 byte 以便存入 Blob
        public byte CrossesPortal;
    }

    /// <summary>
    /// 整张静态导航网格的运行时数据，供各个 World 只读共享
    /// </summary>
    public struct NavigationGridBlob
    {
        // 导航网格的世界坐标最小点
        public float3 BoundsMinimum;

        // 导航网格的世界坐标最大点
        public float3 BoundsMaximum;

        // 每个格子在 XZ 平面的边长
        public float CellSize;

        // 烘焙静态障碍时采用的基础角色半径
        public float BaseAgentRadius;

        // 烘焙静态障碍时采用的基础角色高度
        public float BaseAgentHeight;

        // X 方向的格子数
        public int Width;

        // Z 方向的格子数
        public int Height;

        // 每个寻路分块包含的格子边长
        public int ClusterSizeInCells;

        // X 方向的分块数
        public int ClusterWidth;

        // Z 方向的分块数
        public int ClusterHeight;

        // 静态连通区域的数量
        public int RegionCount;

        // 数据格式版本，用于判断烘焙资产是否兼容
        public int DataVersion;

        // 参与采样的静态场景几何哈希
        public Unity.Entities.Hash128 GeometryHash;

        // 影响采样和连通关系的配置哈希
        public Unity.Entities.Hash128 ParameterHash;

        // 根据格子和分层数据计算出的完整内容哈希
        public Unity.Entities.Hash128 DataHash;

        // 按行排列的全部格子
        public BlobArray<NavigationGridCell> Cells;

        // 按编号排列的全部寻路分块
        public BlobArray<NavigationGridCluster> Clusters;

        // 按边界扫描顺序排列的全部分块通道
        public BlobArray<NavigationGridPortal> Portals;

        // 每条通道两侧对应的分层寻路节点
        public BlobArray<NavigationGridPortalNode> PortalNodes;

        // 各通道节点引用的有向连接
        public BlobArray<NavigationGridAbstractEdge> AbstractEdges;

        // 每个分块连接到的通道节点索引
        public BlobArray<int> ClusterPortalNodeIndices;
    }

    /// <summary>
    /// 将共享的 Navigation Grid Blob 附加到运行时 Entity
    /// </summary>
    public struct NavigationGridReference : IComponentData
    {
        // 当前 World 使用的只读导航网格
        public BlobAssetReference<NavigationGridBlob> Value;
    }
}
