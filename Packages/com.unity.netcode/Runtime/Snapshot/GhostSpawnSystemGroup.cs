using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 所有需要在 Ghost 实体生成后处理它们的系统父组
    /// 此组在 <see cref="NetworkReceiveSystemGroup"/> 之前执行
    /// 确保收到服务器的新 Snapshot 时，所有新 Ghost 均已生成并准备接收新数据
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation, WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst=true)]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    [UpdateBefore(typeof(NetworkReceiveSystemGroup))]
    public partial class GhostSpawnSystemGroup : ComponentSystemGroup
    {
    }
}
