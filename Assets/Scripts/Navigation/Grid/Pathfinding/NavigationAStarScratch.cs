using Unity.Collections;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// A* 批量搜索时重复使用的临时数组；内存由寻路系统统一管理
    /// </summary>
    public struct NavigationAStarScratch
    {
        public NativeArray<float> GCosts;
        public NativeArray<int> Parents;
        public NativeArray<int> Heap;
        public NativeArray<int> HeapPositions;
        public NativeArray<int> NodeGenerations;
    }
}
