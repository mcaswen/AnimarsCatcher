using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将稳定排序后的 Entity 与分层路径请求传入单个 Job 批次
    /// </summary>
    public struct NavigationFlowFieldJobRequest
    {
        // 保存结果写回时使用的 Entity 标识
        public Entity Entity;

        // 调度时复制的只读 Flow Field 请求
        public NavigationFlowFieldRequest Request;
    }

    /// <summary>
    /// 一条 Flow Field 请求的结果，以及它在各共享输出列表中的位置
    /// </summary>
    public struct NavigationFlowFieldJobResult
    {
        // 保存结果所属的请求 Entity
        public Entity Entity;

        // 调度时的请求版本，用于阻止旧结果覆盖新请求
        public uint RequestVersion;

        // 请求完成后的路径状态
        public NavigationPathStatus Status;

        // 请求失败的原因
        public NavigationPathFailureReason FailureReason;

        // 起点纠正后的格子索引
        public int ProjectedStartCellIndex;

        // 终点纠正后的格子索引
        public int ProjectedEndCellIndex;

        // 通道分块在本批共享列表中的起点
        public int CorridorClusterOffset;

        // 通道分块数量
        public int CorridorClusterCount;

        // 分块入口在本批共享列表中的起点
        public int CorridorPortalOffset;

        // 分块入口数量
        public int CorridorPortalCount;

        // 宏观路点在本批共享列表中的起点
        public int HierarchicalWaypointOffset;

        // 宏观路点数量
        public int HierarchicalWaypointCount;

        // 局部 Flow Field 在本批共享列表中的起点
        public int FieldOffset;

        // 局部 Flow Field 格子数量
        public int FieldCount;

        // 分层寻路展开的抽象节点数
        public int AbstractExpandedNodeCount;

        // Integration Field 展开的格子数
        public int IntegrationExpandedCellCount;

        // 宏观路线的累计静态成本
        public float TotalCost;

        // 命中或新建的缓存版本
        public uint CacheVersion;

        // 本次计算使用的动态障碍版本
        public uint DynamicOverlayVersion;

        // 只覆盖实际 Corridor 的动态障碍版本签名
        public uint DynamicOverlaySignature;

        // 是否直接复用了缓存中的 Flow Field
        public byte CacheHit;
    }

    /// <summary>
    /// 在一个后台批次中依次构建分层通道和局部 Flow Field
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
    public struct NavigationGridFlowFieldJob : IJob
    {
        // 本批次共用的只读静态导航网格
        [ReadOnly]
        public BlobAssetReference<NavigationGridBlob> Grid;

        // 保存由 System 按 Entity 稳定排序的请求数组
        [ReadOnly]
        public NativeArray<NavigationFlowFieldJobRequest> Requests;

        [ReadOnly]
        public NativeArray<NavigationDynamicOverlayCell> DynamicOverlay;

        [ReadOnly]
        public NativeArray<NavigationDynamicOverlayCluster> DynamicOverlayClusters;
        public uint DynamicOverlayVersion;

        // 与 Requests 一一对应的结果数组
        public NativeArray<NavigationFlowFieldJobResult> Results;

        // 整个批次输出的通道分块
        public NativeList<int> CorridorClusters;

        // 整个批次输出的分块入口
        public NativeList<int> CorridorPortals;

        // 整个批次输出的宏观路点格子
        public NativeList<int> HierarchicalWaypointCells;

        // 整个批次输出的局部 Flow Field
        public NativeList<NavigationFlowFieldCell> FlowCells;

        // 按 Generation 延迟初始化的格子搜索成本
        public NativeArray<float> CellCosts;

        // 格子搜索使用的二叉堆
        public NativeArray<int> CellHeap;

        // 从格子索引反查其在二叉堆中的位置
        public NativeArray<int> CellHeapPositions;

        // 每个格子临时数据所属的最近一次 Generation
        public NativeArray<int> CellGenerations;

        // 通道分块去重使用的 Generation
        public NativeArray<int> ClusterGenerations;

        // 从起点到各抽象节点的累计成本
        public NativeArray<float> AbstractCosts;

        // 各抽象节点到终点的局部连接成本
        public NativeArray<float> AbstractEndCosts;

        // 重建抽象路线时使用的父节点
        public NativeArray<int> AbstractParents;

        // 抽象节点搜索使用的二叉堆
        public NativeArray<int> AbstractHeap;

        // 从抽象节点反查其在二叉堆中的位置
        public NativeArray<int> AbstractHeapPositions;

        // 每个抽象节点临时数据所属的最近一次 Generation
        public NativeArray<int> AbstractGenerations;

        // 记录一次局部搜索实际访问过的格子，可在请求之间复用
        public NativeList<int> WorkVisitedCells;

        // 构建单条请求通道时复用的分块列表
        public NativeList<int> WorkCorridorClusters;

        // 构建单条请求通道时复用的入口列表
        public NativeList<int> WorkCorridorPortals;

        // 反转分层寻路父节点链时复用的临时列表
        public NativeList<int> WorkNodeChain;

        // 可跨批次复用的缓存索引
        public NativeList<NavigationFlowFieldCacheEntry> CacheEntries;

        // 所有缓存项引用的通道分块
        public NativeList<int> CacheCorridorClusters;

        // 所有缓存项引用的 Flow Field 数据
        public NativeList<NavigationFlowFieldCell> CacheFlowCells;

        // 本批次新缓存项使用的起始版本号
        public uint CacheVersion;

        // 本批次分配临时 Generation 的起点
        public int GenerationStart;

        internal static int CalculateGenerationStride(int portalNodeCount, uint overlayVersion)
        {
            // 动态障碍从未变化时只需要 4 个 Generation
            // 发生过动态变化后，每个通道节点都要有独立的局部搜索 Generation，
            // 最后再留一个给 Integration Field。起点、终点和抽象搜索分别使用前几个编号
            // 即使地图没有通道节点也至少返回 4，保证临时数组编号规则不变
            return overlayVersion > 1u ? math.max(4, portalNodeCount + 3) : 4;
        }

        /// <summary>
        /// 按输入顺序处理整批请求，让各结果在共享列表中的位置明确且不会并发冲突
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
        public void Execute()
        {
            // 当前任务依次处理请求，因此可以安全共享 NativeList 和缓存；若改为并行，必须先拆分写入目标
            CorridorClusters.Clear();
            CorridorPortals.Clear();
            HierarchicalWaypointCells.Clear();
            FlowCells.Clear();
            if (!Grid.IsCreated)
            {
                for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
                {
                    Results[requestIndex] = NavigationFlowFieldSolver.CreateFailureResult(
                        Requests[requestIndex].Entity,
                        Requests[requestIndex].Request.PathRequest.Version,
                        NavigationPathFailureReason.InvalidGrid,
                        CacheVersion);
                }

                return;
            }

            ref NavigationGridBlob grid = ref Grid.Value;
            int generationStride = CalculateGenerationStride(
                grid.PortalNodes.Length,
                DynamicOverlayVersion);
            for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
            {
                Results[requestIndex] = NavigationFlowFieldSolver.Build(
                    ref grid,
                    Requests[requestIndex],
                    GenerationStart + requestIndex * generationStride,
                    CacheVersion,
                    ref CorridorClusters,
                    ref CorridorPortals,
                    ref HierarchicalWaypointCells,
                    ref FlowCells,
                    CellCosts,
                    CellHeap,
                    CellHeapPositions,
                    CellGenerations,
                    ClusterGenerations,
                    AbstractCosts,
                    AbstractEndCosts,
                    AbstractParents,
                    AbstractHeap,
                    AbstractHeapPositions,
                    AbstractGenerations,
                    ref WorkVisitedCells,
                    ref WorkCorridorClusters,
                    ref WorkCorridorPortals,
                    ref WorkNodeChain,
                    ref CacheEntries,
                    ref CacheCorridorClusters,
                    ref CacheFlowCells,
                    DynamicOverlay,
                    DynamicOverlayClusters,
                    DynamicOverlayVersion);
            }
        }
    }
}
