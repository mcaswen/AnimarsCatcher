using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器验证生成 RPC，并按连接阵营实例化拥有权正确的 Ani Ghost
    /// </summary>
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

            // 服务器是生成数量、阵营归属和实体拥有权的最终执行方
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

                // 出生点按服务器分配的阵营匹配，不能信任客户端提供阵营
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

                // 两种 Ani 共用出生变换，但使用各自的 Ghost 预制体
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

                // RPC 实体消费后立即销毁，防止下一帧重复生成
                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }

        // 实例化 Blaster 并绑定服务器确定的阵营与连接拥有权
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
            entityCommandBuffer.SetComponent(ani, new Camp { Value = camp });
            entityCommandBuffer.AddComponent(ani, new GhostOwner { NetworkId = networkId });
            entityCommandBuffer.AddComponent(ani, new BlasterAniTag());
        }

        // 实例化 Picker 并绑定服务器确定的阵营与连接拥有权
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
            entityCommandBuffer.SetComponent(ani, new Camp { Value = camp });
            entityCommandBuffer.AddComponent(ani, new GhostOwner { NetworkId = networkId });
            entityCommandBuffer.AddComponent(ani, new PickerAniTag());
        }

    }
}
