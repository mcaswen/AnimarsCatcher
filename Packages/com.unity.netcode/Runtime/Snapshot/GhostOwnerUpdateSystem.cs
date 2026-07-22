using Unity.Burst;
using Unity.Entities;

namespace Unity.NetCode
{
    [BurstCompile]
    [WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
    [WithChangeFilter(typeof(GhostOwner), typeof(GhostOwnerIsLocal))]
    internal partial struct UpdateGhostOwnerIsLocal : IJobEntity
    {
        public int localNetworkId;
        public void Execute(in GhostOwner ghostOwner, EnabledRefRW<GhostOwnerIsLocal> isLocalEnabledRef) => isLocalEnabledRef.ValueRW = ghostOwner.NetworkId == localNetworkId;
    }

    [BurstCompile]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))] // 确保所有权状态已更新
    [UpdateBefore(typeof(GhostInputSystemGroup))] // 确保收集输入时使用最新的输入所有者信息
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial struct GhostOwnerUpdateSystem : ISystem
    {
        public void OnCreate(ref SystemState state) { }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingletonEntity<LocalConnection>(out var connectionEntity))
            {
                var job = new UpdateGhostOwnerIsLocal() { localNetworkId = state.EntityManager.GetComponentData<NetworkId>(connectionEntity).Value };
                state.Dependency = job.ScheduleParallel(state.Dependency);
            }
        }
    }
}
