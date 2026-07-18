using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 在服务端处理水晶死亡并生成配置数量的可拾取资源
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerFragileCrystalDeathSystem : ISystem
{
    /// <summary>
    /// 仅在场景存在可破坏水晶时启用系统
    /// </summary>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 等待水晶 Transform 数据完成创建
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<FragileCrystal, LocalTransform>()
                .Build());
    }

    /// <summary>
    /// 将生命值耗尽的水晶替换为散落资源实体
    /// </summary>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (stoneRef, transformRef, health, stoneEntity) in
                 SystemAPI.Query<RefRO<FragileCrystal>, RefRO<LocalTransform>, RefRO<Health>>()
                     .WithEntityAccess())
        {
            var stone = stoneRef.ValueRO;

            // 未死亡水晶保留到后续帧继续观察
            if (health.ValueRO.current > 0)
                continue;

            // 缺少有效掉落预制体时仍需销毁死亡水晶
            if (stone.PickablePrefab == Entity.Null ||
                !SystemAPI.HasComponent<PickableResource>(stone.PickablePrefab))
            {
                entityCommandBuffer.DestroyEntity(stoneEntity);
                continue;
            }

            // 从预制体读取资源总量并分摊到各掉落实体
            PickableResource prefabPickable =
                SystemAPI.GetComponent<PickableResource>(stone.PickablePrefab);

            int pieceCount = math.max(1, stone.DropPieceCount);

            float3 origin = transformRef.ValueRO.Position;

            for (int i = 0; i < pieceCount; i++)
            {
                Entity piece = entityCommandBuffer.Instantiate(stone.PickablePrefab);

                // 在圆环上均匀分布掉落位置
                float angle = (2f * math.PI / pieceCount) * i;
                float radius = stone.DropSpawnRadius;

                float3 offset = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;

                float3 spawnPos = origin + offset;

                // 设置小矿的位置
                entityCommandBuffer.SetComponent(piece, new LocalTransform
                {
                    Position = spawnPos,
                    Rotation = transformRef.ValueRO.Rotation,
                    Scale = 6f
                });
            }

            entityCommandBuffer.DestroyEntity(stoneEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
