using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[BurstCompile]
public partial struct ServerBaseSpawnSystem : ISystem
{
    private bool _resultIsInitialized;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 没有 BaseSpawnPoint 就不用跑
        state.RequireForUpdate<BaseSpawnPoint>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer entityCommandBuffer =
            new EntityCommandBuffer(Allocator.Temp);

        foreach (var (spawnPoint, spawnTransform, spawnEntity) in
                 SystemAPI
                     .Query<RefRW<BaseSpawnPoint>, RefRO<LocalTransform>>()
                     .WithEntityAccess())
        {
            if (spawnPoint.ValueRO.HasSpawned != 0)
            {
                continue;
            }

            // 实例化基地
            Entity baseEntity =
                entityCommandBuffer.Instantiate(spawnPoint.ValueRO.BasePrefab);

            entityCommandBuffer.SetComponent(
                baseEntity,
                new Camp { Value = spawnPoint.ValueRO.CampKind });

            entityCommandBuffer.SetComponent(
                baseEntity,
                new Health 
                {
                    max = spawnPoint.ValueRO.Health,
                    current = spawnPoint.ValueRO.Health
                });

            // 把基地丢到刷新点的位置和朝向
            entityCommandBuffer.SetComponent(baseEntity, spawnTransform.ValueRO);

            // 标记这个刷新点已经刷过
            spawnPoint.ValueRW.HasSpawned = 1;
        }

        if (!_resultIsInitialized)
        {
            _resultIsInitialized = true;

            // 初始化 GameResult 实体
            var gameResultEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(gameResultEntity, new GameResult { IsGameOver = 0, Winner = CampType.Neutral });
            // entityCommandBuffer.AddComponent(GhostAuthoring, gameResultEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
