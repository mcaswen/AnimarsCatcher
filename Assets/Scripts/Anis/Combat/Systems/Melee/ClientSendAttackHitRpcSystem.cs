using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在客户端把 Picker 动画命中事件转换为近战攻击 RPC
    /// </summary>
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

        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 客户端世界只维护一条到服务器的游戏连接
            Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();

            // 一帧内可能积累多个动画事件，需要完整排空队列
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
}
