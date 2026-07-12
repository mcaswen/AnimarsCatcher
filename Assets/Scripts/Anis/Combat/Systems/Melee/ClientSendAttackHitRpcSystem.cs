using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在客户端把 Picker 动画命中事件转换为近战攻击 RPC
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientSendAttackHitRpcSystem : ISystem
{
    /// <summary>
    /// 等待客户端进入游戏网络流后再发送攻击事件
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
    }

    /// <summary>
    /// 将桥接队列中的攻击者实体映射为 GhostId 并发送给服务器
    /// </summary>
    /// <param name="state">系统运行状态</param>
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
