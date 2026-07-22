using Unity.Profiling.Editor;
using System;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// NetCode for Entities 的 Server World Profiler 模块
    /// </summary>
    [ProfilerModuleMetadata("Server World", IconPath = "Packages/com.unity.netcode/EditorIcons/GhostAuthoring.png")]
    class ServerWorldProfiler : ProfilerModule
    {
        public ServerWorldProfiler()
            : base(new[]
            {
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostSnapshotsCounterNameServer, ProfilerCategory.Network),
                new ProfilerCounterDescriptor(ProfilerMetricsConstants.GhostInstancesCounterNameServer, ProfilerCategory.Network)
            }, ProfilerModuleChartType.Line) { } // TODO 支持后改用柱状图

        public override ProfilerModuleViewController CreateDetailsViewController()
            => new NetcodeForEntitiesProfilerModuleViewController(ProfilerWindow, NetworkRole.Server);
    }
}
