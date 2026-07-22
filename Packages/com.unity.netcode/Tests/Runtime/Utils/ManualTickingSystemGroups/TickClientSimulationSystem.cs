using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 从另一个 World（通常为默认 World）更新客户端 World 的 <see cref="SimulationSystemGroup"/>
    /// 仅用于 DOTS Runtime、测试或其他特殊场景
    /// </summary>
#if !UNITY_SERVER || UNITY_EDITOR
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [UpdateAfter(typeof(TickServerSimulationSystem))]
#endif
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    internal partial class TickClientSimulationSystem : TickComponentSystemGroup
    {
    }
#endif
}
