using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 从另一个 World（通常为默认 World）更新服务端 World 的 <see cref="SimulationSystemGroup"/>
    /// 仅用于 DOTS Runtime、测试或其他特殊场景
    /// </summary>
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    internal partial class TickServerSimulationSystem : TickComponentSystemGroup
    {
    }
#endif
}
