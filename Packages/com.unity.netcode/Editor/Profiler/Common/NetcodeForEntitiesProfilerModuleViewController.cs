using System;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling.Editor;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    [Serializable]
    class NetcodeForEntitiesProfilerModuleViewController : ProfilerModuleViewController
    {
        const string k_StyleSheetPath = "Packages/com.unity.netcode/Editor/Profiler/netcode-profiler.uss";
        const string k_VariablesDarkPath = "Packages/com.unity.netcode/Editor/Profiler/profiler-vars-dark.uss";
        const string k_VariablesLightPath = "Packages/com.unity.netcode/Editor/Profiler/profiler-vars-light.uss";

        NetworkRole m_NetworkRole;
        TabView m_TabView;
        GhostSnapshotsTab m_GhostSnapshotTab;
        FrameOverviewTab m_FrameOverViewTab;
        PredictionInterpolationTab m_PredictionInterpolationTab;
        NativeArray<UncompressedSizesPerType> m_UncompressedSizesArrayServer;
        NativeArray<UncompressedSizesPerType> m_UncompressedSizesArrayClient;

        internal NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow profilerWindow, NetworkRole networkRole)
            : base(profilerWindow)
        {
            m_NetworkRole = networkRole;
        }

        // 初始化 View Controller 的事件与 UI
        protected override VisualElement CreateView()
        {
            ProfilerWindow.SelectedFrameIndexChanged += OnSelectedFrameIndexChanged;
            ProfilerDriver.profileCleared += OnProfileCleared;

            var container = new VisualElement();
            var ussFile = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_StyleSheetPath);

            var ussVariables = EditorGUIUtility.isProSkin
                ? AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesDarkPath)
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(k_VariablesLightPath);

            container.styleSheets.Add(ussFile);
            container.styleSheets.Add(ussVariables);
            var networkRolePrefix = m_NetworkRole.ToString();
            m_TabView ??= new TabView { viewDataKey = $"N4E{networkRolePrefix}ProfilerTabView" };

            m_FrameOverViewTab ??= new FrameOverviewTab(m_NetworkRole);
            m_FrameOverViewTab.SetSnapshotViewDetailsCallback(ActivateGhostSnapshotTab);
            m_TabView.Add(m_FrameOverViewTab);

            m_GhostSnapshotTab ??= new GhostSnapshotsTab(m_NetworkRole);
            m_TabView.Add(m_GhostSnapshotTab);

            m_PredictionInterpolationTab ??= new PredictionInterpolationTab(m_NetworkRole);
            m_TabView.Add(m_PredictionInterpolationTab);

            container.Add(m_TabView);

            var frameToSelect = ProfilerWindow.selectedFrameIndex == -1 ? ProfilerWindow.lastAvailableFrameIndex : ProfilerWindow.selectedFrameIndex;

            // Profiler 窗口尚未完全初始化，因此需要稍作等待后再选择帧索引
            container.schedule.Execute(() =>
            {
                OnSelectedFrameIndexChanged(frameToSelect);
            }).ExecuteLater(10);

            return container;
        }

        void ActivateGhostSnapshotTab()
        {
            m_TabView.activeTab = m_GhostSnapshotTab;
        }

        void UpdateTabs(NetcodeFrameData frameData)
        {
            m_GhostSnapshotTab.Update(frameData);
            m_FrameOverViewTab.Update(frameData);
            if (m_NetworkRole == NetworkRole.Client)
                m_PredictionInterpolationTab.Update(frameData);
        }

        void OnProfileCleared()
        {
            if (m_UncompressedSizesArrayServer.IsCreated)
                m_UncompressedSizesArrayServer.Dispose();

            if (m_UncompressedSizesArrayClient.IsCreated)
                m_UncompressedSizesArrayClient.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            ProfilerWindow.SelectedFrameIndexChanged -= OnSelectedFrameIndexChanged;
            ProfilerDriver.profileCleared -= OnProfileCleared;
            base.Dispose(true);
        }

        // Profiler 窗口中选中帧索引变化时调用
        // Profiler 运行期间也会持续调用
        void OnSelectedFrameIndexChanged(long selectedFrameIndex)
        {
            if (selectedFrameIndex == -1)
                return;
            var frameData = BuildFrameData(selectedFrameIndex);
            UpdateTabs(frameData);
        }

        // 根据选中帧构建相关 NetCode 帧数据的主方法
        NetcodeFrameData BuildFrameData(long selectedFrameIndex)
        {
            using (var frameDataView = ProfilerDriver.GetRawFrameDataView((int)selectedFrameIndex, 0))
            {
                if (frameDataView == null || frameDataView.valid == false)
                {
                    return default;
                }

                // 根据当前 Profiler 模块选择用于获取 Frame Metadata 的 GUID
                var guid = m_NetworkRole == NetworkRole.Server ? ProfilerMetricsConstants.ServerGuid : ProfilerMetricsConstants.ClientGuid;

                // 获取已序列化的 Ghost 统计
                var serializedGhostStatsSnapshot = frameDataView.GetFrameMetaData<byte>(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
                if (serializedGhostStatsSnapshot.Length == 0)
                {
                    // TODO 此时没有收到 Snapshot，应在 UI 中突出显示，也可保留上一帧的累计统计
                    return default;
                }

                // 反序列化 Ghost 统计
                var ghostStatsSnapshot = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, serializedGhostStatsSnapshot);
                var perGhostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;

                var profilerFrameMetaData = GetProfilerFrameMetaData(frameDataView, guid);
                var uncompressedSizes = GetUncompressedSizes(profilerFrameMetaData.UncompressedSizesPerType);
                var tickData = CreateTickData(ghostStatsSnapshot, profilerFrameMetaData);

                uint ghostTypesSizeInBits = 0;
                uint totalInstanceCount = 0;

                // 遍历 Ghost
                for (var ghostIndex = 0; ghostIndex < profilerFrameMetaData.GhostNames.Length; ghostIndex++)
                {
                    var ghostTypeStats = perGhostTypeStats[ghostIndex];

                    uint sumComponentTypeSizePerType = 0;
                    ghostTypesSizeInBits += ghostTypeStats.SizeInBits;
                    totalInstanceCount += ghostTypeStats.EntityCount;

                    // 获取该 Ghost 类型的组件统计
                    var componentTypesData = CreateComponentTypesData(ghostTypeStats,
                        profilerFrameMetaData,
                        ghostIndex,
                        ref sumComponentTypeSizePerType);

                    var ghostTypeData = CreateGhostTypeData(profilerFrameMetaData, ghostIndex, ghostTypeStats, componentTypesData, uncompressedSizes, sumComponentTypeSizePerType);

                    tickData.ghostTypeData[ghostIndex] = ghostTypeData;
                }

                tickData.snapshotSizeInBits = ghostStatsSnapshot.SnapshotTotalSizeInBits;
                tickData.overheadSize = ghostStatsSnapshot.SnapshotTotalSizeInBits - ghostTypesSizeInBits;
                tickData.totalInstanceCount = totalInstanceCount;

                // 预测误差
                for (var i = 0; i < profilerFrameMetaData.PredictionErrors.Length; i++)
                {
                    var predictionErrorData = new PredictionErrorData();
                    predictionErrorData.name = profilerFrameMetaData.PredictionErrors[i].Name;
                    if (i < profilerFrameMetaData.PredictionErrorMetrics.Length)
                        predictionErrorData.errorValue = profilerFrameMetaData.PredictionErrorMetrics[i].Value;
                    else
                        predictionErrorData.errorValue = 0;

                    tickData.predictionErrors[i] = predictionErrorData;
                }

                var frameData = new NetcodeFrameData
                {
                    tickData = new NativeArray<TickData>(1, Allocator.Temp)
                    {
                        [0] = tickData
                    },
                    frameCount = (uint)(ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex), // TODO 确认是否仍需该值，可能用于计算平均值
                    jitter = profilerFrameMetaData.NetworkMetrics.Jitter,
                    rtt = profilerFrameMetaData.NetworkMetrics.Rtt,
                    serverTickSent = profilerFrameMetaData.ProfilerMetrics.ServerTick,
                    totalSizeSentByServerInBits = profilerFrameMetaData.ProfilerMetrics.TotalSizeSentByServerInBits,
                    totalPacketCountSentByServer = profilerFrameMetaData.ProfilerMetrics.TotalPacketCountSentByServer,
                    totalSizeReceivedByClientInBits = profilerFrameMetaData.ProfilerMetrics.TotalSizeReceivedByClientInBits,
                    totalPacketCountReceivedByClient = profilerFrameMetaData.ProfilerMetrics.TotalPacketCountReceivedByClient
                };

                return frameData;
            }
        }

        // 根据逐帧发送的 Profiler 数据构建 ProfilerGhostTypeData
        static ProfilerGhostTypeData CreateGhostTypeData(ProfilerFrameMetadata profilerFrameMetaData, int ghostIndex, UnsafeGhostStatsSnapshot.PerGhostTypeStats ghostTypeStats, NativeArray<ProfilerGhostTypeData> componentsStats, NativeArray<UncompressedSizesPerType> uncompressedSizes, uint sumComponentTypeSizePerType)
        {
            var ghostTypeData = new ProfilerGhostTypeData
            {
                name = profilerFrameMetaData.GhostNames[ghostIndex].Name,
                sizeInBits = ghostTypeStats.SizeInBits,
                instanceCount = (int)ghostTypeStats.EntityCount,
                componentsPerType = componentsStats,
                newInstancesCount = ghostTypeStats.UncompressedCount
            };

            if (ghostTypeStats.SizeInBits != 0 && ghostTypeStats.EntityCount != 0)
            {
                var sizePerInstance = (float)ghostTypeStats.SizeInBits / ghostTypeStats.EntityCount;
                ghostTypeData.avgSizePerEntity = (float)Math.Round(sizePerInstance, 2);
                // 未压缩尺寸 Buffer 可能尚未创建，此时跳过压缩效率计算
                if (uncompressedSizes.Length > ghostIndex && uncompressedSizes[ghostIndex].SizeInBytes > 0)
                    ghostTypeData.combinedCompressionEfficiency = (float)Math.Round(1f - sizePerInstance / (uncompressedSizes[ghostIndex].SizeInBytes * 8f), 2) * 100f;
            }

            ghostTypeData.overheadSize = ghostTypeStats.SizeInBits - sumComponentTypeSizePerType;
            return ghostTypeData;
        }

        // 根据逐帧发送的 Profiler 数据构建组件类型数据
        static NativeArray<ProfilerGhostTypeData> CreateComponentTypesData(UnsafeGhostStatsSnapshot.PerGhostTypeStats ghostTypeStats, ProfilerFrameMetadata profilerFrameMetaData, int ghostIndex, ref uint sumComponentTypeSizePerType)
        {
            var componentsPerType = new NativeArray<ProfilerGhostTypeData>(ghostTypeStats.PerComponentStatsList.Length, Allocator.Temp);

            // 遍历每种 Ghost 类型的组件
            for (var componentIndex = 0; componentIndex < ghostTypeStats.PerComponentStatsList.Length; componentIndex++)
            {
                var componentTypeStat = ghostTypeStats.PerComponentStatsList[componentIndex];
                var serializerIndex = componentTypeStat.SerializerIndex(componentIndex,
                    profilerFrameMetaData.PrefabSerializers[ghostIndex], profilerFrameMetaData.SerializerStates,
                    profilerFrameMetaData.ComponentIndices);
                var type = componentTypeStat.ComponentType(serializerIndex, profilerFrameMetaData.SerializerStates);
                var uncompressedSize = componentTypeStat.SnapshotSize(serializerIndex, profilerFrameMetaData.SerializerStates);

                var compressionEfficiency = -1f;
                if (uncompressedSize > 0 && componentTypeStat.SizeInSnapshotInBits > 0)
                    compressionEfficiency = (float)Math.Round(1f - componentTypeStat.SizeInSnapshotInBits / (uncompressedSize * 8f * ghostTypeStats.EntityCount), 2) * 100f;

                var sizePerComponent = (float)componentTypeStat.SizeInSnapshotInBits / ghostTypeStats.EntityCount;

                var ghostTypeComponentData = new ProfilerGhostTypeData
                {
                    sizeInBits = componentTypeStat.SizeInSnapshotInBits,
                    name = type.ToString(),
                    instanceCount = (int)ghostTypeStats.EntityCount,
                    combinedCompressionEfficiency = compressionEfficiency,
                    avgSizePerEntity = (float)Math.Round(sizePerComponent, 2)
                };

                sumComponentTypeSizePerType += ghostTypeComponentData.sizeInBits;
                componentsPerType[componentIndex] = ghostTypeComponentData;
            }

            return componentsPerType;
        }

        // 根据逐帧发送的 Profiler 数据创建 TickData
        static TickData CreateTickData(UnsafeGhostStatsSnapshot ghostStatsSnapshot, ProfilerFrameMetadata profilerFrameMetaData)
        {
            var inputTargetTick = new NetworkTick { SerializedData = profilerFrameMetaData.CommandStats[0] };
            var tickData = new TickData
            {
                tick = ghostStatsSnapshot.Tick,
                packetCount = ghostStatsSnapshot.PacketsCount,
                timeScale = profilerFrameMetaData.NetworkMetrics.TimeScale,
                interpolationDelay = profilerFrameMetaData.NetworkMetrics.InterpolationOffset,
                interpolationScale = profilerFrameMetaData.NetworkMetrics.InterpolationScale,
                snapshotAgeMin = profilerFrameMetaData.NetworkMetrics.SnapshotAgeMin,
                snapshotAgeMax = profilerFrameMetaData.NetworkMetrics.SnapshotAgeMax,
                inputTargetTick = inputTargetTick,
                commandSizeInBits = profilerFrameMetaData.CommandStats[1],
                commandAge = profilerFrameMetaData.NetworkMetrics.CommandAge,
                discardedPackets = profilerFrameMetaData.CommandStats[2],
                ghostTypeData = new NativeArray<ProfilerGhostTypeData>(profilerFrameMetaData.GhostNames.Length, Allocator.Temp),
                predictionErrors = new NativeArray<PredictionErrorData>(profilerFrameMetaData.PredictionErrors.Length, Allocator.Temp)
            };
            return tickData;
        }

        // 获取每帧发送给 Profiler 的全部统计数据
        internal static ProfilerFrameMetadata GetProfilerFrameMetaData(RawFrameDataView frameDataView, Guid guid)
        {
            // 获取 Profiler 指标与其他 Metadata
            var profilerFrameMetaData = new ProfilerFrameMetadata();
            profilerFrameMetaData.ProfilerMetrics = frameDataView.GetFrameMetaData<ProfilerMetrics>(guid, ProfilerMetricsConstants.ProfilerMetricsTag)[0];
            profilerFrameMetaData.UncompressedSizesPerType = frameDataView.GetFrameMetaData<UncompressedSizesPerType>(guid, ProfilerMetricsConstants.UncompressedSizesPerTypeTag);
            profilerFrameMetaData.PrefabSerializers = frameDataView.GetFrameMetaData<GhostCollectionPrefabSerializer>(guid, ProfilerMetricsConstants.PrefabSerializersTag);
            profilerFrameMetaData.SerializerStates = frameDataView.GetFrameMetaData<GhostComponentSerializer.State>(guid, ProfilerMetricsConstants.SerializerStatesTag);
            profilerFrameMetaData.ComponentIndices = frameDataView.GetFrameMetaData<GhostCollectionComponentIndex>(guid, ProfilerMetricsConstants.ComponentIndicesTag);
            profilerFrameMetaData.GhostNames = frameDataView.GetFrameMetaData<GhostNames>(guid, ProfilerMetricsConstants.GhostNamesTag);
            profilerFrameMetaData.NetworkMetrics = frameDataView.GetFrameMetaData<NetworkMetrics>(guid, ProfilerMetricsConstants.NetworkMetricsTag)[0];
            profilerFrameMetaData.PredictionErrors = frameDataView.GetFrameMetaData<PredictionErrorNames>(guid, ProfilerMetricsConstants.PredictionErrorNamesTag);
            profilerFrameMetaData.PredictionErrorMetrics = frameDataView.GetFrameMetaData<PredictionErrorMetrics>(guid, ProfilerMetricsConstants.PredictionErrorMetricsTag);
            profilerFrameMetaData.CommandStats = frameDataView.GetFrameMetaData<uint>(guid, ProfilerMetricsConstants.CommandStatsTag);
            return profilerFrameMetaData;
        }

        // 获取服务端或客户端各类型的未压缩大小
        NativeArray<UncompressedSizesPerType> GetUncompressedSizes(NativeArray<UncompressedSizesPerType> uncompressedSizesPerType)
        {
            // 这是会话级数据，仅在尚未创建时获取未压缩大小
            NativeArray<UncompressedSizesPerType> uncompressedSizes;
            switch (m_NetworkRole)
            {
                case NetworkRole.Server:
                {
                    if (!m_UncompressedSizesArrayServer.IsCreated)
                    {
                        m_UncompressedSizesArrayServer = new NativeArray<UncompressedSizesPerType>(uncompressedSizesPerType.Length, Allocator.Persistent);
                        m_UncompressedSizesArrayServer.CopyFrom(uncompressedSizesPerType);
                    }
                    uncompressedSizes = m_UncompressedSizesArrayServer;
                    break;
                }
                case NetworkRole.Client:
                {
                    if (!m_UncompressedSizesArrayClient.IsCreated)
                    {
                        m_UncompressedSizesArrayClient = new NativeArray<UncompressedSizesPerType>(uncompressedSizesPerType.Length, Allocator.Persistent);
                        m_UncompressedSizesArrayClient.CopyFrom(uncompressedSizesPerType);
                    }
                    uncompressedSizes = m_UncompressedSizesArrayClient;
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return uncompressedSizes;
        }
    }
}
