using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 从另一个 World（通常为默认 World）更新客户端 World 的 <see cref="PresentationSystemGroup"/>
    /// 仅用于 DOTS Runtime、测试或其他特殊场景
    /// </summary>
#if !UNITY_SERVER || UNITY_EDITOR
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    internal partial class TickClientPresentationSystem : TickComponentSystemGroup
    {
    }
#endif
}
