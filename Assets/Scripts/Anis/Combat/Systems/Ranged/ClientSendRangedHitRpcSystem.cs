using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientSendRangedHitRpcSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
    }

    // [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityManager = state.EntityManager;
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // 客户端只会有一条到服务器的连接
        Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();

        while (AniHitBridge.TryDequeue(out var hit))
        {
            Entity attackerEntity = hit.Attacker;

            if (!entityManager.Exists(attackerEntity))
                continue;

            if (!entityManager.HasComponent<GhostInstance>(attackerEntity))
                continue;

            var attackerGhost = entityManager.GetComponentData<GhostInstance>(attackerEntity);
            int attackerGhostId = attackerGhost.ghostId;

            int targetGhostId = -1;
            if (hit.HitTarget != Entity.Null &&
                entityManager.Exists(hit.HitTarget) &&
                entityManager.HasComponent<GhostInstance>(hit.HitTarget))
            {
                var targetGhost = entityManager.GetComponentData<GhostInstance>(hit.HitTarget);
                targetGhostId   = targetGhost.ghostId;
            }

            Entity rpcEntity = ecb.CreateEntity();
            ecb.AddComponent(rpcEntity, new RangedHitRpc
            {
                AttackerGhostId = attackerGhostId,
                TargetGhostId   = targetGhostId,
                HitPosition     = hit.HitPosition,
                HitNormal       = hit.HitNormal,
                ShotId          = hit.ShotId
            });

            // UnityEngine.Debug.Log($"ClientSendRangedHitRpcSystem: Enqueue RangedHitRpc: AttackerGhostId={attackerGhostId}, TargetGhostId={targetGhostId}, ShotId={hit.ShotId}");

            ecb.AddComponent(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });
        }

        ecb.Playback(entityManager);
        ecb.Dispose();
    }
}
