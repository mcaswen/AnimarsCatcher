using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientSendAttackHitRpcSystem : ISystem
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
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 客户端到服务器只有一条连接
        Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();

        // 从桥里把所有 AniAttackHitEvent 拿出来
        while (AniAttackEventBridge.TryDequeue(out var evt))
        {
            Entity attackerEntity = evt.Attacker;

            if (!entityManager.Exists(attackerEntity))
                continue;

            if (!entityManager.HasComponent<GhostInstance>(attackerEntity))
                continue;

            var ghost = entityManager.GetComponentData<GhostInstance>(attackerEntity);

            Entity rpcEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(rpcEntity, new AttackHitRpc
            {
                AttackerGhostId = ghost.ghostId,
                ShotId          = evt.ShotId
            });

            entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}
