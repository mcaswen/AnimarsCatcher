using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerSpawnAnisSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AniGhostPrefabCollection>();
        state.RequireForUpdate<AniSpawnPointTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        var hasPrefab = SystemAPI.TryGetSingleton<AniGhostPrefabCollection>(out var aniGhostPrefabCollection);
        if (!hasPrefab)
        {
            entityCommandBuffer.Playback(state.EntityManager);
            return;
        }

        // 处理所有 SpawnBlasterAniRpc
        foreach (var (rpc, req, rpcEntity) in SystemAPI
                     .Query<RefRO<SpawnAniRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            var connectionEntity = req.ValueRO.SourceConnection;

            if (!SystemAPI.HasComponent<NetworkId>(connectionEntity))
            {
                Debug.LogWarning("[ServerSpawnBlasterAniSystem] SourceConnection has no NetworkId.");
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            var networkId = SystemAPI.GetComponent<NetworkId>(connectionEntity).Value;

            var camp = ServerCampAssignmentPolicy.GetCampForConnection(networkId);

            // 找到该阵营的 AniSpawnPoint
            bool foundSpawnPoint = false;
            float3 spawnPosition = default;
            quaternion spawnRotation = quaternion.identity;

            foreach (var (spawnCamp, transform) in SystemAPI
                         .Query<RefRO<Camp>, RefRO<LocalTransform>>()
                         .WithAll<AniSpawnPointTag>())
            {
                if (spawnCamp.ValueRO.Value != camp)
                    continue;

                spawnPosition = transform.ValueRO.Position;
                spawnRotation = transform.ValueRO.Rotation;
                foundSpawnPoint = true;
                break; 
            }

            if (!foundSpawnPoint)
            {
                Debug.LogWarning($"[ServerSpawnBlasterAniSystem] No AniSpawnPoint for camp={camp}, fallback to (0,0,0).");
                spawnPosition = float3.zero;
                spawnRotation = quaternion.identity;
            }
            
            // 生成指定数量的 Ani
            for (int i = 0; i < rpc.ValueRO.BlasterAniSpawnCount; i++)
            {
                SpawnBlasterAniForConnection(
                    entityCommandBuffer,
                    aniGhostPrefabCollection.BlasterAniPrefabEntity,
                    spawnPosition,
                    spawnRotation,
                    camp,
                    networkId);
            }

            for (int i = 0; i < rpc.ValueRO.PickerAniSpawnCount; i++)
            {
                SpawnPickerAniForConnection(
                    entityCommandBuffer,
                    aniGhostPrefabCollection.PickerAniPrefabEntity,
                    spawnPosition,
                    spawnRotation,
                    camp,
                    networkId);
            }

            // 清理 RPC Entity
            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }

    private void SpawnBlasterAniForConnection(
        EntityCommandBuffer entityCommandBuffer,
        Entity blasterAniPrefab,
        float3 spawnPosition,
        quaternion spawnRotation,
        CampType camp, 
        int networkId)
    {
        var ani = entityCommandBuffer.Instantiate(blasterAniPrefab);

        entityCommandBuffer.SetComponent(ani, LocalTransform.FromPositionRotation(spawnPosition, spawnRotation));
        entityCommandBuffer.AddComponent(ani, new Camp { Value = camp });
        entityCommandBuffer.AddComponent(ani, new GhostOwner { NetworkId = networkId });
        entityCommandBuffer.AddComponent(ani, new BlasterAniTag());
    }

    private void SpawnPickerAniForConnection(
        EntityCommandBuffer entityCommandBuffer,
        Entity pickerAniPrefab,
        float3 spawnPosition,
        quaternion spawnRotation,
        CampType camp, 
        int networkId)
    {
        var ani = entityCommandBuffer.Instantiate(pickerAniPrefab);

        entityCommandBuffer.SetComponent(ani, LocalTransform.FromPositionRotation(spawnPosition, spawnRotation));
        entityCommandBuffer.AddComponent(ani, new Camp { Value = camp });
        entityCommandBuffer.AddComponent(ani, new GhostOwner { NetworkId = networkId });
        entityCommandBuffer.AddComponent(ani, new PickerAniTag());
    }

}
