#if (UNITY_EDITOR || NETCODE_DEBUG) && (NETCODE_PROFILER_ENABLED && UNITY_6000_0_OR_NEWER)
using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    // Profiler 计数器与附加指标
    struct ProfilerMetrics : IComponentData
    {
        // 整个 Profiler 运行期间服务端累计发送的大小
        internal uint TotalSizeSentByServerInBits;

        // 整个 Profiler 运行期间服务端累计发送的数据包总数
        internal uint TotalPacketCountSentByServer;

        // 整个 Profiler 运行期间客户端累计接收的大小
        internal uint TotalSizeReceivedByClientInBits;

        // 整个 Profiler 运行期间客户端累计接收的数据包总数
        internal uint TotalPacketCountReceivedByClient;

        // 服务端 World 计数器
        internal ProfilerCounterValue<uint> ServerGhostInstancesCounter;
        internal ProfilerCounterValue<uint> ServerGhostSnapshotCounter;

        // 客户端 World 计数器
        internal ProfilerCounterValue<uint> ClientGhostInstancesCounter;
        internal ProfilerCounterValue<uint> ClientGhostSnapshotCounter;
        internal ProfilerCounterValue<float> JitterCounter;
        internal ProfilerCounterValue<float> RttCounter;
        internal ProfilerCounterValue<float> SnapshotAgeMinCounter;
        internal ProfilerCounterValue<float> SnapshotAgeMaxCounter;

        // 服务端 Tick
        internal NetworkTick ServerTick;
    }

    /// <summary>
    /// 保存每种 Ghost 类型的未压缩大小，单位为位
    /// 用于在 N4E Profiler 模块中计算压缩效率
    /// 只需要 GhostCollectionPrefab 中各类型的大小，因此仅保存 GhostCollectionPrefabSerializer 每个条目的 Snapshot 大小
    /// </summary>
    [InternalBufferCapacity(0)]
    struct UncompressedSizesPerType : IBufferElementData
    {
        internal uint SizeInBytes;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    partial class ProfilerMetricsCollector : SystemBase
    {
        static readonly ComponentType[] k_RequiredStatsComponents =
        {
            ComponentType.ReadOnly<GhostMetricsMonitor>(),
            ComponentType.ReadOnly<NetworkMetrics>(),
            ComponentType.ReadOnly<GhostNames>(),
            ComponentType.ReadOnly<GhostMetrics>(),
            ComponentType.ReadOnly<PredictionErrorNames>(),
            ComponentType.ReadOnly<PredictionErrorMetrics>()
        };

        // 标记指标收集是否已经初始化
        bool m_MetricsCollectionEnabled;
        // 标记是否正在等待连接，以便连接建立后设置各类型的未压缩大小
        bool m_WaitForConnection;
        // 标记是否需要清理 Profiler 指标
        bool m_IsCleanedUp = true;

        void Initialize()
        {
            m_IsCleanedUp = false;

            if (!SystemAPI.TryGetSingletonEntity<ProfilerMetrics>(out var profilerMetricsSingleton))
                profilerMetricsSingleton = EntityManager.CreateSingleton<ProfilerMetrics>("ProfilerMetrics");

            if (!SystemAPI.TryGetSingletonEntity<UncompressedSizesPerType>(out _))
                EntityManager.CreateSingletonBuffer<UncompressedSizesPerType>("UncompressedSizesPerType");

            var profilerMetrics = new ProfilerMetrics
            {
                TotalSizeSentByServerInBits = 0,
                TotalPacketCountSentByServer = 0,
                TotalSizeReceivedByClientInBits = 0,
                TotalPacketCountReceivedByClient = 0,
                ServerTick = new NetworkTick()
            };

            if (World.IsServer())
            {
                profilerMetrics.ServerGhostInstancesCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostInstancesCounterNameServer, ProfilerMarkerDataUnit.Count);
                profilerMetrics.ServerGhostSnapshotCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostSnapshotsCounterNameServer, ProfilerMarkerDataUnit.Bytes);
            }
            else
            {
                profilerMetrics.ClientGhostInstancesCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostInstancesCounterNameClient, ProfilerMarkerDataUnit.Count);
                profilerMetrics.ClientGhostSnapshotCounter = new ProfilerCounterValue<uint>(ProfilerCategory.Network, ProfilerMetricsConstants.GhostSnapshotsCounterNameClient, ProfilerMarkerDataUnit.Bytes);
                profilerMetrics.JitterCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.JitterCounterName, ProfilerMarkerDataUnit.TimeNanoseconds);
                profilerMetrics.RttCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.RTTCounterName, ProfilerMarkerDataUnit.TimeNanoseconds);
                profilerMetrics.SnapshotAgeMinCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.SnapshotAgeMinCounterName, ProfilerMarkerDataUnit.Count);
                profilerMetrics.SnapshotAgeMaxCounter = new ProfilerCounterValue<float>(ProfilerCategory.Network, ProfilerMetricsConstants.SnapshotAgeMaxCounterName, ProfilerMarkerDataUnit.Count);
            }

            EntityManager.AddComponentData(profilerMetricsSingleton, profilerMetrics);

            if (!EntityManager.CreateEntityQuery(typeof(GhostMetricsMonitor)).TryGetSingletonEntity<GhostMetricsMonitor>(out var singletonEntity))
            {
                // GhostMetricsMonitor 单例不存在时创建一个新实体
                CreateGhostMetricsMonitorSingleton();
            }
            else
            {
                // 此时用户已经创建了 GhostMetricsMonitor 单例
                // 提醒用户该单例将被销毁，并需在禁用 Profiler 后重新创建
                Debug.LogWarning("A GhostMetricsMonitor singleton already exists in the world.\n " +
                    "This will be destroyed and recreated by the ProfilerMetricsCollector system.\n " +
                    "Please recreate your GhostMetricsMonitor after disabling the profiler.");

                EntityManager.DestroyEntity(singletonEntity);
                CreateGhostMetricsMonitorSingleton();
            }

            m_MetricsCollectionEnabled = true;
        }

        void CreateGhostMetricsMonitorSingleton()
        {
            var typeList = new NativeArray<ComponentType>(k_RequiredStatsComponents, Allocator.Temp);
            var metricSingleton = EntityManager.CreateEntity(EntityManager.CreateArchetype(typeList));
            EntityManager.SetName(metricSingleton, "MetricsMonitor");
        }

        protected override void OnUpdate()
        {
            if (!Profiler.enabled)
            {
                // 无法收到 Profiler 禁用通知，因此只清理一次指标并设置标记
                Cleanup();
                return;
            }

            if (!m_MetricsCollectionEnabled)
                Initialize();

            // 该方法还会检查 NetworkStreamInGame，因此必须在统计为空而可能提前退出前调用
            SetUncompressedSizesPerType();

            var ghostStatsSnapshot = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().GetAsyncStatsReader();
            var ghostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            var hasSnapshotStats = ghostTypeStats.IsCreated && ghostTypeStats.Length > 0;
            if (!hasSnapshotStats) return;

            var ghostMetrics = SystemAPI.GetSingletonBuffer<GhostMetrics>();
            var hasGhostMetrics = ghostMetrics.IsCreated && ghostMetrics.Length > 0;
            if (!hasGhostMetrics) return;

            var profilerMetrics = SystemAPI.GetSingleton<ProfilerMetrics>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            profilerMetrics.ServerTick = networkTime.ServerTick;

            UpdateProfilerCounters(ghostStatsSnapshot, ref profilerMetrics);

            if (World.IsServer())
            {
                profilerMetrics.TotalSizeSentByServerInBits += ghostStatsSnapshot.SnapshotTotalSizeInBits;
                profilerMetrics.TotalPacketCountSentByServer += ghostStatsSnapshot.PacketsCount;
            }
            else
            {
                profilerMetrics.TotalSizeReceivedByClientInBits += ghostStatsSnapshot.SnapshotTotalSizeInBits;
                profilerMetrics.TotalPacketCountReceivedByClient += ghostStatsSnapshot.PacketsCount;
            }

            SystemAPI.SetSingleton(profilerMetrics);

            // 序列化组件统计
            var serializedGhostStatsSnapshot = ghostStatsSnapshot.ToBlittableData(Allocator.Temp);

            var guid = World.IsServer() ? ProfilerMetricsConstants.ServerGuid : ProfilerMetricsConstants.ClientGuid;

            // 将数据发送到 Profiler
            EmitNetcodeFrameMetaData(guid, serializedGhostStatsSnapshot);
        }

        void SetUncompressedSizesPerType()
        {
            var uncompressedSizesPerType = SystemAPI.GetSingletonBuffer<UncompressedSizesPerType>();

            // 仅在启动时或数据变化时设置未压缩大小
            if (uncompressedSizesPerType.IsEmpty && !m_WaitForConnection)
            {
                // 初始设置
                var serializers = SystemAPI.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
                if (serializers.IsEmpty)
                    return;

                uncompressedSizesPerType.Resize(serializers.Length, NativeArrayOptions.ClearMemory);
                for (var i = 0; i < serializers.Length; i++)
                {
                    uncompressedSizesPerType.ElementAt(i).SizeInBytes = (uint)serializers[i].SnapshotSize;
                }

                return;
            }

            // 检查是否需要重建 Buffer
            if (SystemAPI.QueryBuilder().WithAll<NetworkStreamInGame>().Build().CalculateEntityCount() == 0)
            {
                // 等待连接重新建立后再重建 Buffer
                m_WaitForConnection = true;
                uncompressedSizesPerType.Clear();
            }
            else
            {
                if (m_WaitForConnection)
                {
                    m_WaitForConnection = false;
                    SetUncompressedSizesPerType();
                }
            }
        }

        void UpdateProfilerCounters(UnsafeGhostStatsSnapshot ghostStatsSnapshot, ref ProfilerMetrics profilerMetrics)
        {
            var ghostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            uint instancesCount = 0;
            for (var i = 0; i < ghostTypeStats.Length; i++)
            {
                instancesCount += ghostTypeStats[i].EntityCount;
            }

            // 更新图表计数器
            if (World.IsServer())
            {
                profilerMetrics.ServerGhostInstancesCounter.Value = instancesCount;
                profilerMetrics.ServerGhostSnapshotCounter.Value = ghostStatsSnapshot.SnapshotTotalSizeInBits >> 3; // 转换为字节
            }
            else
            {
                profilerMetrics.ClientGhostInstancesCounter.Value = instancesCount;
                profilerMetrics.ClientGhostSnapshotCounter.Value = ghostStatsSnapshot.SnapshotTotalSizeInBits >> 3; // 转换为字节

                var networkMetrics = SystemAPI.GetSingleton<NetworkMetrics>();
                // Profiler 使用纳秒作为基础单位
                profilerMetrics.JitterCounter.Value = networkMetrics.Jitter * 1_000_000f;
                profilerMetrics.RttCounter.Value = networkMetrics.Rtt * 1_000_000f;
                profilerMetrics.SnapshotAgeMinCounter.Value = networkMetrics.SnapshotAgeMin;
                profilerMetrics.SnapshotAgeMaxCounter.Value = networkMetrics.SnapshotAgeMax;
            }
        }

        [Conditional("ENABLE_PROFILER")]
        void EmitNetcodeFrameMetaData(Guid guid, NativeArray<byte> serializedGhostStatsSnapshot)
        {
            var serializers = SystemAPI.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
            var serializerStates = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
            var ghostCollectionComponentIndices = SystemAPI.GetSingletonBuffer<GhostCollectionComponentIndex>();
            var commandStats = SystemAPI.GetComponent<GhostStatsCollectionCommand>(SystemAPI.GetSingletonEntity<GhostStatsCollectionCommand>());
            var targetTick = commandStats.Value[0];
            var commandStatsSize = commandStats.Value[1];
            var discardedPackets = commandStats.Value[2];

            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.NetworkMetricsTag, new[] { SystemAPI.GetSingleton<NetworkMetrics>() });
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.GhostNamesTag, SystemAPI.GetSingletonBuffer<GhostNames>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.GhostMetricsTag, SystemAPI.GetSingletonBuffer<GhostMetrics>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PredictionErrorNamesTag, SystemAPI.GetSingletonBuffer<PredictionErrorNames>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PredictionErrorMetricsTag, SystemAPI.GetSingletonBuffer<PredictionErrorMetrics>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.ProfilerMetricsTag, new[] { SystemAPI.GetSingleton<ProfilerMetrics>() });
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.UncompressedSizesPerTypeTag, SystemAPI.GetSingletonBuffer<UncompressedSizesPerType>().AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag, serializedGhostStatsSnapshot);
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.PrefabSerializersTag, serializers.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.SerializerStatesTag, serializerStates.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.ComponentIndicesTag, ghostCollectionComponentIndices.AsNativeArray());
            Profiler.EmitFrameMetaData(guid, ProfilerMetricsConstants.CommandStatsTag, new [] { targetTick, commandStatsSize, discardedPackets });
        }

        void Cleanup()
        {
            if (m_IsCleanedUp)
                return;

            DestroySingletonEntity<GhostMetricsMonitor>();

            m_MetricsCollectionEnabled = false;
            m_IsCleanedUp = true;
        }

        protected override void OnDestroy()
        {
            Cleanup();
            DestroySingletonEntity<ProfilerMetrics>();
        }

        void DestroySingletonEntity<T>() where T : unmanaged, IComponentData
        {
            if (SystemAPI.TryGetSingletonEntity<T>(out var singletonEntity))
                EntityManager.DestroyEntity(singletonEntity);
        }
    }
}
#endif
