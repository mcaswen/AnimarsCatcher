using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// 简单的层级 Profiler
    /// 用于跟踪代码生成工具链的性能
    /// </summary>
    public class Profiler
    {
        public class Auto : IDisposable
        {
            public Auto(string name)
            {
                Profiler.Begin(name);
            }
            public void Dispose()
            {
                Profiler.End();
            }
        }

        private class Marker
        {
            public int parent;
            public int id;
            public string name;
            public long overheadTicks;
            public long ticks;
            public int count;
            public int depth;
            public List<int> children = new List<int>();
        }
        public class Record
        {
            public long totalTime;
            public int count;
        }

        private List<Marker> timers = new List<Marker>();
        private int currentId;

        // 实例通过静态入口访问但可能由多个线程调用，因此必须按线程隔离
        private static ThreadLocal<Profiler> _instance = new ThreadLocal<Profiler>(() =>
        {
            return new Profiler();
        });

        private static Profiler instance => _instance.Value;

        Profiler()
        {
            Init();
        }

        static public void Initialize()
        {
            instance.Init();
        }
        static public void Begin(string marker)
        {
            instance.Start(marker);
        }
        static public void End()
        {
            instance.Stop();
        }

        static public string PrintStats(bool fullTiming=false)
        {
            return instance.CollectStats(fullTiming);
        }

        int GetChildId(string name)
        {
            foreach (var childId in timers[currentId].children)
            {
                if (timers[childId].name == name)
                    return childId;
            }

            return -1;
        }

        private void Init()
        {
            timers.Clear();
            timers.Add(new Marker
            {
                parent = 0,
                id = 0,
                name = "Total",
                overheadTicks = 0,
                ticks = Stopwatch.GetTimestamp(),
                count = 0,
                depth = 0
            });
            currentId = 0;
        }

        private void Start(string name)
        {
            var t1 = Stopwatch.GetTimestamp();
            var childId = GetChildId(name);
            if(childId < 0)
            {
                var marker = new Marker
                {
                    name = name,
                    id = timers.Count,
                    parent = timers[currentId].id,
                    ticks = 0,
                    count = 0,
                    depth = timers[currentId].depth + 1
                };
                timers[currentId].children.Add(marker.id);
                timers.Add(marker);
                childId = marker.id;
            }
            var t2 = Stopwatch.GetTimestamp();
            ++timers[childId].count;
            timers[childId].ticks -= t2;
            timers[childId].overheadTicks += t2 - t1;
            currentId = childId;
        }

        private void Stop()
        {
            var marker = timers[currentId];
            marker.ticks += Stopwatch.GetTimestamp();
            currentId = marker.parent;
        }

        string CollectStats(bool fullTiming)
        {
            timers[0].ticks = Stopwatch.GetTimestamp() - timers[0].ticks;
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Timing:");
            // Timer 以深度优先顺序存储为树
            builder.Append($"{timers[0].name}: {(1000.0*(timers[0].ticks - timers[0].overheadTicks))/Stopwatch.Frequency} msec\n");
            if (fullTiming)
            {
                for (int i = 1; i < timers.Count; ++i)
                {
                    var node = timers[i];
                    var s = $"{node.name}: {(1000.0*node.ticks)/Stopwatch.Frequency} msec ({node.count}) [{(1000.0*node.overheadTicks)/Stopwatch.Frequency}]\n";
                    builder.Append(s.PadLeft(node.depth*2 + s.Length));
                }
            }
            timers[0].ticks = Stopwatch.GetTimestamp();
            return builder.ToString();
        }
    }
}
