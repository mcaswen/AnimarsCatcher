using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

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
        foreach (var (health, damageBuffer) in
                 SystemAPI.Query<RefRW<Health>, DynamicBuffer<DamageEvent>>())
        {
            int totalDamage = 0;

            for (int i = 0; i < damageBuffer.Length; i++)
            {
                totalDamage += damageBuffer[i].amount;
            }

            damageBuffer.Clear();

            if (totalDamage == 0)
                continue;

            var h = health.ValueRW;
            h.current = math.max(0, h.current - totalDamage);
            health.ValueRW = h;
        }
    }
}
