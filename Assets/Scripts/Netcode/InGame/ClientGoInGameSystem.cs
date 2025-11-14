using Unity.NetCode;
using Unity.Entities;

public struct GoInGameRequest : IRpcCommand {}

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct ClientGoInGameSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        if (!SystemAPI.TryGetSingletonEntity<NetworkId>(out var connection)) return; // 无连接
        if (SystemAPI.HasComponent<NetworkStreamInGame>(connection)) return; // 已 Ingame

        var rpcEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(rpcEntity, new GoInGameRequest());
        state.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });

        state.EntityManager.AddComponent<NetworkStreamInGame>(connection);
        UnityEngine.Debug.Log("[Client] Sent GoInGameRequest and marked InGame locally");
    }
}
