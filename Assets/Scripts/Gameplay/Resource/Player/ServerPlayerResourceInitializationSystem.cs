using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{

    /// <summary>
    /// 为进入游戏且尚无资源状态的连接生成玩家资源 Ghost
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerPlayerResourceInitializationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate<PlayerResourceGhostPrefabReference>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var prefab = SystemAPI.GetSingleton<PlayerResourceGhostPrefabReference>().Value;

            // 遍历连接并保证每个 NetworkId 只有一份资源实体
            foreach (var (networkId, connectionEntity) in SystemAPI
                         .Query<RefRO<NetworkId>>()
                         .WithEntityAccess())
            {
                int id = networkId.ValueRO.Value;

                // 先查询已有 GhostOwner 防止重复实例化
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
                    FoodAmount               = 20,
                    CrystalAmount            = 5
                });

                Debug.Log($"[ServerPlayerResourceInitializationSystem] Created PlayerResourceState for NetworkId = {id}");
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }
    }
}
