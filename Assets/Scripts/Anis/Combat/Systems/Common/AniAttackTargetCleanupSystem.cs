using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

public struct AniTargetCleanedTag : IComponentData
{
}

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
// 放在感知系统之前，先把脏目标清掉，再由 AniAttackSenseSystem 重新感知
[UpdateBefore(typeof(AniAttackSenseSystem))]
public partial struct AniAttackTargetCleanupSystem : ISystem
{
    private ComponentLookup<Health> _healthLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _healthLookup = state.GetComponentLookup<Health>(isReadOnly: true);

        // 只有有攻击目标时才更新
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<AniAttackTarget>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _healthLookup.Update(ref state);

        EntityManager entityManager = state.EntityManager;
        var entityCommandBuffer     = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (attackTarget, attackerEntity) in
                 SystemAPI.Query<RefRO<AniAttackTarget>>()
                     .WithEntityAccess())
        {
            Entity targetEntity = attackTarget.ValueRO.Target;
            AniAttackTargetKind kind = attackTarget.ValueRO.Kind;

            bool shouldClear = false;

            // 1) 本身就是无效目标
            if (targetEntity == Entity.Null || kind == AniAttackTargetKind.None)
            {
                shouldClear = true;
            }
            // 2) 实体已经被 Destroy 了
            else if (!entityManager.Exists(targetEntity))
            {
                shouldClear = true;
            }
            else
            {
                // 3) 有 Health 且已经 <= 0
                if (_healthLookup.HasComponent(targetEntity))
                {
                    var health = _healthLookup[targetEntity];

                    if (health.current <= 0f)
                    {
                        shouldClear = true;
                        entityCommandBuffer.AddComponent<AniTargetCleanedTag>(targetEntity);
                    }
                }

            }

            if (!shouldClear)
                continue;

            // —— 真正清目标 —— //

            entityCommandBuffer.RemoveComponent<AniAttackTarget>(attackerEntity);

            if (SystemAPI.HasComponent<AniPendingAttack>(attackerEntity))
            {
                entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);
            }

            if (SystemAPI.HasComponent<AniAttackState>(attackerEntity))
            {
                var stateData = SystemAPI.GetComponent<AniAttackState>(attackerEntity);

                // 重置 CD：这样下帧可以立即重新感知 / 重新开火
                stateData.CooldownRemaining = 0f;

                entityCommandBuffer.SetComponent(attackerEntity, stateData);
            }
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}
