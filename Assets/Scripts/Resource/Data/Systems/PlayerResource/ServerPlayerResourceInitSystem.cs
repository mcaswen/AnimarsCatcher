using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;


[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ServerGoInGameDebugSystem))] 
[UpdateAfter(typeof(ServerStartGameSystem))] 
public partial struct ServerPlayerResourceInitSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var prefab = SystemAPI.GetSingleton<PlayerResourceGhostPrefab>().Value;

        // 遍历所有连接，给没有 PlayerResourceState 的连接补一份
        foreach (var (networkId, connectionEntity) in SystemAPI
                     .Query<RefRO<NetworkId>>()
                     .WithEntityAccess())
        {
            int id = networkId.ValueRO.Value;

            // 检查是否已经有对应资源实体
            bool hasResource = false;
            foreach (var owner in SystemAPI
                         .Query<RefRO<GhostOwner>>()
                         .WithAll<PlayerResourceTag>())
            {
                if (owner.ValueRO.NetworkId == id)
                {
                    hasResource = true;
                    break;
                }
            }

            if (hasResource)
                continue;

            // 新建资源实体
            var resourceEntity = entityCommandBuffer.Instantiate(prefab);
            
            entityCommandBuffer.SetComponent(resourceEntity, new GhostOwner { NetworkId = id });
            entityCommandBuffer.SetComponent(resourceEntity, new PlayerResourceState
            {
                TotalPickerAniCount   = 0,
                TotalBlasterAniCount  = 0,
                InTeamPickerAniCount  = 0,
                InTeamBlasterAniCount = 0,
                FoodSum               = 99,
                CrystalSum            = 99
            });

            Debug.Log($"[ServerPlayerResourceInitSystem] Created PlayerResourceState for NetworkId = {id}");
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
