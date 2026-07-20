namespace AnimarsCatcher.Networking
{
    using AnimarsCatcher.Gameplay.Contracts;
    using AnimarsCatcher.Gameplay;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Mathematics;
    using UnityEngine;

    /// <summary>
    /// 在 Server World 响应客户端就绪请求并权威创建角色
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerStartMatchSystem))] // 在发完 StartMatchNotificationRpc 之后
    public partial struct ServerHandleReadyForGameRpcSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ServerMatchStartState>();
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<CharacterGhostPrefabReference>();
            state.RequireForUpdate<NetworkId>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var matchStateRW = SystemAPI.GetSingletonRW<ServerMatchStartState>();
            if (matchStateRW.ValueRO.MatchStartRequested == 0)
                return;

            var entityManager = state.EntityManager;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefabReference>(out var characterPrefab);
            var hasCameraPrefab    = SystemAPI.TryGetSingleton<CameraGhostPrefabReference>(out var cameraPrefab);

            if (!hasCharacterPrefab || !hasCameraPrefab)
            {
                entityCommandBuffer.Playback(entityManager);
                return;
            }

            // GhostCollection 为空时不能安全实例化并复制 Ghost Prefab
            Entity ghostCollectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            DynamicBuffer<GhostCollectionPrefab> ghostPrefabs =
                SystemAPI.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);

            if (ghostPrefabs.Length == 0)
            {
                entityCommandBuffer.Playback(entityManager);
                return;
            }

            bool anySpawnedThisFrame = false;

            // 每条 RPC 的 SourceConnection 是服务器授予所有权的唯一依据
            foreach (var (rpc, req, rpcEntity) in SystemAPI
                         .Query<RefRO<ClientReadyForGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                Entity connectionEntity = req.ValueRO.SourceConnection;
                var connectionAspect    = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);
                int networkId           = connectionAspect.Id;

                CampType camp = ServerCampAssignmentPolicy.GetCampForConnection(networkId);

                if (connectionAspect.HasSpawned(ref state))
                {
                    Debug.Log($"[ServerHandleReadyForGameRpcSystem] Connection {networkId} already spawned, skip.");
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 连接进入 InGame 后才会参与 Ghost 快照和输入命令传输
                connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

                // 阵营和出生点均由服务器策略决定，客户端请求不携带权威结果
                bool spawned = TrySpawnCharacterForConnection(
                    ref state,
                    ref entityCommandBuffer,
                    connectionAspect,
                    characterPrefab.Value,
                    camp
                );

                if (spawned)
                {
                    connectionAspect.MarkSpawned(ref entityCommandBuffer);
                    Debug.Log($"[ServerHandleReadyForGameRpcSystem] Spawned character for connection {networkId}, camp {camp}");
                    anySpawnedThisFrame = true;
                }
                else
                {
                    Debug.LogError($"[ServerHandleReadyForGameRpcSystem] Failed to spawn character for connection {networkId}");
                }

                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            if (anySpawnedThisFrame)
            {
                // 至少一个角色创建成功后记录服务器已进入角色阶段
                matchStateRW.ValueRW.CharactersSpawned = 1;

                Entity notificationEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(
                    notificationEntity,
                    new MatchStartedNotification
                    {
                        Source = NetworkNotificationSource.ServerWorld,
                        LocalPlayerNetworkId = -1
                    });
            }

            entityCommandBuffer.Playback(entityManager);
            entityCommandBuffer.Dispose();
        }

        // 在连接所属阵营的出生点组中创建角色并建立权威状态
        private bool TrySpawnCharacterForConnection(
            ref SystemState state,
            ref EntityCommandBuffer entityCommandBuffer,
            ServerGetConnectionAspect connectionAspect,
            Entity characterPrefab,
            CampType camp)
        {
            int id = connectionAspect.Id;

            // 出生点配置按阵营分组，服务器只使用与连接阵营匹配的一组
            foreach (var (spawnState, selectMode, groupCamp, points) in
                     SystemAPI.Query<
                         RefRW<CharacterSpawnPointsState>,
                         RefRO<CharacterSpawnSelectionConfig>,
                         RefRO<Camp>,
                         DynamicBuffer<CharacterSpawnPointElement>>())
            {
                // 连接只能使用服务器分配阵营对应的出生点组
                if (groupCamp.ValueRO.Value != camp)
                    continue;

                bool spawnPointSelected = CharacterSpawnUtility.TrySelectCharacterSpawnPoint(
                    spawnState.ValueRW,
                    connectionAspect,
                    points,
                    selectMode.ValueRO.Value,
                    out float3 spawnPosition,
                    out quaternion spawnRotation
                );

                if (!spawnPointSelected)
                {
                    Debug.LogError($"[ServerHandleReadyForGameRpcSystem] Failed to select spawn point for connection {id}, camp {camp}");
                    return false;
                }

                var character = CharacterSpawnUtility.InstantiateAndInitialize(
                    ref entityCommandBuffer,
                    characterPrefab,
                    id,
                    spawnPosition,
                    spawnRotation,
                    camp,
                    1f
                );

                // CommandTarget 路由输入，GhostOwner 授予对应客户端预测权限
                connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
                entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });
                entityCommandBuffer.SetComponent(character, new Camp { Value = camp });

                return true;
            }

            return false;
        }
    }
}
