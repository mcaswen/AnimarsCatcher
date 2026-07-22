#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 用于迁移到新组件类型的临时类型，应在最终 1.0 版本前移除
    /// </summary>
    [Obsolete("GhostMetricsMonitorComponent has been deprecated. Use GhostMetricsMonitor instead (UnityUpgradable) -> GhostMetricsMonitor", true)]
    public struct GhostMetricsMonitorComponent : IComponentData
    {}

    /// <summary>
    /// 同时存在于客户端和服务端 World 中，用于启用 Ghost 指标监控的单例组件
    /// </summary>
    public struct GhostMetricsMonitor : IComponentData
    {
        /// <summary>
        /// 收到指标更新时的 Server Tick
        /// </summary>
        public NetworkTick CapturedTick;
    }

    /// <summary>
    /// 保存网络与时间相关指标的单例组件
    /// </summary>
    public struct NetworkMetrics : IComponentData
    {
        /// <summary>
        /// 仅对以可变步长运行的客户端有意义，服务端始终为 1.0，取值范围为 (0.0, 1.0]
        /// </summary>
        public float SampleFraction;
        /// <summary>
        /// Time Scale 的平均值
        /// </summary>
        public float TimeScale;
        /// <summary>
        /// 当前插值偏移
        /// </summary>
        public float InterpolationOffset;
        /// <summary>
        /// 当前插值缩放比例
        /// </summary>
        public float InterpolationScale;
        /// <summary>
        /// Command Stream 的 Age
        /// </summary>
        public float CommandAge;
        /// <summary>
        /// 估算的往返时间
        /// </summary>
        public float Rtt;
        /// <summary>
        /// 估算的抖动
        /// </summary>
        public float Jitter;
        /// <summary>
        /// Snapshot 的最小 Age
        /// </summary>
        public float SnapshotAgeMin;
        /// <summary>
        /// Snapshot 的最大 Age
        /// </summary>
        public float SnapshotAgeMax;
    }

    /// <summary>
    /// 保存 Snapshot 指标的单例组件
    /// </summary>
    public struct SnapshotMetrics : IComponentData
    {
        /// <summary>
        /// 收集 Snapshot 指标时的 Server Tick
        /// </summary>
        public uint SnapshotTick;
        /// <summary>
        /// Snapshot 数据包总大小
        /// </summary>
        public uint TotalSizeInBits;
        /// <summary>
        /// Snapshot 数据包中的 Ghost 总数
        /// </summary>
        public uint TotalGhostCount;
        /// <summary>
        /// Despawn 数量
        /// </summary>
        public uint DestroyInstanceCount;
        /// <summary>
        /// Despawn 数据包大小
        /// </summary>
        public uint DestroySizeInBits;
    }

    /// <summary>
    /// 监控 Ghost 的序列化耗时
    /// <remarks>
    /// 若要确定各索引对应的值，还需要从 <see cref="GhostNames"/> 获取索引
    /// </remarks>
    /// </summary>
    public struct GhostSerializationMetrics : IBufferElementData
    {
        /// <summary>
        /// Ghost 序列化耗时，单位为微秒
        /// </summary>
        public float LastRecordedValue;
    }

    /// <summary>
    /// 监控 Ghost 的预测误差
    /// <remarks>
    /// 若要确定各索引对应的值，还需要从 <see cref="PredictionErrorNames"/> 获取索引
    /// </remarks>
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PredictionErrorMetrics : IBufferElementData
    {
        /// <summary>
        /// 最近一次记录的预测误差指标
        /// </summary>
        public float Value;
    }

    /// <summary>
    /// 当前所有可用预测误差名称的列表
    /// 该列表与 <see cref="PredictionErrorMetrics"/> 一一对应
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PredictionErrorNames : IBufferElementData
    {
        /// <summary>
        /// 预测误差类型名称
        /// </summary>
        public FixedString128Bytes Name;
    }
    /// <summary>
    /// 当前所有可用 Ghost 的列表
    /// 该列表与 <see cref="GhostSerializationMetrics"/> 和 <see cref="GhostMetrics"/> 一一对应
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostNames : IBufferElementData
    {
        /// <summary>
        /// Ghost 类型名称
        /// </summary>
        public FixedString64Bytes Name;
    }

    /// <summary>
    /// 已序列化 Ghost 指标的列表
    /// <remarks>
    /// 若要查找每项指标对应的 Ghost 名称，该 Buffer 的各索引与 <see cref="GhostNames"/> 一一对应
    /// </remarks>
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct GhostMetrics : IBufferElementData
    {
        /// <summary>
        /// 序列化数据包中该 Ghost 的实例数量
        /// </summary>
        public uint InstanceCount;
        /// <summary>
        /// 已序列化 Ghost 的大小，单位为位
        /// </summary>
        public uint SizeInBits;
        /// <summary>
        /// <remarks>仅服务端可用</remarks>
        /// 创建 Snapshot 时需要遍历的 Chunk 数量
        /// </summary>
        public uint ChunkCount;   // 服务端
        /// <summary>
        /// <remarks>仅客户端可用</remarks>
        /// 收到的未压缩 Ghost 数量，通常由新 Spawn 导致
        /// </summary>
        public uint Uncompressed; // 客户端
    }
}

#endif
