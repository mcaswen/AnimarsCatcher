using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ServerStartGameSystem))] // 在发完 ClientStartGameRpc 之后
public partial struct ServerSetInGameRpcSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ServerMatchStartState>();
        state.RequireForUpdate<GhostCollection>();
        state.RequireForUpdate<CharacterGhostPrefab>();
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var matchStateRW = SystemAPI.GetSingletonRW<ServerMatchStartState>();
        if (matchStateRW.ValueRO.MatchStartRequested == 0)
            return;

        var entityManager = state.EntityManager;
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var characterPrefab);
        var hasCameraPrefab    = SystemAPI.TryGetSingleton<CameraGhostPrefab>(out var cameraPrefab);

        if (!hasCharacterPrefab || !hasCameraPrefab)
        {
            entityCommandBuffer.Playback(entityManager);
            return;
        }

        // （可选）再检查一下 GhostCollection 是否 ready
        Entity ghostCollectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
        DynamicBuffer<GhostCollectionPrefab> ghostPrefabs =
            SystemAPI.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);

        if (ghostPrefabs.Length == 0)
        {
            entityCommandBuffer.Playback(entityManager);
            return;
        }

        bool anySpawnedThisFrame = false;

        // 处理所有 SetInGameRpc
        foreach (var (rpc, req, rpcEntity) in SystemAPI
                     .Query<RefRO<SetInGameRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            Entity connectionEntity = req.ValueRO.SourceConnection;
            var connectionAspect    = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);
            int networkId           = connectionAspect.Id;

            CampType camp = ServerCampAssignmentPolicy.GetCampForConnection(networkId);

            if (connectionAspect.HasSpawned(ref state))
            {
                Debug.Log($"[ServerSetInGameRpcSystem] Connection {networkId} already spawned, skip.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 1. 给这个连接标记 InGame（这会加 NetworkStreamInGame）
            connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

            // 2. 找到对应阵营的出生点组，复用你原来的逻辑
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
                Debug.Log($"[ServerSetInGameRpcSystem] Spawned character for connection {networkId}, camp {camp}");
                anySpawnedThisFrame = true;
            }
            else
            {
                Debug.LogError($"[ServerSetInGameRpcSystem] Failed to spawn character for connection {networkId}");
            }

            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        if (anySpawnedThisFrame)
        {
            // 如果你仍然想用 CharactersSpawned 标记“整局开始”，可以在这里更新
            matchStateRW.ValueRW.CharactersSpawned = 1;

            NetUIEventBridge.RaiseMatchStartedEvent(NetUIEventSource.ServerWorld, localPlayerNetworkId: -1);
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }

    private bool TrySpawnCharacterForConnection(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        ServerGetConnectionAspect connectionAspect,
        Entity characterPrefab,
        CampType camp)
    {
        int id = connectionAspect.Id;

        foreach (var (spawnState, selectMode, groupCamp, points) in
                 SystemAPI.Query<
                     RefRW<CharacterSpawnPointsState>,
                     RefRO<CharacterSpawnSelectMode>,
                     RefRO<Camp>,
                     DynamicBuffer<CharacterSpawnPointElement>>())
        {
            // 若阵营不同则跳过
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
                Debug.LogError($"[ServerSetInGameRpcSystem] Failed to select spawn point for connection {id}, camp {camp}");
                return false;
            }

            var character = CharacterSpawnUtility.InstantiateAndInit(
                ref entityCommandBuffer,
                characterPrefab,
                id,
                spawnPosition,
                spawnRotation,
                camp,
                1f
            );

            // 设置 CommandTarget 和 GhostOwner
            connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
            entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });
            entityCommandBuffer.SetComponent(character, new Camp { Value = camp });

            return true;
        }

        return false;
    }
}
