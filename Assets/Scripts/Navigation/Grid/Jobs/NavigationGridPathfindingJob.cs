using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace AnimarsCatcher.Animars.Navigation.Grid
{
    /// <summary>
    /// 保存单个 Burst 路径任务的实体归属和不可变请求数据
    /// </summary>
    public struct NavigationPathJobRequest
    {
        // Entity 只作为结果归属标识 Job 内不访问 EntityManager
        public Entity Entity;

        // 请求在调度前复制 后续 ECS 修改不会改变活动批次输入
        public NavigationPathRequest Request;
    }

    /// <summary>
    /// 保存单个 Burst 路径任务写回 ECS 所需的紧凑结果
    /// </summary>
    public struct NavigationPathJobResult
    {
        // Entity 与 RequestVersion 共同用于主线程写回前的过期检查
        public Entity Entity;
        public uint RequestVersion;

        // 失败结果也保留已成功投影的端点 便于定位不可达原因
        public NavigationPathStatus Status;
        public NavigationPathFailureReason FailureReason;
        public int ProjectedStartCellIndex;
        public int ProjectedEndCellIndex;

        // 全批次路径连续写入 PathCells 每个结果只保存自己的切片
        public int PathOffset;
        public int PathLength;

        // 搜索统计不参与下游控制 只用于验收和性能分析
        public int ExpandedNodeCount;
        public float TotalCost;
    }

    /// <summary>
    /// 在单个后台任务中顺序处理路径批次并复用整张 Grid 的 Scratch 内存
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
    public struct NavigationGridPathfindingJob : IJob
    {
        // Grid 与 Requests 在整个 Job 生命周期只读
        [ReadOnly] public BlobAssetReference<NavigationGridBlob> Grid;
        [ReadOnly] public NativeArray<NavigationPathJobRequest> Requests;

        // Results 与 PathCells 是本批次输出 由拥有批次的 System 释放
        public NativeArray<NavigationPathJobResult> Results;
        public NativeList<int> PathCells;

        // 以下数组覆盖整张 Grid 但批次内所有请求顺序复用同一份内存
        public NativeArray<float> GCosts;
        public NativeArray<int> Parents;
        public NativeArray<int> Heap;
        public NativeArray<int> HeapPositions;

        // NodeGenerations 区分数组槽位是否属于当前请求 避免每次搜索全量清零
        public NativeArray<int> NodeGenerations;
        public int GenerationStart;

        /// <summary>
        /// 执行完整批次并把每条平滑路径写入共享连续数组
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
        public void Execute()
        {
            PathCells.Clear();
            if (!Grid.IsCreated)
            {
                // Grid 失效时仍为每个请求生成可写回的稳定失败结果
                for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
                {
                    NavigationPathJobRequest request = Requests[requestIndex];
                    Results[requestIndex] = NavigationGridPathAlgorithms.CreateFailureResult(
                        request.Entity,
                        request.Request.Version,
                        NavigationPathFailureReason.InvalidGrid);
                }

                return;
            }

            ref NavigationGridBlob grid = ref Grid.Value;
            for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
            {
                // 单 Job 顺序处理使 Scratch 数组无需按请求复制或加锁
                NavigationPathJobRequest request = Requests[requestIndex];
                Results[requestIndex] = NavigationGridPathAlgorithms.FindPath(
                    ref grid,
                    request,
                    GenerationStart + requestIndex,
                    ref PathCells,
                    GCosts,
                    Parents,
                    Heap,
                    HeapPositions,
                    NodeGenerations);
            }
        }
    }
}
