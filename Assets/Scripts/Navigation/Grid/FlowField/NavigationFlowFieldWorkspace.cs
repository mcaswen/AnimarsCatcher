using Unity.Collections;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// Flow Field 批处理复用的一组临时数组；内存由调度系统创建和释放
    /// </summary>
    public struct NavigationFlowFieldWorkspace
    {
        public NativeArray<float> CellCosts;
        public NativeArray<int> CellHeap;
        public NativeArray<int> CellHeapPositions;
        public NativeArray<int> CellGenerations;
        public NativeArray<int> ClusterGenerations;
        public NativeArray<float> AbstractCosts;
        public NativeArray<float> AbstractEndCosts;
        public NativeArray<int> AbstractParents;
        public NativeArray<int> AbstractHeap;
        public NativeArray<int> AbstractHeapPositions;
        public NativeArray<int> AbstractGenerations;
    }
}
