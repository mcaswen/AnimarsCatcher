using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

// Editor 调试模式专用
// 只在 UNITY_EDITOR 下生效
// 只在当前场景为 "GameLevel" 时工作
// 收到 GoInGameRequest 后：标记 InGame + 为该连接 Spawn 角色与相机
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerGoInGameDebugSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        state.RequireForUpdate<CharacterGhostPrefab>();
        state.RequireForUpdate<CharacterSpawnPointsTag>();
#else
        state.Enabled = false;
#endif
    }

    public void OnUpdate(ref SystemState state)
    {
        Debug.Log("[ServerGoInGameDebug] OnUpdate called.");

#if !UNITY_EDITOR
        return;
#else
        if (SceneManager.GetActiveScene().name != "GameLevel")
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var characterPrefab);
        var hasCameraPrefab    = SystemAPI.TryGetSingleton<CameraGhostPrefab>(out var cameraPrefab);

        if (!hasCharacterPrefab || !hasCameraPrefab)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        var spawnPoints = SystemAPI.GetSingletonBuffer<CharacterSpawnPointElement>(true);
        var spawnPointsState = SystemAPI.GetSingletonRW<CharacterSpawnPointsState>().ValueRW;
        var selectMode = SystemAPI.GetSingleton<CharacterSpawnSelectMode>().Value;

        // 处理 GoInGameRequest
        foreach (var (request, src, rpcEntity) in SystemAPI
                     .Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = src.ValueRO.SourceConnection;
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);

            Debug.Log("[ServerGoInGameDebug] GoInGameRequest received.");

            // 标记 InGame
            connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

            if (connectionAspect.HasSpawned(ref state))
            {
                Debug.Log("[ServerGoInGameDebug] Character already spawned for this connection, skip.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            var id = connectionAspect.Id;

            // 选取出生点
            CharacterSpawnUtil.SelectCharacterSpwanPoint(
                id,
                spawnPointsState,
                connectionAspect,
                spawnPoints,
                selectMode,
                out var spawnPosition,
                out var spawnRotation
            );

            // 实例化角色
            var character = CharacterSpawnUtil.InstantiateAndInit(
                ref entityCommandBuffer,
                characterPrefab.Value,
                id,
                spawnPosition,
                spawnRotation,
                1f
            );

            // 设置 CommandTarget / GhostOwner
            connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
            entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });

            var cameraEntity = entityCommandBuffer.Instantiate(cameraPrefab.Value);
            entityCommandBuffer.AddComponent(cameraEntity, new GhostOwner { NetworkId = id });

            connectionAspect.MarkSpawned(ref entityCommandBuffer);

            entityCommandBuffer.DestroyEntity(rpcEntity);

            Debug.Log($"[ServerGoInGameDebug] Spawned character for connection {id} at {spawnPosition}");
        }

        entityCommandBuffer.Playback(state.EntityManager);
#endif
    }
}
