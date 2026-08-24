using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存一个唯一 Field Key 的构建输入和确定性发布信息
    /// </summary>
    public struct NavigationSharedFlowFieldBuildRequest
    {
        // Key 用于归并消费者，JobRequest 保留 Solver 需要的完整请求
        public NavigationFlowFieldKey Key;
        public NavigationFlowFieldJobRequest JobRequest;

        // 记录版本和 Generation 起点在主线程分配，不能由 Worker 竞争生成
        public uint RecordVersion;
        public int EnqueuedTick;
        public int GenerationStart;
    }

    /// <summary>
    /// 为每个唯一 Field Key 分配独立工作区并行构建结果
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
    public struct NavigationSharedFlowFieldBuildJob : IJobParallelFor
    {
        [ReadOnly]
        public BlobAssetReference<NavigationGridBlob> Grid;

        [ReadOnly]
        public NativeArray<NavigationSharedFlowFieldBuildRequest> Requests;

        [ReadOnly]
        public NativeArray<NavigationDynamicOverlayCell> DynamicOverlay;

        [ReadOnly]
        public NativeArray<NavigationDynamicOverlayCluster> DynamicOverlayClusters;

        // 结果头与四个流使用同一索引，发布阶段不依赖 Worker 完成先后
        public uint DynamicOverlayVersion;
        public NativeArray<NavigationFlowFieldJobResult> Results;

        public NativeStream.Writer CorridorClusters;
        public NativeStream.Writer CorridorPortals;
        public NativeStream.Writer HierarchicalWaypointCells;
        public NativeStream.Writer FlowCells;

        // Cell 工作区按请求槽位切片，禁用容器的默认索引限制不会产生交叉写入
        [NativeDisableParallelForRestriction]
        public NativeArray<float> CellCosts;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> CellHeap;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> CellHeapPositions;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> CellGenerations;

        // Cluster 和抽象图同样按槽位隔离，Solver 内部无需锁或原子写入
        [NativeDisableParallelForRestriction]
        public NativeArray<int> ClusterGenerations;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> AbstractCosts;

        [NativeDisableParallelForRestriction]
        public NativeArray<float> AbstractEndCosts;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> AbstractParents;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> AbstractHeap;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> AbstractHeapPositions;

        [NativeDisableParallelForRestriction]
        public NativeArray<int> AbstractGenerations;

        // Stride 来自当前 Grid 规模，用于定位每个槽位的连续内存范围
        public int CellStride;
        public int ClusterStride;
        public int AbstractStride;

        public void Execute(int index)
        {
            NavigationSharedFlowFieldBuildRequest buildRequest = Requests[index];
            // 输出和工作列表只在当前并行索引内存在，Solver 不会共享可变 NativeList
            var corridorClusters = new NativeList<int>(16, Allocator.Temp);
            var corridorPortals = new NativeList<int>(16, Allocator.Temp);
            var waypointCells = new NativeList<int>(32, Allocator.Temp);
            var flowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.Temp);
            var workVisitedCells = new NativeList<int>(256, Allocator.Temp);
            var workCorridorClusters = new NativeList<int>(16, Allocator.Temp);
            var workCorridorPortals = new NativeList<int>(16, Allocator.Temp);
            var workNodeChain = new NativeList<int>(32, Allocator.Temp);
            // 全局 Store 已完成跨请求归并，槽位内缓存保持为空以免形成第二套所有权
            var cacheEntries = new NativeList<NavigationFlowFieldCacheEntry>(1, Allocator.Temp);
            var cacheCorridorClusters = new NativeList<int>(16, Allocator.Temp);
            var cacheFlowCells = new NativeList<NavigationFlowFieldCell>(256, Allocator.Temp);

            // 所有切片都以相同请求索引定位，这是并行写入互不重叠的核心约束
            NativeArray<float> cellCosts = CellCosts.GetSubArray(index * CellStride, CellStride);
            NativeArray<int> cellHeap = CellHeap.GetSubArray(index * CellStride, CellStride);
            NativeArray<int> cellHeapPositions = CellHeapPositions.GetSubArray(
                index * CellStride,
                CellStride);
            NativeArray<int> cellGenerations = CellGenerations.GetSubArray(
                index * CellStride,
                CellStride);
            NativeArray<int> clusterGenerations = ClusterGenerations.GetSubArray(
                index * ClusterStride,
                ClusterStride);
            NativeArray<float> abstractCosts = AbstractCosts.GetSubArray(
                index * AbstractStride,
                AbstractStride);
            NativeArray<float> abstractEndCosts = AbstractEndCosts.GetSubArray(
                index * AbstractStride,
                AbstractStride);
            NativeArray<int> abstractParents = AbstractParents.GetSubArray(
                index * AbstractStride,
                AbstractStride);
            NativeArray<int> abstractHeap = AbstractHeap.GetSubArray(
                index * AbstractStride,
                AbstractStride);
            NativeArray<int> abstractHeapPositions = AbstractHeapPositions.GetSubArray(
                index * AbstractStride,
                AbstractStride);
            NativeArray<int> abstractGenerations = AbstractGenerations.GetSubArray(
                index * AbstractStride,
                AbstractStride);

            // Solver 仍复用 Stage 3 的权威算法，只替换外层批处理和结果所有权
            ref NavigationGridBlob grid = ref Grid.Value;
            NavigationFlowFieldJobResult result = NavigationFlowFieldSolver.Build(
                ref grid,
                buildRequest.JobRequest,
                buildRequest.GenerationStart,
                buildRequest.RecordVersion,
                ref corridorClusters,
                ref corridorPortals,
                ref waypointCells,
                ref flowCells,
                cellCosts,
                cellHeap,
                cellHeapPositions,
                cellGenerations,
                clusterGenerations,
                abstractCosts,
                abstractEndCosts,
                abstractParents,
                abstractHeap,
                abstractHeapPositions,
                abstractGenerations,
                ref workVisitedCells,
                ref workCorridorClusters,
                ref workCorridorPortals,
                ref workNodeChain,
                ref cacheEntries,
                ref cacheCorridorClusters,
                ref cacheFlowCells,
                DynamicOverlay,
                DynamicOverlayClusters,
                DynamicOverlayVersion);
            Results[index] = result;

            // NativeStream 为每个索引提供独立段，发布阶段再按请求排序逐段读取
            WriteStream(CorridorClusters, index, corridorClusters);
            WriteStream(CorridorPortals, index, corridorPortals);
            WriteStream(HierarchicalWaypointCells, index, waypointCells);
            WriteStream(FlowCells, index, flowCells);

            // Allocator.Temp 容器由当前槽位创建并释放，不跨帧进入共享 Store
            cacheFlowCells.Dispose();
            cacheCorridorClusters.Dispose();
            cacheEntries.Dispose();
            workNodeChain.Dispose();
            workCorridorPortals.Dispose();
            workCorridorClusters.Dispose();
            workVisitedCells.Dispose();
            flowCells.Dispose();
            waypointCells.Dispose();
            corridorPortals.Dispose();
            corridorClusters.Dispose();
        }

        private static void WriteStream<T>(
            NativeStream.Writer writer,
            int index,
            NativeList<T> values)
            where T : unmanaged
        {
            // 即使结果为空也写出一个完整段，四个流才能与 Results 保持索引对齐
            writer.BeginForEachIndex(index);
            for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                writer.Write(values[valueIndex]);
            }
            writer.EndForEachIndex();
        }
    }
}
