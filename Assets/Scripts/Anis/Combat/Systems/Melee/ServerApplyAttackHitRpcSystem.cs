using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

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

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        if (_ghostIdToEntity.IsCreated)
            _ghostIdToEntity.Dispose();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityManager = state.EntityManager;

        _pendingLookup.Update(ref state);
        _attributesLookup.Update(ref state);
        _healthLookup.Update(ref state);
        _campLookup.Update(ref state);
        _meleeAttackableLookup.Update(ref state);

        // 先把服务器世界里所有 Ghost 的 ghostId → Entity 映射建出来
        _ghostIdToEntity.Clear();

        foreach (var (ghostInstance, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>>()
                     .WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 处理所有 AttackHitRpc
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

            // 只处理近战
            if (attr.AttackMode != AniAttackMode.Melee)
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // ShotId 对不上，说明是过期的那一刀
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
                // 清掉这个 pending
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

            // —— 真正扣血 —— //
            entityCommandBuffer.AddBuffer<DamageEvent>(target).Add(new DamageEvent
            {
                amount = attr.AttackDamage,
            });

            // 这一刀结算完毕
            entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);

            // 用完的 RPC 实体销毁
            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(entityManager);
        entityCommandBuffer.Dispose();
    }
}
