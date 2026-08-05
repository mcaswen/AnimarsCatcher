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
        // 地面命中点的世界高度
        public float Height;

        // 地面命中点的世界空间法线
        public Vector3 SurfaceNormal;

        // 地面法线相对世界向上的坡度角
        public float SlopeDegrees;

        // 地形对后续路径搜索施加的基础成本
        public float TerrainCost;

        // Cell 中心到最近静态阻挡边界的世界距离
        public float Clearance;

        // 静态连通区域标识，阻挡 Cell 使用零
        public int RegionId;

        // 后续 HPA 星分块使用的稳定标识
        public int ClusterId;

        // 可直接到达的八方向邻接位掩码
        public NavigationNeighborMask NeighborMask;

        // 当前 Cell 是否满足基础地面和静态占用条件
        public bool Walkable;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的紧凑 Cell 数据
    /// </summary>
    public struct NavigationGridCell
    {
        // 保存 Cell 地面命中点的世界高度
        public float Height;

        // 保存 Cell 地面命中点的世界空间法线
        public float3 SurfaceNormal;

        // 保存地面法线相对世界向上的坡度角
        public float SlopeDegrees;

        // 保存进入该 Cell 使用的静态地形成本倍率
        public float TerrainCost;

        // 保存 Cell 中心附近可用的保守世界空间半径
        public float Clearance;

        // 保存静态连通区域编号，零表示不可行走
        public int RegionId;

        // 保存规则空间分块编号
        public int ClusterId;

        // 保存八方向静态可达位掩码
        public byte NeighborMask;

        // 表示 Cell 是否满足基础地面和静态占用约束
        public byte Walkable;
    }

    /// <summary>
    /// 保存相邻 Cluster 边界上的一个连续可通行区间
    /// </summary>
    [Serializable]
    public struct NavigationGridPortalData
    {
        // 保存 Portal 第一侧的 Cluster 索引
        public int ClusterA;

        // 保存 Portal 第二侧的 Cluster 索引
        public int ClusterB;

        // 保存 Portal 两侧共享的静态连通区域编号
        public int RegionId;

        // 保存第一侧连续边界的首个 Cell 索引
        public int FirstCellA;

        // 保存第一侧连续边界的末个 Cell 索引
        public int LastCellA;

        // 保存第二侧连续边界的首个 Cell 索引
        public int FirstCellB;

        // 保存第二侧连续边界的末个 Cell 索引
        public int LastCellB;

        // 保存第一侧用于抽象节点定位的代表 Cell
        public int RepresentativeCellA;

        // 保存第二侧用于抽象节点定位的代表 Cell
        public int RepresentativeCellB;

        // 保存整个连续区间可保证的最小 Clearance
        public float MinimumClearance;

        // 保存从第一侧代表 Cell 跨越到第二侧的静态成本
        public float StaticCostAtoB;

        // 保存从第二侧代表 Cell 跨越到第一侧的静态成本
        public float StaticCostBtoA;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的 Portal 数据
    /// </summary>
    public struct NavigationGridPortal
    {
        // 保存 Portal 第一侧的 Cluster 索引
        public int ClusterA;

        // 保存 Portal 第二侧的 Cluster 索引
        public int ClusterB;

        // 保存 Portal 两侧共享的静态连通区域编号
        public int RegionId;

        // 保存第一侧连续边界的首个 Cell 索引
        public int FirstCellA;

        // 保存第一侧连续边界的末个 Cell 索引
        public int LastCellA;

        // 保存第二侧连续边界的首个 Cell 索引
        public int FirstCellB;

        // 保存第二侧连续边界的末个 Cell 索引
        public int LastCellB;

        // 保存第一侧抽象节点使用的代表 Cell
        public int RepresentativeCellA;

        // 保存第二侧抽象节点使用的代表 Cell
        public int RepresentativeCellB;

        // 保存整个 Portal 区间可保证的最小 Clearance
        public float MinimumClearance;

        // 保存从第一侧跨越到第二侧的静态成本
        public float StaticCostAtoB;

        // 保存从第二侧跨越到第一侧的静态成本
        public float StaticCostBtoA;
    }

    /// <summary>
    /// 描述规则 Cluster 的 Cell 范围及其 Portal 节点切片
    /// </summary>
    [Serializable]
    public struct NavigationGridClusterData
    {
        // 保存 Cluster 在 Grid X 轴包含的最小坐标
        public int MinimumX;

        // 保存 Cluster 在 Grid Z 轴包含的最小坐标
        public int MinimumZ;

        // 保存 Cluster 在 Grid X 轴不包含的最大坐标
        public int MaximumXExclusive;

        // 保存 Cluster 在 Grid Z 轴不包含的最大坐标
        public int MaximumZExclusive;

        // 指向 ClusterPortalNodeIndices 中的切片起点
        public int PortalNodeOffset;

        // 保存当前 Cluster 引用的 Portal Node 数量
        public int PortalNodeCount;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的 Cluster 数据
    /// </summary>
    public struct NavigationGridCluster
    {
        // 保存 Cluster 在 Grid X 轴包含的最小坐标
        public int MinimumX;

        // 保存 Cluster 在 Grid Z 轴包含的最小坐标
        public int MinimumZ;

        // 保存 Cluster 在 Grid X 轴不包含的最大坐标
        public int MaximumXExclusive;

        // 保存 Cluster 在 Grid Z 轴不包含的最大坐标
        public int MaximumZExclusive;

        // 指向 ClusterPortalNodeIndices 中的切片起点
        public int PortalNodeOffset;

        // 保存当前 Cluster 引用的 Portal Node 数量
        public int PortalNodeCount;
    }

    /// <summary>
    /// 表示 Portal 在某一侧 Cluster 内的抽象图节点
    /// </summary>
    [Serializable]
    public struct NavigationGridPortalNodeData
    {
        // 引用该节点所属的 Portal 索引
        public int PortalIndex;

        // 保存该 Portal 侧所在的 Cluster 索引
        public int ClusterId;

        // 保存该 Portal 侧代表 Cell 的索引
        public int CellIndex;

        // 指向 AbstractEdges 中的有向边切片起点
        public int EdgeOffset;

        // 保存该节点拥有的有向边数量
        public int EdgeCount;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的 Portal 节点
    /// </summary>
    public struct NavigationGridPortalNode
    {
        // 引用该节点所属的 Portal 索引
        public int PortalIndex;

        // 保存该 Portal 侧所在的 Cluster 索引
        public int ClusterId;

        // 保存该 Portal 侧代表 Cell 的索引
        public int CellIndex;

        // 指向 AbstractEdges 中的有向边切片起点
        public int EdgeOffset;

        // 保存该节点拥有的有向边数量
        public int EdgeCount;
    }

    /// <summary>
    /// 保存抽象图中的一条有向静态成本边
    /// </summary>
    [Serializable]
    public struct NavigationGridAbstractEdgeData
    {
        // 保存有向边目标 Portal Node 的索引
        public int ToNodeIndex;

        // 保存跨 Portal 或 Cluster 内路线的预计算静态成本
        public float StaticCost;

        // 保存整条边可保证的最小 Clearance
        public float MinimumClearance;

        // 表示该边是否连接同一 Portal 的两个 Cluster 侧
        public bool CrossesPortal;
    }

    /// <summary>
    /// 保存运行时只读 Blob 中的抽象图有向边
    /// </summary>
    public struct NavigationGridAbstractEdge
    {
        // 保存有向边目标 Portal Node 的索引
        public int ToNodeIndex;

        // 保存跨 Portal 或 Cluster 内路线的预计算静态成本
        public float StaticCost;

        // 保存整条边可保证的最小 Clearance
        public float MinimumClearance;

        // 表示该边是否连接同一 Portal 的两个 Cluster 侧
        public byte CrossesPortal;
    }

    /// <summary>
    /// 保存所有 World 共享的静态 Grid 数据
    /// </summary>
    public struct NavigationGridBlob
    {
        // 保存 Grid 世界包围盒最小点
        public float3 BoundsMinimum;

        // 保存 Grid 世界包围盒最大点
        public float3 BoundsMaximum;

        // 保存 Cell 在 XZ 平面的世界边长
        public float CellSize;

        // 保存静态占用烘焙已经包含的基础 Agent 半径
        public float BaseAgentRadius;

        // 保存静态占用烘焙已经包含的基础 Agent 高度
        public float BaseAgentHeight;

        // 保存 Grid 在 X 轴的 Cell 数量
        public int Width;

        // 保存 Grid 在 Z 轴的 Cell 数量
        public int Height;

        // 保存规则 Cluster 的 Cell 边长
        public int ClusterSizeInCells;

        // 保存 Grid 在 X 轴的 Cluster 数量
        public int ClusterWidth;

        // 保存 Grid 在 Z 轴的 Cluster 数量
        public int ClusterHeight;

        // 保存非零静态连通区域数量
        public int RegionCount;

        // 保存 Blob 字段协议对应的数据格式版本
        public int DataVersion;

        // 保存参与采样的静态几何 Hash
        public Unity.Entities.Hash128 GeometryHash;

        // 保存影响采样与拓扑的参数 Hash
        public Unity.Entities.Hash128 ParameterHash;

        // 保存 Cell 与分层数据共同计算的完整数据 Hash
        public Unity.Entities.Hash128 DataHash;

        // 保存按行主序排列的全部 Cell
        public BlobArray<NavigationGridCell> Cells;

        // 保存按 ClusterId 排列的规则分块
        public BlobArray<NavigationGridCluster> Clusters;

        // 保存按边界扫描顺序排列的连续 Portal
        public BlobArray<NavigationGridPortal> Portals;

        // 保存每个 Portal 两侧对应的抽象节点
        public BlobArray<NavigationGridPortalNode> PortalNodes;

        // 保存由 Portal Node 切片引用的有向抽象边
        public BlobArray<NavigationGridAbstractEdge> AbstractEdges;

        // 保存由 Cluster 切片引用的 Portal Node 索引
        public BlobArray<int> ClusterPortalNodeIndices;
    }

    /// <summary>
    /// 将共享的 Navigation Grid Blob 附加到运行时实体
    /// </summary>
    public struct NavigationGridReference : IComponentData
    {
        // 保存当前 World 唯一的只读静态 Grid Blob
        public BlobAssetReference<NavigationGridBlob> Value;
    }
}
