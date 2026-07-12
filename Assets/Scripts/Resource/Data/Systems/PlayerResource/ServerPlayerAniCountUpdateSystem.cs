using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在服务端按 GhostOwner 汇总每名玩家的 Ani 统计
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerPlayerAniCountUpdateSystem : ISystem
{
    /// <summary>
    /// 重建本帧玩家 Ani 总数 入队数和选中数
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        // 先收集玩家资源快照 统一在统计完成后回写
        var resourceQuery = SystemAPI.QueryBuilder()
            .WithAll<PlayerResourceTag, PlayerResourceState, GhostOwner>()
            .Build();

        var resourceEntities = resourceQuery.ToEntityArray(Allocator.Temp);
        var resourceStates   = resourceQuery.ToComponentDataArray<PlayerResourceState>(Allocator.Temp);
        var resourceOwners   = resourceQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);

        // 建立 NetworkId 到资源快照索引的常数时间映射
        var idToIndex = new NativeHashMap<int, int>(resourceEntities.Length, Allocator.Temp);
        for (int i = 0; i < resourceEntities.Length; i++)
        {
            idToIndex.TryAdd(resourceOwners[i].NetworkId, i);
        }

        // 统计值来自实体现状 每帧必须从零重建
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

        // 遍历 Ani 实体并按所有者累计各分类计数
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
