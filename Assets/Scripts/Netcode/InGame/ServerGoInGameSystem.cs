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
        state.RequireForUpdate<PlayerSpawnPointsTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasPrefab = SystemAPI.TryGetSingleton<CharacterGhostPrefab>(out var prefab);
        var pointsRO  = SystemAPI.GetSingletonBuffer<PlayerSpawnPointElement>(true);

        foreach (var (req, source, rpcEntity) in SystemAPI
                     .Query<RefRO<GoInGameRequest>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = source.ValueRO.SourceConnection;
            var connectionAspect = SystemAPI.GetAspect<ServerGetConnectionAspect>(connectionEntity);

            UnityEngine.Debug.Log("[Server] GoInGameRequest received");

            // 先标记 InGame
            connectionAspect.EnsureInGame(ref state, ref entityCommandBuffer);

            // 已经生成过, 则销毁rpc
            if (connectionAspect.HasSpawned(ref state))
            {
                UnityEngine.Debug.LogWarning("[Server] CharacterGhostPrefab has been spawned in ServerWorld.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 找不到prefab也清理 RPC
            if (!hasPrefab)
            {
                UnityEngine.Debug.LogWarning("[Server] CharacterGhostPrefab not found in ServerWorld.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 生成角色并初始化
            var id = connectionAspect.Id;

            CharacterSpawnUtil.SelectCharacterSpwanPoint(
                id,
                SystemAPI.GetSingletonRW<PlayerSpawnPointsState>().ValueRW,
                connectionAspect,
                pointsRO,
                SystemAPI.GetSingleton<PlayerSpawnSelectMode>().Value,
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
            connectionAspect.MarkSpawned(ref entityCommandBuffer);
            entityCommandBuffer.DestroyEntity(rpcEntity);

            UnityEngine.Debug.LogWarning($"[Server] Spawned character for connection {id} at {spawnPosition}");
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
