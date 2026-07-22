using System.Collections.Generic;
using Unity.Burst;
using Unity.Entities;
using Unity.PerformanceTesting;
using Unity.Profiling;

namespace Unity.NetCode.Tests.Performance
{
    internal class PerfTestRecorder
    {
        internal class SampleRecorder
        {
            public string SampleName;
            public ProfilerRecorder Recorder;
        }

        static public void StartRecording(SampleRecorder[] recorders)
        {
            foreach (var recorder in recorders)
            {
                recorder.Recorder.Stop();
                recorder.Recorder.Start();
            }
        }
        static public void StopRecording(SampleRecorder[] recorders)
        {
            foreach (var recorder in recorders)
            {
                recorder.Recorder.Stop();
            }
        }
        static public void Report(SampleRecorder[] recorders, string name)
        {
            // 第一个 Recorder 对应系统总标记，无需按子标记拆分
            for (var index = 0; index < recorders.Length; index++)
            {
                var r = recorders[index];
                var delta = (double)(r.Recorder.GetSample(0).Value) / 1e9;
                var sampleGroup = new SampleGroup($"{name} - {r.SampleName}", SampleUnit.Second);
                Measure.Custom(sampleGroup, delta);
            }
        }

        static public SampleRecorder[] CreateRecorders(World world,
            SystemHandle handle,
            params string[] customMarkers)
        {
            var ll = new List<SampleRecorder>();
            var profilerMarker = EntityManager.EntityManagerDebug.GetSystemProfilerMarkerName(world, handle);
            // 使用底层类别值匹配正确的 Burst 标记名称和类别
            var category = ProfilerCategory.Scripts;
            if (BurstCompiler.IsEnabled) { unsafe { *((short*)&category) = 3; } }
            var marker = new ProfilerMarker(category, profilerMarker);
            var recorder = new ProfilerRecorder(marker, 1, ProfilerRecorderOptions.CollectOnlyOnCurrentThread | ProfilerRecorderOptions.SumAllSamplesInFrame);
            ll.Add(new SampleRecorder
            {
                SampleName = profilerMarker,
                Recorder = recorder,
            });
            for (int i = 0; i < customMarkers.Length; ++i)
            {
                recorder = new ProfilerRecorder(customMarkers[i], 1,
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread | ProfilerRecorderOptions.SumAllSamplesInFrame);
                ll.Add(new SampleRecorder
                {
                    SampleName = customMarkers[i],
                    Recorder = recorder,
                });
            }

            return ll.ToArray();
        }
    }
}
