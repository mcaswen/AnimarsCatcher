using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct PlayerResourceApplyCollectedSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var resourceQuery = SystemAPI.QueryBuilder()
            .WithAll<PlayerResourceTag, PlayerResourceState, GhostOwner>()
            .Build();

        var resourceEntities = resourceQuery.ToEntityArray(Allocator.Temp);
        var resourceStates = resourceQuery.ToComponentDataArray<PlayerResourceState>(Allocator.Temp);
        var owners = resourceQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);

        var idToIndex = new NativeHashMap<int, int>(resourceEntities.Length, Allocator.Temp);
        for (int i = 0; i < resourceEntities.Length; i++)
        {
            idToIndex.TryAdd(owners[i].NetworkId, i);
        }

        // 遍历所有 FoodCollectedEvent buffer，应用收集数量
        foreach (var buffer in SystemAPI.Query<DynamicBuffer<FoodAmountChangedEvent>>())
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                var eventData = buffer[i];
                if (!idToIndex.TryGetValue(eventData.OwnerNetworkId, out var idx))
                    continue;

                var resourceState = resourceStates[idx];
                resourceState.FoodSum += eventData.Amount;
                resourceStates[idx] = resourceState;
            }

            buffer.Clear();
        }

        // 遍历所有 CrystalCollectedEvent buffer, 应用收集数量
        foreach (var buffer in SystemAPI.Query<DynamicBuffer<CrystalAmountChangedEvent>>())
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                var eventData = buffer[i];
                if (!idToIndex.TryGetValue(eventData.OwnerNetworkId, out var idx))
                    continue;

                var resourceState = resourceStates[idx];
                resourceState.CrystalSum += eventData.Amount;
                resourceStates[idx] = resourceState;
            }

            buffer.Clear();
        }

        for (int i = 0; i < resourceEntities.Length; i++)
        {
            state.EntityManager.SetComponentData(resourceEntities[i], resourceStates[i]);
        }

        resourceEntities.Dispose();
        resourceStates.Dispose();
        owners.Dispose();
        idToIndex.Dispose();
    }
}
