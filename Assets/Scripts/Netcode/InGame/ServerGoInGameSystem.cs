using System.Threading;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerGoInGameSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<CharacterGhostPrefab>();
        state.RequireForUpdate<CharacterSpawnPointsTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasCharacterPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var prefab);
        var hasCameraPrefab = SystemAPI.TryGetSingleton<CameraGhostPrefab>(out var cameraPrefab);

        var pointsRO  = SystemAPI.GetSingletonBuffer<CharacterSpawnPointElement>(true);

        foreach (var (req, source, rpcEntity) in SystemAPI
                     .Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = source.ValueRO.SourceConnection;
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);

            UnityEngine.Debug.Log("[Server InGame] GoInGameRequest received");

            // 先标记 InGame
            connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

            // 已经生成过, 则销毁rpc
            if (connectionAspect.HasSpawned(ref state))
            {
                UnityEngine.Debug.Log("[Server InGame] CharacterGhostPrefab has been spawned in ServerWorld.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 找不到prefab也清理 RPC
            if (!hasCharacterPrefab || !hasCameraPrefab)
            {
                UnityEngine.Debug.LogWarning("[Server InGame] CharacterGhostPrefab not found in ServerWorld.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 生成角色并初始化
            var id = connectionAspect.Id;

            CharacterSpawnUtil.SelectCharacterSpwanPoint(
                id,
                SystemAPI.GetSingletonRW<CharacterSpawnPointsState>().ValueRW,
                connectionAspect,
                pointsRO,
                SystemAPI.GetSingleton<CharacterSpawnSelectMode>().Value,
                out var spawnPosition,
                out var spawnRotation
            );

            var character = CharacterSpawnUtil.InstantiateAndInit(
                ref entityCommandBuffer,
                prefab.Value,
                id,
                spawnPosition,
                spawnRotation,
                1f
            );

            // 绑定 CommandTarget、打已生成标记、清理 RPC
            connectionAspect.SetCommandTarget(character, ref state, ref entityCommandBuffer);
            entityCommandBuffer.AddComponent(character, new GhostOwner { NetworkId = id });

            var cameraEntity = entityCommandBuffer.Instantiate(cameraPrefab.Value);
            entityCommandBuffer.AddComponent(cameraEntity, new GhostOwner { NetworkId = id });

            connectionAspect.MarkSpawned(ref entityCommandBuffer);
            entityCommandBuffer.DestroyEntity(rpcEntity);

            UnityEngine.Debug.Log($"[Server InGame] Spawned character for connection {id} at {spawnPosition}");
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
