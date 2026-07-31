namespace AnimarsCatcher.Networking
{
    using Unity.Collections;
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 在 Client World 接收开局通知并切换到服务器指定场景
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientHandleStartMatchNotificationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkId>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            bool hasStart = false;
            FixedString64Bytes sceneName = default;

            foreach (var (rpc, req, entity) in SystemAPI
                     .Query<RefRO<StartMatchNotificationRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
            {
                hasStart  = true;
                sceneName = rpc.ValueRO.SceneName;

                entityCommandBuffer.DestroyEntity(entity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();

            if (!hasStart)
                return;

            string sceneNameString = sceneName.ToString();
            Debug.Log($"[ClientHandleStartMatchNotificationSystem] Received StartMatchNotificationRpc for scene '{sceneNameString}'");

            // 本地状态用于阻止场景就绪系统在开局通知前运行
            var matchStateEntity = state.EntityManager.CreateEntity(typeof(ClientMatchStartState));
            state.EntityManager.SetComponentData(matchStateEntity, new ClientMatchStartState { Active = 1 });

            int localNetworkId = SystemAPI.GetSingleton<NetworkId>().Value;
            Entity notificationEntity = state.EntityManager.CreateEntity(typeof(MatchStartedNotification));
            state.EntityManager.SetComponentData(
                notificationEntity,
                new MatchStartedNotification
                {
                    Source = NetworkNotificationSource.ClientWorld,
                    LocalPlayerNetworkId = localNetworkId
                });

                // 网络层只发布权威场景请求，具体加载表现由上层桥接负责
            Entity sceneLoadEntity = state.EntityManager.CreateEntity(typeof(ClientSceneLoadRequest));
            state.EntityManager.SetComponentData(
                sceneLoadEntity,
                new ClientSceneLoadRequest { SceneName = sceneName });
        }
    }
}
