#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using Unity.Collections;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// 保存 NetCode Profiler 单帧数据的结构
    /// 在 NetCode Profiler 中选择帧时创建并填充
    /// 数据由 ProfilerMetricsCollector System 提供
    /// </summary>
    [Serializable]
    struct NetcodeFrameData
    {
        internal uint frameCount;
        internal uint totalSizeSentByServerInBits;
        internal uint totalSizeReceivedByClientInBits;
        internal uint totalPacketCountSentByServer;
        internal uint totalPacketCountReceivedByClient;
        internal NetworkTick serverTickSent;
        internal NativeArray<TickData> tickData;
        internal float jitter;
        internal float rtt;
    }

    /// <summary>
    /// 保存 NetCode Profiler 单个 Tick 数据的结构
    /// 在 NetCode Profiler 中选择帧时创建并填充
    /// 数据由 ProfilerMetricsCollector System 提供
    /// </summary>
    [Serializable]
    struct TickData
    {
        internal NetworkTick tick;
        internal uint packetCount;
        internal uint snapshotSizeInBits;
        internal uint totalInstanceCount;
        internal uint overheadSize;
        internal float timeScale;
        internal float interpolationDelay;
        internal float interpolationScale;
        internal float snapshotAgeMin;
        internal float snapshotAgeMax;
        internal NetworkTick inputTargetTick;
        internal uint commandSizeInBits;
        internal float commandAge;
        internal uint discardedPackets;
        internal NativeArray<ProfilerGhostTypeData> ghostTypeData;
        internal NativeArray<PredictionErrorData> predictionErrors;
    }

    /// <summary>
    /// 保存 NetCode Profiler 单个 Ghost 类型或 Ghost 组件类型数据的结构
    /// 在 NetCode Profiler 中选择帧时创建并填充
    /// 数据由 ProfilerMetricsCollector System 提供
    /// </summary>
    [Serializable]
    struct ProfilerGhostTypeData
    {
        internal FixedString128Bytes name;
        internal uint sizeInBits;
        internal int instanceCount;
        internal uint overheadSize;
        internal float combinedCompressionEfficiency;
        internal float avgSizePerEntity;
        internal NativeArray<ProfilerGhostTypeData> componentsPerType;
        internal bool needsOverheadIcon;
        internal uint newInstancesCount;
    }

    [Serializable]
    struct PredictionErrorData
    {
        internal FixedString128Bytes name;
        internal float errorValue;
    }
}
#endif
