using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation.Harness
{
    /// <summary>
    /// 区分未经修正的历史基线和只修复正确性与测量噪声的归一化基线
    /// </summary>
    public enum LegacyNavigationBaselineVariant : byte
    {
        RawLegacy,
        NormalizedLegacy
    }

    /// <summary>
    /// 标识 Legacy 导航基准当前所处的生命周期阶段
    /// </summary>
    public enum LegacyNavigationBenchmarkPhase : byte
    {
        WaitingForScene,
        Warmup,
        Sampling,
        Completed,
        Failed
    }

    /// <summary>
    /// 保存一次 Legacy 导航基准运行的固定输入与结果元数据
    /// </summary>
    public struct LegacyNavigationBenchmarkConfig : IComponentData
    {
        public int AgentCount;
        public int RandomSeed;
        public int WarmupTicks;
        public int SampleTicks;
        public int SpawnColumnCount;
        public float SpawnSpacing;
        public float3 SpawnOrigin;
        public LegacyNavigationBaselineVariant BaselineVariant;
        public FixedString64Bytes BaselineVersion;
        public FixedString64Bytes ScenarioName;
        public FixedString64Bytes GitCommit;
        public FixedString128Bytes MapSceneHash;
        public FixedString128Bytes ReplayScriptHash;
        public FixedString128Bytes ResultDirectory;
        public byte AutoQuit;
    }

    /// <summary>
    /// 保存 Harness 在预热、采样和导出阶段之间推进所需的运行时状态
    /// </summary>
    public struct LegacyNavigationBenchmarkState : IComponentData
    {
        public LegacyNavigationBenchmarkPhase Phase;
        public int PhaseTick;
        public int NextCommandIndex;
        public int AppliedCommandCount;
        public Entity LeaderEntity;
        public float3 LastFormationCenter;
        public quaternion LastFormationRotation;
        public long FrameStartTimestamp;
        public long FrameStartAllocatedBytes;
        public byte RecordCurrentTick;
        public byte ResultExported;
    }

    /// <summary>
    /// 保存一次已经通过权限校验的确定性移动命令
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct LegacyNavigationBenchmarkCommandElement : IBufferElementData
    {
        public int Tick;
        public float3 TargetOffset;
    }

    /// <summary>
    /// 保存单个 Server Simulation Tick 的墙钟时间和主线程分配量
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct LegacyNavigationBenchmarkSampleElement : IBufferElementData
    {
        public double ServerSimulationMilliseconds;
        public long MainThreadAllocatedBytes;
    }

    /// <summary>
    /// 累计 Legacy NavMesh 路径计算次数与成功状态
    /// </summary>
    public struct LegacyNavigationBenchmarkCounters : IComponentData
    {
        public int PathRequestCount;
        public int PathSuccessCount;
        public int PathFailureCount;
    }

    /// <summary>
    /// 标识由 Harness 创建并负责统计与销毁的 Ani
    /// </summary>
    public struct LegacyNavigationBenchmarkAniTag : IComponentData
    {
    }

    /// <summary>
    /// 标识 Harness 创建的非 Ani 辅助实体
    /// </summary>
    public struct LegacyNavigationBenchmarkOwnedTag : IComponentData
    {
    }
}
