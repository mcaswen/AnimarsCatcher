using System;
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
    /// 为一个并发槽位长期保存 Flow Field 构建期间使用的可变列表
    /// </summary>
    public struct NavigationSharedFlowFieldWorkspace : IDisposable
    {
        [NativeDisableParallelForRestriction]
        public NativeList<int> CorridorClusters;

        [NativeDisableParallelForRestriction]
        public NativeList<int> CorridorPortals;

        [NativeDisableParallelForRestriction]
        public NativeList<int> WaypointCells;

        [NativeDisableParallelForRestriction]
        public NativeList<NavigationFlowFieldCell> FlowCells;

        [NativeDisableParallelForRestriction]
        public NativeList<int> VisitedCells;

        [NativeDisableParallelForRestriction]
        public NativeList<int> WorkCorridorClusters;

        [NativeDisableParallelForRestriction]
        public NativeList<int> WorkCorridorPortals;

        [NativeDisableParallelForRestriction]
        public NativeList<int> NodeChain;

        [NativeDisableParallelForRestriction]
        public NativeList<NavigationFlowFieldCacheEntry> CacheEntries;

        [NativeDisableParallelForRestriction]
        public NativeList<int> CacheCorridorClusters;

        [NativeDisableParallelForRestriction]
        public NativeList<NavigationFlowFieldCell> CacheFlowCells;

        public static NavigationSharedFlowFieldWorkspace Create(
            int cellCount,
            int clusterCount,
            int abstractCount)
        {
            return new NavigationSharedFlowFieldWorkspace
            {
                CorridorClusters = new NativeList<int>(
                    math.max(16, clusterCount),
                    Allocator.Persistent),
                CorridorPortals = new NativeList<int>(
                    math.max(16, abstractCount),
                    Allocator.Persistent),
                WaypointCells = new NativeList<int>(
                    math.max(32, abstractCount + 2),
                    Allocator.Persistent),
                FlowCells = new NativeList<NavigationFlowFieldCell>(
                    math.max(256, cellCount),
                    Allocator.Persistent),
                VisitedCells = new NativeList<int>(
                    math.max(256, cellCount),
                    Allocator.Persistent),
                WorkCorridorClusters = new NativeList<int>(
                    math.max(16, clusterCount),
                    Allocator.Persistent),
                WorkCorridorPortals = new NativeList<int>(
                    math.max(16, abstractCount),
                    Allocator.Persistent),
                NodeChain = new NativeList<int>(
                    math.max(32, abstractCount),
                    Allocator.Persistent),
                // 共享 Store 已承担跨请求缓存，槽位内缓存只保留合法空容器
                CacheEntries = new NativeList<NavigationFlowFieldCacheEntry>(
                    1,
                    Allocator.Persistent),
                CacheCorridorClusters = new NativeList<int>(
                    1,
                    Allocator.Persistent),
                CacheFlowCells = new NativeList<NavigationFlowFieldCell>(
                    1,
                    Allocator.Persistent),
            };
        }

        public void Clear()
        {
            CorridorClusters.Clear();
            CorridorPortals.Clear();
            WaypointCells.Clear();
            FlowCells.Clear();
            VisitedCells.Clear();
            WorkCorridorClusters.Clear();
            WorkCorridorPortals.Clear();
            NodeChain.Clear();
            CacheEntries.Clear();
            CacheCorridorClusters.Clear();
            CacheFlowCells.Clear();
        }

        public void Dispose()
        {
            if (CorridorClusters.IsCreated) CorridorClusters.Dispose();
            if (CorridorPortals.IsCreated) CorridorPortals.Dispose();
            if (WaypointCells.IsCreated) WaypointCells.Dispose();
            if (FlowCells.IsCreated) FlowCells.Dispose();
            if (VisitedCells.IsCreated) VisitedCells.Dispose();
            if (WorkCorridorClusters.IsCreated) WorkCorridorClusters.Dispose();
            if (WorkCorridorPortals.IsCreated) WorkCorridorPortals.Dispose();
            if (NodeChain.IsCreated) NodeChain.Dispose();
            if (CacheEntries.IsCreated) CacheEntries.Dispose();
            if (CacheCorridorClusters.IsCreated) CacheCorridorClusters.Dispose();
            if (CacheFlowCells.IsCreated) CacheFlowCells.Dispose();
        }

        internal long CalculateCapacityBytes()
        {
            if (!CorridorClusters.IsCreated)
            {
                return 0;
            }

            return (long)CorridorClusters.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)CorridorPortals.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)WaypointCells.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)FlowCells.Capacity * UnsafeUtility.SizeOf<NavigationFlowFieldCell>() +
                   (long)VisitedCells.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)WorkCorridorClusters.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)WorkCorridorPortals.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)NodeChain.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)CacheEntries.Capacity *
                   UnsafeUtility.SizeOf<NavigationFlowFieldCacheEntry>() +
                   (long)CacheCorridorClusters.Capacity * UnsafeUtility.SizeOf<int>() +
                   (long)CacheFlowCells.Capacity *
                   UnsafeUtility.SizeOf<NavigationFlowFieldCell>();
        }
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

        public NavigationSharedFlowFieldWorkspace Workspace0;
        public NavigationSharedFlowFieldWorkspace Workspace1;
        public NavigationSharedFlowFieldWorkspace Workspace2;
        public NavigationSharedFlowFieldWorkspace Workspace3;
        public NavigationSharedFlowFieldWorkspace Workspace4;
        public NavigationSharedFlowFieldWorkspace Workspace5;
        public NavigationSharedFlowFieldWorkspace Workspace6;
        public NavigationSharedFlowFieldWorkspace Workspace7;

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
            switch (index)
            {
                case 0: Build(index, ref Workspace0); return;
                case 1: Build(index, ref Workspace1); return;
                case 2: Build(index, ref Workspace2); return;
                case 3: Build(index, ref Workspace3); return;
                case 4: Build(index, ref Workspace4); return;
                case 5: Build(index, ref Workspace5); return;
                case 6: Build(index, ref Workspace6); return;
                default: Build(index, ref Workspace7); return;
            }
        }

        private void Build(int index, ref NavigationSharedFlowFieldWorkspace workspace)
        {
            NavigationSharedFlowFieldBuildRequest buildRequest = Requests[index];
            workspace.Clear();

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
                ref workspace.CorridorClusters,
                ref workspace.CorridorPortals,
                ref workspace.WaypointCells,
                ref workspace.FlowCells,
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
                ref workspace.VisitedCells,
                ref workspace.WorkCorridorClusters,
                ref workspace.WorkCorridorPortals,
                ref workspace.NodeChain,
                ref workspace.CacheEntries,
                ref workspace.CacheCorridorClusters,
                ref workspace.CacheFlowCells,
                DynamicOverlay,
                DynamicOverlayClusters,
                DynamicOverlayVersion);
            Results[index] = result;
        }
    }
}
