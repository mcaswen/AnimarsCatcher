using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerReceiveLobbyIntroSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (rpc, rpcRequestSource, rpcEntity) in SystemAPI
                     .Query<RefRO<ClientLobbyIntroRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connection = rpcRequestSource.ValueRO.SourceConnection;

            int networkId = -1;
            if (state.EntityManager.HasComponent<NetworkId>(connection))
            {
                networkId = state.EntityManager.GetComponentData<NetworkId>(connection).Value;
            }

            string playerName = rpc.ValueRO.PlayerName.ToString();
            Debug.Log($"[ServerLobbyIntroSystem] Received lobby intro from connection {networkId}: '{playerName}'");

            // 通知 UI
            NetUIEventBridge.RaiseLobbyClientJoinedEvent(
                NetUIEventSource.ServerWorld,
                networkId,
                playerName,
                isLocalPlayer: false // Server World 里看到的都非本地
            );

            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
