using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerFragileCrystalDeathSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 仅当场景中存在 BreakableStone 时才更新
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<FragileCrystal, LocalTransform>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (stoneRef, transformRef, health, stoneEntity) in
                 SystemAPI.Query<RefRO<FragileCrystal>, RefRO<LocalTransform>, RefRO<Health>>()
                     .WithEntityAccess())
        {
            var stone = stoneRef.ValueRO;

            // 还没被打碎
            if (health.ValueRO.current > 0)
                continue;

            // 没配置掉落 prefab，就直接删掉石头
            if (stone.PickablePrefab == Entity.Null ||
                !SystemAPI.HasComponent<PickableResource>(stone.PickablePrefab))
            {
                ecb.DestroyEntity(stoneEntity);
                continue;
            }

            // 从 prefab 上拿一个 PickableResource 作为模板
            PickableResource prefabPickable =
                SystemAPI.GetComponent<PickableResource>(stone.PickablePrefab);

            int pieceCount = math.max(1, stone.DropPieceCount);

            float3 origin = transformRef.ValueRO.Position;

            for (int i = 0; i < pieceCount; i++)
            {
                Entity piece = ecb.Instantiate(stone.PickablePrefab);

                // 在圆环上均匀分布掉落位置
                float angle = (2f * math.PI / pieceCount) * i;
                float radius = stone.DropSpawnRadius;

                float3 offset = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;

                float3 spawnPos = origin + offset;

                // 设置小矿的位置
                ecb.SetComponent(piece, new LocalTransform
                {
                    Position = spawnPos,
                    Rotation = transformRef.ValueRO.Rotation,
                    Scale = 6f
                });
            }

            ecb.DestroyEntity(stoneEntity);
        }

        ecb.Playback(state.EntityManager);
    }
}
