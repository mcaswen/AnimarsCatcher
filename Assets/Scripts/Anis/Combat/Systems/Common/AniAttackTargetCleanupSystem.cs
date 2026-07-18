using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

/// <summary>
/// 标记因生命值耗尽而触发过攻击目标清理的实体
/// </summary>
public struct AniTargetCleanedTag : IComponentData
{
}

/// <summary>
/// 在服务器感知前移除无效、已销毁或死亡的攻击目标
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AniAttackSenseSystem))]
public partial struct AniAttackTargetCleanupSystem : ISystem
{
    private ComponentLookup<Health> _healthLookup;

    /// <summary>
    /// 缓存生命值查询并仅在存在攻击目标时运行
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _healthLookup = state.GetComponentLookup<Health>(isReadOnly: true);

        // 无攻击目标时跳过整个清理系统
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<AniAttackTarget>()
                .Build());
    }

    /// <summary>
    /// 清除目标和待结算快照，并允许攻击者下一帧立即重新感知
    /// </summary>
    /// <param name="state">系统运行状态</param>
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

            // 空实体或无目标类别属于不完整状态
            if (targetEntity == Entity.Null || kind == AniAttackTargetKind.None)
            {
                shouldClear = true;
            }
            // 已销毁实体引用不能继续进入后续查询
            else if (!entityManager.Exists(targetEntity))
            {
                shouldClear = true;
            }
            else
            {
                // 生命值耗尽的目标交给对应死亡流程处理
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

            entityCommandBuffer.RemoveComponent<AniAttackTarget>(attackerEntity);

            if (SystemAPI.HasComponent<AniPendingAttack>(attackerEntity))
            {
                entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);
            }

            if (SystemAPI.HasComponent<AniAttackState>(attackerEntity))
            {
                var stateData = SystemAPI.GetComponent<AniAttackState>(attackerEntity);

                // 清理目标后重置冷却，使新目标出现时可以立即攻击
                stateData.CooldownRemaining = 0f;

                entityCommandBuffer.SetComponent(attackerEntity, stateData);
            }
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}
