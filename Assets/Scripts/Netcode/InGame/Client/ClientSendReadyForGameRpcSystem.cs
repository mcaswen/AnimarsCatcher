namespace AnimarsCatcher.Networking
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.NetCode;
    using UnityEngine;

    /// <summary>
    /// 等待客户端 Ghost 资源稳定后完成正式 InGame 握手
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientSendReadyForGameRpcSystem : ISystem
    {
        private bool   _hasSent;          // 是否已经发送 ClientReadyForGameRpc
        private bool   _sceneReadyOnce;   // Ghost 资源是否至少完整过一次
        private double _sceneReadyTime;   // 首次资源完整的时间戳


        public void OnCreate(ref SystemState state)
        {
            // 三项依赖同时存在才能进入正式游戏握手
            state.RequireForUpdate<NetworkId>();
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<ClientMatchStartState>();

            _hasSent         = false;
            _sceneReadyOnce  = false;
            _sceneReadyTime  = 0.0;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_hasSent)
                return;

            var entityManager = state.EntityManager;
            double elapsed    = SystemAPI.Time.ElapsedTime;

            // GhostCollection 包含关键 Prefab 表示游戏 SubScene 已完成加载
            Entity ghostCollectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            DynamicBuffer<GhostCollectionPrefab> prefabs =
                SystemAPI.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);

            bool readyNow = IsClientSceneReady(ref state, prefabs);

            if (!readyNow)
            {
                // 稳定等待期间资源再次失效时重新计时
                _sceneReadyOnce = false;
                return;
            }

            // 首次完整只记录时间，避免资源刚注册时立即开始预测
            if (!_sceneReadyOnce)
            {
                _sceneReadyOnce = true;
                _sceneReadyTime = elapsed;
                Debug.Log("[ClientSendReadyForGameRpcSystem] Scene marked ready, waiting extra 2s before entering InGame.");
                return;
            }

            // 额外等待窗口用于覆盖 Ghost 集合和 SubScene 状态传播延迟
            const double extraDelaySeconds = 3.0;
            if (elapsed - _sceneReadyTime < extraDelaySeconds)
                return;

            // 后续 RPC 和本地 InGame 标记必须指向当前连接实体
            if (!SystemAPI.TryGetSingletonEntity<NetworkId>(out var connectionEntity))
                return;

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 服务器收到 ClientReadyForGameRpc 后才有权创建角色并设置 CommandTarget
            Entity rpcEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(rpcEntity, new ClientReadyForGameRpc());
            entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connectionEntity
            });

            // 本地同步进入 InGame，使输入和预测系统开始运行
            if (!entityManager.HasComponent<NetworkStreamInGame>(connectionEntity))
            {
                entityCommandBuffer.AddComponent<NetworkStreamInGame>(connectionEntity);
                Debug.Log("[ClientSendReadyForGameRpcSystem] Extra 2s passed, mark InGame and send ClientReadyForGameRpc.");
            }

            // 此时客户端已具备运行对局的全部网络状态 可通知表现层退出等待界面
            Entity notificationEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(
                notificationEntity,
                new MatchStartedNotification
                {
                    Source = NetworkNotificationSource.ClientWorld,
                    LocalPlayerNetworkId = SystemAPI.GetSingleton<NetworkId>().Value
                });

            entityCommandBuffer.Playback(entityManager);
            entityCommandBuffer.Dispose();

            _hasSent = true;
        }

        private bool IsClientSceneReady(ref SystemState state, DynamicBuffer<GhostCollectionPrefab> prefabs)
        {
            var entityManager = state.EntityManager;

            if (prefabs.Length == 0)
            {
                return false;
            }

            bool hasCharacter = false;

            for (int i = 0; i < prefabs.Length; i++)
            {
                var entry        = prefabs[i];
                var prefabEntity = entry.GhostPrefab;

                if (prefabEntity == Entity.Null || !entityManager.Exists(prefabEntity))
                {
                    Debug.LogError($"[ClientSendReadyForGameRpcSystem] Prefab[{i}] is invalid: {prefabEntity}, Exists={entityManager.Exists(prefabEntity)}");
                    // 任一注册项无效都说明 Ghost 集合仍在变动
                    return false;
                }

                string name = entityManager.GetName(prefabEntity);
                if (!hasCharacter && !string.IsNullOrEmpty(name) && name.Contains("Robot"))
                    hasCharacter = true;
            }

            if (!hasCharacter)
            {
                Debug.Log("[ClientSendReadyForGameRpcSystem] GhostCollection ready but no 'Robot' prefab found yet.");
                return false;
            }

            return hasCharacter;
        }
    }
}
