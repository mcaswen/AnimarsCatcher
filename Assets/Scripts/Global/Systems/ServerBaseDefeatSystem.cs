using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器检测大基地死亡并向所有连接广播唯一的对局结果
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerBaseDefeatSystem : ISystem
    {
        /// <summary>
        /// 等待服务器存在对局结果实体后再进行胜负判断
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameResult>();
        }

        /// <summary>
        /// 首次发现大基地生命值耗尽时锁定结果并广播 RPC
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var gameResultEntity = SystemAPI.GetSingletonEntity<GameResult>();
            var gameResult = entityManager.GetComponentData<GameResult>(gameResultEntity);

            // 对局结果一旦锁定就不允许后续帧覆盖
            if (gameResult.IsGameOver != 0)
                return;

            // 只有大基地会触发全局胜负，小基地不参与此流程
            foreach (var (health, camp, baseEntity) in
                     SystemAPI.Query<RefRO<Health>, RefRO<Camp>>()
                         .WithAll<BigBaseTag>()
                         .WithEntityAccess())
            {
                if (health.ValueRO.current > 0f)
                    continue;

                // 被摧毁基地的对立阵营成为胜方
                CampType loser  = camp.ValueRO.Value;
                CampType winner = loser == CampType.Alpha ? CampType.Beta : CampType.Alpha;

                gameResult.IsGameOver = 1;
                gameResult.Winner = winner;
                entityCommandBuffer.SetComponent(gameResultEntity, gameResult);

                // 标记已毁基地，阻止其他系统重复处理同一生命周期
                if (!SystemAPI.HasComponent<BaseDestroyedTag>(baseEntity))
                {
                    entityCommandBuffer.AddComponent<BaseDestroyedTag>(baseEntity);
                }

                UnityEngine.Debug.LogWarning($"[ServerBaseDefeatSystem] Big base of {loser} destroyed, winner = {winner}");

                foreach (var (streamInGame, connectionEntity) in
                        SystemAPI.Query<RefRO<NetworkStreamInGame>>()
                        .WithEntityAccess())
                {
                    var rpcEntity = entityCommandBuffer.CreateEntity();
                    entityCommandBuffer.AddComponent(rpcEntity, new GameOverRpc
                    {
                        Winner = winner
                    });
                    entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
                    {
                        TargetConnection = connectionEntity
                    });
                }

                break; // 首个被摧毁的大基地已经确定唯一结果
            }

            entityCommandBuffer.Playback(entityManager);
            entityCommandBuffer.Dispose();
        }
    }

    /// <summary>
    /// 标记已经触发过胜负结算的基地实体
    /// </summary>
    public struct BaseDestroyedTag : IComponentData {}
}
