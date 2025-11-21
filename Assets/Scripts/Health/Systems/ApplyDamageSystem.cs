using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ApplyDamageSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<Health, DamageEvent>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (health, damageBuffer, entity) in
                 SystemAPI.Query<RefRW<Health>, DynamicBuffer<DamageEvent>>()
                          .WithEntityAccess())
        {
            int totalDamage = 0;

            for (int i = 0; i < damageBuffer.Length; i++)
            {
                totalDamage += damageBuffer[i].amount;
            }

            damageBuffer.Clear();

            if (totalDamage == 0)
            {
                continue;
            }

            Health h = health.ValueRW;
            h.current = math.max(0, h.current - totalDamage);
            health.ValueRW = h;

            if (h.current <= 0)
            {
                // 这里可以做死亡逻辑：播放特效 / 掉落资源 / 通知别的系统
                entityCommandBuffer.DestroyEntity(entity);
            }
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
