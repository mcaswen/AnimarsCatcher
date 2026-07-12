using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在服务器验证近战攻击序号、模式、目标能力和阵营后写入伤害事件
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerApplyAttackHitRpcSystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _ghostIdToEntity;

    private ComponentLookup<AniPendingAttack> _pendingLookup;
    private ComponentLookup<AniAttributes>    _attributesLookup;
    private ComponentLookup<Health>           _healthLookup;
    private ComponentLookup<Camp>             _campLookup;
    private ComponentLookup<MeleeAttackableTag> _meleeAttackableLookup;

    /// <summary>
    /// 创建持久化 GhostId 映射并缓存结算所需组件查询
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _ghostIdToEntity = new NativeParallelHashMap<int, Entity>(1024, Allocator.Persistent);

        _pendingLookup         = state.GetComponentLookup<AniPendingAttack>(isReadOnly: false);
        _attributesLookup      = state.GetComponentLookup<AniAttributes>(isReadOnly: true);
        _healthLookup          = state.GetComponentLookup<Health>(isReadOnly: false);
        _campLookup            = state.GetComponentLookup<Camp>(isReadOnly: true);
        _meleeAttackableLookup = state.GetComponentLookup<MeleeAttackableTag>(isReadOnly: true);

        state.RequireForUpdate<GhostInstance>();
    }

    /// <summary>
    /// 释放跨帧持有的 GhostId 映射容器
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        if (_ghostIdToEntity.IsCreated)
            _ghostIdToEntity.Dispose();
    }

    /// <summary>
    /// 消费近战 RPC，并以服务器开火快照为准完成一次性伤害结算
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityManager = state.EntityManager;

        _pendingLookup.Update(ref state);
        _attributesLookup.Update(ref state);
        _healthLookup.Update(ref state);
        _campLookup.Update(ref state);
        _meleeAttackableLookup.Update(ref state);

        // Ghost 生命周期会改变映射，因此每帧从服务器世界重建
        _ghostIdToEntity.Clear();

        foreach (var (ghostInstance, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>>()
                     .WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 客户端只提供攻击者和动画时机，目标取自服务器待结算快照
        foreach (var (rpc, req, rpcEntity) in
                 SystemAPI.Query<RefRO<AttackHitRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            int  attackerGhostId = rpc.ValueRO.AttackerGhostId;
            uint shotId = rpc.ValueRO.ShotId;

            if (!_ghostIdToEntity.TryGetValue(attackerGhostId, out var attackerEntity))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            if (!_pendingLookup.HasComponent(attackerEntity) ||
                !_attributesLookup.HasComponent(attackerEntity) ||
                !_campLookup.HasComponent(attackerEntity))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            var pending   = _pendingLookup[attackerEntity];
            var attr      = _attributesLookup[attackerEntity];

            // 攻击模式必须与近战 RPC 链路匹配
            if (attr.AttackMode != AniAttackMode.Melee)
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // ShotId 不匹配表示事件过期、乱序或重复
            if (pending.ShotId != shotId)
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            Entity target = pending.Target;
            if (target == Entity.Null ||
                !_healthLookup.HasComponent(target) ||
                !_campLookup.HasComponent(target) ||
                !_meleeAttackableLookup.HasComponent(target))
            {
                // 目标失效后消费快照，避免同一攻击持续重试
                entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            var attackerCamp = _campLookup[attackerEntity];
            var targetCamp   = _campLookup[target];

            if (attackerCamp.Value == targetCamp.Value)
            {
                entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 伤害写入缓冲区，由统一伤害系统在后续阶段汇总
            entityCommandBuffer.AddBuffer<DamageEvent>(target).Add(new DamageEvent
            {
                amount = attr.AttackDamage,
            });

            // 待结算快照只能成功消费一次
            entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);

            // RPC 实体消费后立即销毁
            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}
