using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器汇总 Entity 收到的全部伤害事件并一次性更新生命值
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerApplyDamageSystem : ISystem
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

                // 同一帧可能有多个攻击来源，先求和再写回可避免覆盖
                for (int i = 0; i < damageBuffer.Length; i++)
                {
                    totalDamage += damageBuffer[i].Amount;
                }

                damageBuffer.Clear();

                // 零伤害事件仍需清空，但不触发生命值写回
                if (totalDamage == 0)
                    continue;

                var healthData = health.ValueRW;
                healthData.Current = math.max(0, healthData.Current - totalDamage);
                health.ValueRW = healthData;
            }
        }
    }
}
