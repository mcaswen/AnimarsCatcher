using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

/// <summary>在 Server World 接收客户端大厅身份并通知服务器 UI</summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerReceiveLobbyIntroSystem : ISystem
{
    /// <summary>消费大厅介绍 RPC 并销毁对应请求实体</summary>
    /// <param name="state">系统状态</param>
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

            // 通过桥接事件通知托管 UI，避免 ECS 系统直接引用面板对象
            NetworkUIEventBridge.RaiseLobbyClientJoinedEvent(
                NetworkUIEventSource.ServerWorld,
                networkId,
                playerName,
                isLocalPlayer: false // Server World 中的连接都不属于本地 UI 玩家
            );

            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
