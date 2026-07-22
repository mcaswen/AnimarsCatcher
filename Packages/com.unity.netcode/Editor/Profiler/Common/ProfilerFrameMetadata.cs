using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// 保存 NetCode Profiler 单帧全部已发送 Metadata 的包装结构
    /// </summary>
    struct ProfilerFrameMetadata
    {
        internal ProfilerMetrics ProfilerMetrics;
        internal NativeArray<UncompressedSizesPerType> UncompressedSizesPerType;
        internal NativeArray<GhostCollectionPrefabSerializer> PrefabSerializers;
        internal NativeArray<GhostComponentSerializer.State> SerializerStates;
        internal NativeArray<GhostCollectionComponentIndex> ComponentIndices;
        internal NativeArray<GhostNames> GhostNames;
        internal NetworkMetrics NetworkMetrics;
        internal NativeArray<PredictionErrorNames> PredictionErrors;
        internal NativeArray<PredictionErrorMetrics> PredictionErrorMetrics;
        internal NativeArray<uint> CommandStats;
    }
}
