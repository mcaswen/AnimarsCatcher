using Unity.Collections;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// A* 批次可复用 Scratch 的所有权描述，实际生命周期仍由 Pathfinding System 管理
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
