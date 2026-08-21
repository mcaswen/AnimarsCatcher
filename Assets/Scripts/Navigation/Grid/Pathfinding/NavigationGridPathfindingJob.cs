using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 保存单个 Burst 路径任务的 Entity 归属和不可变请求数据
    /// </summary>
    public struct NavigationPathJobRequest
    {
        // Entity 只用于标记结果属于谁，任务内部不会访问 EntityManager
        public Entity Entity;

        // 请求在调度前复制，之后 ECS 中的修改不会影响正在运行的批次
        public NavigationPathRequest Request;
    }

    /// <summary>
    /// 单条 Burst 寻路结果，包含写回 ECS 所需的状态和路径切片
    /// </summary>
    public struct NavigationPathJobResult
    {
        // Entity 与 RequestVersion 共同用于主线程写回前的过期检查
        public Entity Entity;
        public uint RequestVersion;

        // 即使失败也保留已纠正的端点，便于定位不可达发生在哪一步
        public NavigationPathStatus Status;
        public NavigationPathFailureReason FailureReason;
        public int ProjectedStartCellIndex;
        public int ProjectedEndCellIndex;

        // 整个批次的路径连续写入 PathCells，每条结果只记录自己的起点和长度
        public int PathOffset;
        public int PathLength;

        // 搜索统计只用于验证和性能分析，不影响游戏逻辑
        public int ExpandedNodeCount;
        public float TotalCost;
    }

    /// <summary>
    /// 在一个后台任务中依次处理多条路径，并为整批请求复用同一组临时数组
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
    public struct NavigationGridPathfindingJob : IJob
    {
        // 导航网格和请求在任务运行期间只读
        [ReadOnly] public BlobAssetReference<NavigationGridBlob> Grid;
        [ReadOnly] public NativeArray<NavigationPathJobRequest> Requests;
        [ReadOnly] public NativeArray<NavigationDynamicOverlayCell> DynamicOverlay;

        // Results 和 PathCells 由调度该批次的系统创建并释放
        public NativeArray<NavigationPathJobResult> Results;
        public NativeList<int> PathCells;

        // 临时数组按整张网格分配，批次中的请求依次复用它们
        public NativeArray<float> GCosts;
        public NativeArray<int> Parents;
        public NativeArray<int> Heap;
        public NativeArray<int> HeapPositions;

        // Generation 区分数组值属于哪次搜索，因此无需在每条请求前全部清零
        public NativeArray<int> NodeGenerations;
        public int GenerationStart;

        /// <summary>
        /// 执行整批请求，并把每条平滑路径写入共享的连续数组
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Deterministic, FloatPrecision = FloatPrecision.Standard)]
        public void Execute()
        {
            PathCells.Clear();
            if (!Grid.IsCreated)
            {
                // 导航网格无效时仍为每条请求生成完整失败结果，主线程可以正常写回
                for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
                {
                    NavigationPathJobRequest request = Requests[requestIndex];
                    Results[requestIndex] = NavigationGridPathfinder.CreateFailureResult(
                        request.Entity,
                        request.Request.Version,
                        NavigationPathFailureReason.InvalidGrid);
                }

                return;
            }

            ref NavigationGridBlob grid = ref Grid.Value;
            for (int requestIndex = 0; requestIndex < Requests.Length; requestIndex++)
            {
                // 一个任务内顺序处理请求，临时数组无需按请求复制，也不需要加锁
                NavigationPathJobRequest request = Requests[requestIndex];
                Results[requestIndex] = NavigationGridPathfinder.FindPath(
                    ref grid,
                    request,
                    GenerationStart + requestIndex,
                    ref PathCells,
                    GCosts,
                    Parents,
                    Heap,
                    HeapPositions,
                    NodeGenerations,
                    DynamicOverlay);
            }
        }
    }
}
