using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 根据本地拥有的 Ghost 建立客户端阵营单例并在完成后停用
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientLocalPlayerCampSystem : ISystem
    {
        bool _localPlayerIsSet;

        public void OnCreate(ref SystemState state)
        {
            var entity = state.EntityManager.CreateEntity(typeof(LocalPlayerCamp));
            state.EntityManager.SetComponentData(entity, new LocalPlayerCamp
            {
                Value = CampType.Alpha
            });

            state.RequireForUpdate<NetworkId>();
        }

        public void OnUpdate(ref SystemState state)
        {
            int localNetworkId = SystemAPI.GetSingleton<NetworkId>().Value;

            // 以 GhostOwner 为准，避免把其他客户端角色误认成本地玩家
            foreach (var (camp, owner) in SystemAPI
                         .Query<RefRO<Camp>, RefRO<GhostOwner>>())
            {
                if (owner.ValueRO.NetworkId != localNetworkId)
                    continue;

                var localCamp = SystemAPI.GetSingletonRW<LocalPlayerCamp>();
                if (localCamp.ValueRO.Value != camp.ValueRO.Value)
                {
                    localCamp.ValueRW = new LocalPlayerCamp { Value = camp.ValueRO.Value };
                    Debug.Log($"[Client] Local player camp set to {camp.ValueRO.Value}");
                }

                _localPlayerIsSet = true;
                break; // 本地拥有的主角色唯一，找到后即可停止扫描
            }

            if (_localPlayerIsSet)
            {
                state.Enabled = false;
            }
        }
    }
}
