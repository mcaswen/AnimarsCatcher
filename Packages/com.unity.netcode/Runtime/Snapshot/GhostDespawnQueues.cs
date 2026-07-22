using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含 Ghost 生成与销毁所需 API 和集合的单例组件
    /// 参见 <see cref="GhostSpawnSystem"/> 和 <see cref="GhostDespawnSystem"/>
    /// </summary>
    internal struct GhostDespawnQueues : IComponentData
    {
        internal NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> InterpolatedDespawnQueue;
        internal NativeQueue<GhostDespawnSystem.DelayedDespawnGhost> PredictedDespawnQueue;
    }
}
