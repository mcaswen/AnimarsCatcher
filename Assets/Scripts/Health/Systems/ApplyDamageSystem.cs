using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

/// <summary>
/// 在服务器汇总实体收到的全部伤害事件并一次性更新生命值
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ApplyDamageSystem : ISystem
{
    /// <summary>
    /// 仅在同时存在生命值和伤害缓冲区的实体时运行
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<Health, DamageEvent>()
                .Build());
    }

    /// <summary>
    /// 汇总每个缓冲区后立即清空事件，保证每次伤害只结算一次
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (health, damageBuffer) in
                 SystemAPI.Query<RefRW<Health>, DynamicBuffer<DamageEvent>>())
        {
            int totalDamage = 0;

            // 同一帧可能有多个攻击来源，先求和再写回可避免覆盖
            for (int i = 0; i < damageBuffer.Length; i++)
            {
                totalDamage += damageBuffer[i].amount;
            }

            damageBuffer.Clear();

            // 零伤害事件仍需清空，但不触发生命值写回
            if (totalDamage == 0)
                continue;

            var h = health.ValueRW;
            h.current = math.max(0, h.current - totalDamage);
            health.ValueRW = h;
        }
    }
}
