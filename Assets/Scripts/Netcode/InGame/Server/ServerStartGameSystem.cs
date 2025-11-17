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
        var spawnPoints = SystemAPI.GetSingletonBuffer<CharacterSpawnPointElement>(true);
        var spawnPointsState = SystemAPI.GetSingletonRW<CharacterSpawnPointsState>().ValueRW;
        var selectMode = SystemAPI.GetSingleton<CharacterSpawnSelectMode>().Value;

        foreach (var (networkId, connectionEntity) in SystemAPI
                     .Query<RefRO<NetworkId>>()
                     .WithEntityAccess())
        {
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);

            // 标记 InGame
            connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

            if (connectionAspect.HasSpawned(ref state))
            {
                UnityEngine.Debug.Log($"[Server] Connection {networkId.ValueRO.Value} already spawned, skip.");
                continue;
            }

            // 选择出生点并实例化角色
            var id = connectionAspect.Id;

            CharacterSpawnUtil.SelectCharacterSpwanPoint(
                id,
                spawnPointsState,
                connectionAspect,
                spawnPoints,
                selectMode,
                out var spawnPosition,
                out var spawnRotation
            );

            var character = CharacterSpawnUtil.InstantiateAndInit(
                ref entityCommandBuffer,
                characterPrefab.Value,
                id,
                spawnPosition,
                spawnRotation,
                1f
            );

            // 设置 CommandTarget 和 GhostOwner
            connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
            entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });

            var cameraEntity = entityCommandBuffer.Instantiate(cameraPrefab.Value);
            entityCommandBuffer.AddComponent(cameraEntity, new GhostOwner { NetworkId = id });

            connectionAspect.MarkSpawned(ref entityCommandBuffer);

            UnityEngine.Debug.Log($"[Server] Spawned character for connection {id} at {spawnPosition}");
        }

        matchStateRW.ValueRW.CharactersSpawned = 1;

        // 通知 UI：游戏开始
        NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ServerWorld, localPlayerNetworkId: -1);

        entityCommandBuffer.Playback(state.EntityManager);
    }

}
