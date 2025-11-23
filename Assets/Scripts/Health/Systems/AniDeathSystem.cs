using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ApplyDamageSystem))]
[UpdateAfter(typeof(AniAttackTargetCleanupSystem))]  // 确保在扣血之后跑
public partial struct AniDeathSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<Health>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (health, entity) in
                 SystemAPI.Query<RefRO<Health>>()
                     .WithEntityAccess())
        {
            var h = health.ValueRO;

            // 活着的跳过
            if (h.current > 0)
                continue;

            // 基地、FragileCrystal 这些交给别的系统
            if (SystemAPI.HasComponent<FragileCrystal>(entity) ||
                SystemAPI.HasComponent<BigBaseTag>(entity))
            {
                continue;
            }

            // // 如果你想等攻击目标清理完，可以在这儿再加一道：
            // if (!SystemAPI.HasComponent<AniTargetCleanedTag>(entity)) continue;

            ecb.DestroyEntity(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
