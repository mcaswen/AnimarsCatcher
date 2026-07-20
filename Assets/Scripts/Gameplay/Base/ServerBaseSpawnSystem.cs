using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 仅在服务器实例化基地并创建唯一的对局结果实体
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [BurstCompile]
    public partial struct ServerBaseSpawnSystem : ISystem
    {
        private bool _resultIsInitialized;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            // 场景没有基地刷新点时无需参与更新
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

                // 基地必须由服务器实例化以保持网络状态权威
                Entity baseEntity =
                    entityCommandBuffer.Instantiate(spawnPoint.ValueRO.BasePrefab);

                entityCommandBuffer.SetComponent(
                    baseEntity,
                    new Camp { Value = spawnPoint.ValueRO.CampKind });

                entityCommandBuffer.SetComponent(
                    baseEntity,
                    new Health
                    {
                        Maximum = spawnPoint.ValueRO.Health,
                        Current = spawnPoint.ValueRO.Health
                    });

                // 使用刷新点变换覆盖预制体默认变换
                entityCommandBuffer.SetComponent(baseEntity, spawnTransform.ValueRO);

                // 标记刷新点以保证整个生命周期只生成一次
                spawnPoint.ValueRW.HasSpawned = 1;
            }

            if (!_resultIsInitialized)
            {
                _resultIsInitialized = true;

                // 对局结果由服务器维护且整个世界只创建一次
                var gameResultEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(gameResultEntity, new GameResult { IsGameOver = 0, Winner = CampType.Neutral });
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
