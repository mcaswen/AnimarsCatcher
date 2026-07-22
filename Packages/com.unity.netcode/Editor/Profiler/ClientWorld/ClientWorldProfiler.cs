using Unity.Profiling.Editor;
using System;
using Unity.Profiling;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// NetCode for Entities 的 Client World Profiler 模块
    /// </summary>
    [ProfilerModuleMetadata("Client World", IconPath = "Packages/com.unity.netcode/EditorIcons/GhostAuthoring.png")]
    class ClientWorldProfiler : ProfilerModule
    {
        public ClientWorldProfiler()
            : base(new[]
            {
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostSnapshotsCounterNameClient, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostInstancesCounterNameClient, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.JitterCounterName, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.RTTCounterName, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.SnapshotAgeMinCounterName, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.SnapshotAgeMaxCounterName, ProfilerCategory.Network)
            }, ProfilerModuleChartType.Line) { } // TODO 支持后改用柱状图

        public override ProfilerModuleViewController CreateDetailsViewController()
            => new NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow, NetworkRole.Client);
    }
}
