using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在客户端把 Blaster 视图射线结果转换为远程命中 RPC
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientSendRangedHitRpcSystem : ISystem
    {
        /// <summary>
        /// 等待客户端进入游戏网络流后再发送射线结果
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// 将本地实体映射为 GhostId 并发送候选命中给服务器
        /// </summary>
        /// <param name="state">系统运行状态</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 客户端世界只维护一条到服务器的游戏连接
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

                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connection
                });
            }

            ecb.Playback(entityManager);
            ecb.Dispose();
        }
    }
}
