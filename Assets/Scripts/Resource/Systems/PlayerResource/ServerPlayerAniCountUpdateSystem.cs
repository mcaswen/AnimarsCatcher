using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerPlayerAniCountUpdateSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // 收集所有玩家资源实体
        var resourceQuery = SystemAPI.QueryBuilder()
            .WithAll<PlayerResourceTag, PlayerResourceState, GhostOwner>()
            .Build();

        var resourceEntities = resourceQuery.ToEntityArray(Allocator.Temp);
        var resourceStates   = resourceQuery.ToComponentDataArray<PlayerResourceState>(Allocator.Temp);
        var resourceOwners   = resourceQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);

        // id -> 索引映射表
        var idToIndex = new NativeHashMap<int, int>(resourceEntities.Length, Allocator.Temp);
        for (int i = 0; i < resourceEntities.Length; i++)
        {
            idToIndex.TryAdd(resourceOwners[i].NetworkId, i);
        }

        // 统计前清零
        for (int i = 0; i < resourceStates.Length; i++)
        {
            var resourceState = resourceStates[i];

            resourceState.TotalPickerAniCount = 0;
            resourceState.TotalBlasterAniCount  = 0;

            resourceState.SelectedPickerAniCount = 0;
            resourceState.SelectedBlasterAniCount = 0;

            resourceState.InTeamPickerAniCount  = 0;
            resourceState.InTeamBlasterAniCount = 0;
            resourceStates[i] = resourceState;
        }

        // 遍历所有 Ani 实体进行计数
        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<PickerAniTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.TotalPickerAniCount++;
            resourceStates[idx] = resourceState;
        }

        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<BlasterAniTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.TotalBlasterAniCount++;
            resourceStates[idx] = resourceState;
        }

        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<PickerAniTag, AniInTeamTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.InTeamPickerAniCount++;
            resourceStates[idx] = resourceState;
        }

        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<BlasterAniTag, AniInTeamTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.InTeamBlasterAniCount++;
            resourceStates[idx] = resourceState;
        }

        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<BlasterAniTag, AniSelectedTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.SelectedBlasterAniCount++;
            resourceStates[idx] = resourceState;
        }

        foreach (var owner in SystemAPI
                     .Query<RefRO<GhostOwner>>()
                     .WithAll<PickerAniTag, AniSelectedTag>())
        {
            if (!idToIndex.TryGetValue(owner.ValueRO.NetworkId, out var idx))
                continue;

            var resourceState = resourceStates[idx];
            resourceState.SelectedPickerAniCount++;
            resourceStates[idx] = resourceState;
        }

        // 写回组件
        for (int i = 0; i < resourceEntities.Length; i++)
        {
            state.EntityManager.SetComponentData(resourceEntities[i], resourceStates[i]);
        }

        resourceEntities.Dispose();
        resourceStates.Dispose();
        resourceOwners.Dispose();
        idToIndex.Dispose();
    }
}
