using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerBaseDefeatSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GameResult>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityManager = state.EntityManager;
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var gameResultEntity = SystemAPI.GetSingletonEntity<GameResult>();
        var gameResult = entityManager.GetComponentData<GameResult>(gameResultEntity);

        // 已经结束就别再判了
        if (gameResult.IsGameOver != 0)
            return;

        // 找所有大基地（BigBaseTag）+ Health + Camp
        foreach (var (health, camp, baseEntity) in
                 SystemAPI.Query<RefRO<Health>, RefRO<Camp>>()
                     .WithAll<BigBaseTag>()
                     .WithEntityAccess())
        {
            if (health.ValueRO.current > 0f)
                continue;

            // 这座大基地被摧毁了
            CampType loser  = camp.ValueRO.Value;
            CampType winner = loser == CampType.Alpha ? CampType.Beta : CampType.Alpha;

            gameResult.IsGameOver = 1;
            gameResult.Winner = winner;
            entityCommandBuffer.SetComponent(gameResultEntity, gameResult);

            // 可选：给这个基地打个“已毁”标记，防止后续系统再处理它
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

            break; // 一座大基地爆了就够了
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}

public struct BaseDestroyedTag : IComponentData {}
