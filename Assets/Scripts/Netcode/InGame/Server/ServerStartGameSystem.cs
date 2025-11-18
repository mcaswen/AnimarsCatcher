using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using AnimarsCatcher.Mono.Global;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerStartGameSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // 只保证有自己的状态 singleton
        if (!state.EntityManager.CreateEntityQuery(typeof(ServerMatchStartState)).IsEmpty)
        {
            state.RequireForUpdate<ServerMatchStartState>();
            return;
        }

        var stateEntity = state.EntityManager.CreateEntity(typeof(ServerMatchStartState));
        state.EntityManager.SetComponentData(stateEntity, new ServerMatchStartState
        {
            SceneName           = default,
            MatchStartRequested = 0,
            ClientStartRpcSent  = 0,
            CharactersSpawned   = 0
        });

        state.RequireForUpdate<ServerMatchStartState>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var matchStateRW = SystemAPI.GetSingletonRW<ServerMatchStartState>();

        bool hasStartRequestInThisFrame = false;
        FixedString64Bytes sceneNameFromRpc = default;

        // 处理 StartGameRpc
        foreach (var (startGameRpc, source, rpcEntity) in SystemAPI
                     .Query<RefRO<StartGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            hasStartRequestInThisFrame = true;
            sceneNameFromRpc = startGameRpc.ValueRO.SceneName;

            // 这里可以校验 source.ValueRO.SourceConnection 是不是 host
            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        if (hasStartRequestInThisFrame)
        {
            matchStateRW.ValueRW.SceneName = sceneNameFromRpc;
            matchStateRW.ValueRW.MatchStartRequested = 1;
            matchStateRW.ValueRW.ClientStartRpcSent  = 0;
            matchStateRW.ValueRW.CharactersSpawned   = 0;

            UnityEngine.Debug.Log($"[Server] Match start requested, scene = '{sceneNameFromRpc.ToString()}'.");
        }

        // 如果还没人请求开始，直接退出
        if (matchStateRW.ValueRO.MatchStartRequested == 0)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        var sceneName = matchStateRW.ValueRO.SceneName;

        // 处理 ClientStartRpcSent
        if (matchStateRW.ValueRO.ClientStartRpcSent == 0)
        {
            foreach (var (networkId, connectionEntity) in SystemAPI
                         .Query<RefRO<NetworkId>>()
                         .WithEntityAccess())
            {
                var rpcEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(rpcEntity, new ClientStartGameRpc
                {
                    SceneName = sceneName
                });
                entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connectionEntity
                });

                UnityEngine.Debug.Log($"[Server] Send ClientStartGameRpc to connection {networkId.ValueRO.Value}.");
            }

            matchStateRW.ValueRW.ClientStartRpcSent = 1;
        }

        // 处理 CharactersSpawned
        // 已经 Spawn 过了，不用重复
        if (matchStateRW.ValueRO.CharactersSpawned == 1)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var characterPrefab);
        var hasCameraPrefab = SystemAPI.TryGetSingleton<CameraGhostPrefab>(out var cameraPrefab);
        var hasSpawnTag  = SystemAPI.HasSingleton<CharacterSpawnPointsTag>();
        

        if (!hasCharacterPrefab || !hasCameraPrefab || !hasSpawnTag)
        {
            // 场景未加载完毕，等待下一帧继续加载
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        // 真正开始 Spawn
        foreach (var (networkId, connectionEntity) in SystemAPI
                     .Query<RefRO<NetworkId>>()
                     .WithEntityAccess())
        {
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);
            var id = connectionAspect.Id;

            CampType camp = ServerCampAssignmentPolicy.GetCampForConnection(id);
            
            foreach (var (spawnState, selectMode, groupCamp, points) in
                SystemAPI.Query<RefRW<CharacterSpawnPointsState>,
                                RefRO<CharacterSpawnSelectMode>,
                                RefRO<Camp>,
                                DynamicBuffer<CharacterSpawnPointElement>>())
            {
                // 若阵营不同则跳过
                if (groupCamp.ValueRO.Value != camp)
                    continue;

                // 标记 InGame
                connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

                if (connectionAspect.HasSpawned(ref state))
                {
                    UnityEngine.Debug.Log($"[Server] Connection {networkId.ValueRO.Value} already spawned, skip.");
                    continue;
                }

                // 选择出生点并实例化角色
                bool spawnPointSelected = CharacterSpawnUtil.TrySelectCharacterSpawnPoint(
                    spawnState.ValueRW,
                    connectionAspect,
                    points,
                    selectMode.ValueRO.Value,
                    out var spawnPosition,
                    out var spawnRotation
                );

                if (!spawnPointSelected)
                {
                    UnityEngine.Debug.LogError($"[Server] Failed to select spawn point for connection {id}, skip spawning.");
                    continue;
                }

                var character = CharacterSpawnUtil.InstantiateAndInit(
                    ref entityCommandBuffer,
                    characterPrefab.Value,
                    id,
                    spawnPosition,
                    spawnRotation,
                    camp,
                    1f
                );
                
                // 设置 CommandTarget 和 GhostOwner
                connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
                entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });
                entityCommandBuffer.AddComponent(character, new Camp { Value = camp });

                var cameraEntity = entityCommandBuffer.Instantiate(cameraPrefab.Value);
                entityCommandBuffer.AddComponent(cameraEntity, new GhostOwner { NetworkId = id });

                connectionAspect.MarkSpawned(ref entityCommandBuffer);

                UnityEngine.Debug.Log($"[Server] Spawned character for connection {id}, camp {camp} at {spawnPosition}");
            }
        }

        matchStateRW.ValueRW.CharactersSpawned = 1;

        // 通知 UI：游戏开始
        NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ServerWorld, localPlayerNetworkId: -1);

        entityCommandBuffer.Playback(state.EntityManager);
    }

}
