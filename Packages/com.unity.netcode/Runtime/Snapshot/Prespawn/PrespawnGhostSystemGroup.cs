using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 包含所有与预生成 Ghost 相关的 System
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostCollectionSystem))]
    public partial class PrespawnGhostSystemGroup : ComponentSystemGroup
    {
    }
}
