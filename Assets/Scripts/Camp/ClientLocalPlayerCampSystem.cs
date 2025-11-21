using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

public struct LocalPlayerCamp : IComponentData
{
    public CampType Value;
}

// 维护一个本地玩家阵营的单例
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
        int myId = SystemAPI.GetSingleton<NetworkId>().Value;

        // 找本地拥有的角色，以 GhostOwner 为准
        foreach (var (camp, owner) in SystemAPI
                     .Query<RefRO<Camp>, RefRO<GhostOwner>>())
        {
            if (owner.ValueRO.NetworkId != myId)
                continue;

            var localCamp = SystemAPI.GetSingletonRW<LocalPlayerCamp>();
            if (localCamp.ValueRO.Value != camp.ValueRO.Value)
            {
                localCamp.ValueRW = new LocalPlayerCamp { Value = camp.ValueRO.Value };
                Debug.Log($"[Client] Local player camp set to {camp.ValueRO.Value}");
            }

            _localPlayerIsSet = true;
            break; // 角色唯一，找到后退出
        }

        if (_localPlayerIsSet)
        {
            state.Enabled = false;
        }
    }
}
