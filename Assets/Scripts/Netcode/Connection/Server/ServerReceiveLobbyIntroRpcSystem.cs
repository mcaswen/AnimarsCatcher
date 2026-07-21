namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Collections;
    using UnityEngine;

    /// <summary>
    /// 在 Server World 接收客户端大厅身份并通知服务器 UI
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerReceiveLobbyIntroRpcSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, rpcRequestSource, rpcEntity) in SystemAPI
                         .Query<RefRO<LobbyIntroRequestRpc>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                var connection = rpcRequestSource.ValueRO.SourceConnection;

                int networkId = -1;
                if (state.EntityManager.HasComponent<NetworkId>(connection))
                {
                    networkId = state.EntityManager.GetComponentData<NetworkId>(connection).Value;
                }

                Debug.Log($"[ServerReceiveLobbyIntroRpcSystem] Received lobby intro from connection {networkId}: '{rpc.ValueRO.PlayerName}'");

                // 发布网络生命周期通知 由上层表现桥接决定如何呈现
                Entity notificationEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(
                    notificationEntity,
                    new LobbyClientJoinedNotification
                    {
                        Source = NetworkNotificationSource.ServerWorld,
                        NetworkId = networkId,
                        PlayerName = rpc.ValueRO.PlayerName,
                        IsLocalPlayer = 0
                    });

                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
