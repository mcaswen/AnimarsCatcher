using Unity.Collections;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 描述 Flow Field 批次的 Native 工作区边界，所有容器仍由系统创建和释放
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
