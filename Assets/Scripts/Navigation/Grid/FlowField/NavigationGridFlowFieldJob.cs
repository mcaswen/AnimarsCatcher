using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将稳定排序后的实体与分层路径请求传入单个 Job 批次
    /// </summary>
    public struct NavigationFlowFieldJobRequest
    {
        // 保存结果写回时使用的实体标识
        public Entity Entity;

        // 保存本批次只读的路径与局部场请求
        public NavigationFlowFieldRequest Request;
    }

    /// <summary>
    /// 描述单个请求在共享输出列表中的切片和可观察统计
    /// </summary>
    public struct NavigationFlowFieldJobResult
    {
        // 保存结果所属的请求实体
        public Entity Entity;

        // 保存调度时捕获的请求版本，用于拒绝旧结果
        public uint RequestVersion;

        // 保存请求完成后的统一路径状态
        public NavigationPathStatus Status;

        // 保存失败时的稳定原因
        public NavigationPathFailureReason FailureReason;

        // 保存起点投影后的 Cell 索引
        public int ProjectedStartCellIndex;

        // 保存终点投影后的 Cell 索引
        public int ProjectedEndCellIndex;

        // 指向本批次 Corridor Cluster 共享列表的切片起点
        public int CorridorClusterOffset;

        // 保存 Corridor Cluster 切片长度
        public int CorridorClusterCount;

        // 指向本批次 Corridor Portal 共享列表的切片起点
        public int CorridorPortalOffset;

        // 保存 Corridor Portal 切片长度
        public int CorridorPortalCount;

        // 指向本批次宏观路点共享列表的切片起点
        public int HierarchicalWaypointOffset;

        // 保存宏观路点切片长度
        public int HierarchicalWaypointCount;

        // 指向本批次局部 Field 共享列表的切片起点
        public int FieldOffset;

        // 保存局部 Field 切片长度
        public int FieldCount;

        // 保存 HPA 星抽象搜索展开节点数量
        public int AbstractExpandedNodeCount;

        // 保存 Integration 搜索展开 Cell 数量
        public int IntegrationExpandedCellCount;

        // 保存宏观路线累计静态成本
        public float TotalCost;

        // 保存命中或新建的缓存版本
        public uint CacheVersion;

        // 保存本次请求使用的动态 Overlay 版本
        public uint DynamicOverlayVersion;

        // 表示结果是否直接复用了缓存 Field
        public byte CacheHit;
    }

    /// <summary>
    /// 在单个确定性批次内构建 HPA 星 Corridor 与局部 Flow Field
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
    public struct NavigationGridFlowFieldJob : IJob
    {
        // 保存本批次共享且只读的静态 Grid Blob
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

        // 保存与 Requests 相同下标的结果描述
        public NativeArray<NavigationFlowFieldJobResult> Results;

        // 累积全部请求的 Corridor Cluster 切片
        public NativeList<int> CorridorClusters;

        // 累积全部请求的 Corridor Portal 切片
        public NativeList<int> CorridorPortals;

        // 累积全部请求的宏观路点 Cell 切片
        public NativeList<int> HierarchicalWaypointCells;

        // 累积全部请求的局部 Field 切片
        public NativeList<NavigationFlowFieldCell> FlowCells;

        // 保存按 Generation 懒初始化的 Cell 搜索成本
        public NativeArray<float> CellCosts;

        // 保存 Cell 搜索二叉堆节点
        public NativeArray<int> CellHeap;

        // 保存 Cell 到二叉堆位置的反向索引
        public NativeArray<int> CellHeapPositions;

        // 保存 Cell Scratch 最近一次有效的 Generation
        public NativeArray<int> CellGenerations;

        // 保存 Corridor Cluster 去重使用的 Generation
        public NativeArray<int> ClusterGenerations;

        // 保存抽象节点从起点侧累计的成本
        public NativeArray<float> AbstractCosts;

        // 保存抽象节点到终点侧的局部连接成本
        public NativeArray<float> AbstractEndCosts;

        // 保存抽象路径重建使用的父节点索引
        public NativeArray<int> AbstractParents;

        // 保存抽象搜索二叉堆节点
        public NativeArray<int> AbstractHeap;

        // 保存抽象节点到二叉堆位置的反向索引
        public NativeArray<int> AbstractHeapPositions;

        // 保存抽象节点 Scratch 最近一次有效的 Generation
        public NativeArray<int> AbstractGenerations;

        // 复用为局部搜索实际访问 Cell 的临时列表
        public NativeList<int> WorkVisitedCells;

        // 复用为单个请求 Corridor Cluster 的临时列表
        public NativeList<int> WorkCorridorClusters;

        // 复用为单个请求 Corridor Portal 的临时列表
        public NativeList<int> WorkCorridorPortals;

        // 复用为 HPA 星父链反转前的抽象节点列表
        public NativeList<int> WorkNodeChain;

        // 保存跨批次复用的缓存元数据
        public NativeList<NavigationFlowFieldCacheEntry> CacheEntries;

        // 保存全部缓存项引用的 Corridor 切片
        public NativeList<int> CacheCorridorClusters;

        // 保存全部缓存项引用的 Field 切片
        public NativeList<NavigationFlowFieldCell> CacheFlowCells;

        // 保存本批次新建缓存使用的起始版本
        public uint CacheVersion;

        // 保存本批次可分配的第一个 Scratch Generation
        public int GenerationStart;

        /// <summary>
        /// 按输入顺序构建结果，保证共享输出列表切片稳定且不并发写入
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
        public void Execute()
        {
            // 单 Job 顺序处理是共享 NativeList 与缓存的所有权前提，改为并行 Job 前必须拆分输出和缓存写入
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
            for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
            {
                Results[requestIndex] = NavigationFlowFieldSolver.Build(
                    ref grid,
                    Requests[requestIndex],
                    GenerationStart + requestIndex * 4,
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
