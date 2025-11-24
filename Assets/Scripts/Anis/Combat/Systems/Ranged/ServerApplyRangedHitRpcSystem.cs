using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(RpcSystem))]
public partial struct ServerApplyRangedHitRpcSystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _ghostIdToEntity;

    private ComponentLookup<AniPendingAttack>      _pendingLookup;
    private ComponentLookup<AniAttributes>         _attributesLookup;
    private ComponentLookup<Health>                _healthLookup;
    private ComponentLookup<Camp>                  _campLookup;
    private ComponentLookup<RangedAttackableTag>   _rangedAttackableLookup;
    private ComponentLookup<LocalTransform>        _transformLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        _ghostIdToEntity = new NativeParallelHashMap<int, Entity>(1024, Allocator.Persistent);

        _pendingLookup           = state.GetComponentLookup<AniPendingAttack>(isReadOnly: false);
        _attributesLookup        = state.GetComponentLookup<AniAttributes>(isReadOnly: true);
        _healthLookup            = state.GetComponentLookup<Health>(isReadOnly: false);
        _campLookup              = state.GetComponentLookup<Camp>(isReadOnly: true);
        _rangedAttackableLookup  = state.GetComponentLookup<RangedAttackableTag>(isReadOnly: true);
        _transformLookup         = state.GetComponentLookup<LocalTransform>(isReadOnly: true);

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
        _rangedAttackableLookup.Update(ref state);
        _transformLookup.Update(ref state);

        // 重建一次 ghostId -> Entity 映射
        _ghostIdToEntity.Clear();
        foreach (var (ghostInstance, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>>()
                     .WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (rpc, req, rpcEntity) in
                 SystemAPI.Query<RefRO<RangedHitRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            int  attackerGhostId = rpc.ValueRO.AttackerGhostId;
            int  targetGhostId   = rpc.ValueRO.TargetGhostId;
            uint shotId          = rpc.ValueRO.ShotId;

            // UnityEngine.Debug.Log($"ServerApplyRangedHitRpcSystem: Received RangedHitRpc: AttackerGhostId={attackerGhostId}, TargetGhostId={targetGhostId}, ShotId={shotId}");  

            // ghostId -> attacker Entity
            if (!_ghostIdToEntity.TryGetValue(attackerGhostId, out var attackerEntity))
            {
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            if (!_pendingLookup.HasComponent(attackerEntity) ||
                !_attributesLookup.HasComponent(attackerEntity) ||
                !_campLookup.HasComponent(attackerEntity))
            {
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            var pending = _pendingLookup[attackerEntity];
            var attr    = _attributesLookup[attackerEntity];

            // 只处理远程
            if (attr.AttackMode != AniAttackMode.Ranged)
            {
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            // ShotId 不匹配 -> 过期或乱序，丢掉
            if (pending.ShotId != shotId)
            {
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            // 如果没打到实体（比如只命中地面），就结束这发，不扣血
            if (targetGhostId < 0)
            {
                ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            if (!_ghostIdToEntity.TryGetValue(targetGhostId, out var targetEntity))
            {
                ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            if (!_healthLookup.HasComponent(targetEntity) ||
                !_campLookup.HasComponent(targetEntity)   ||
                !_rangedAttackableLookup.HasComponent(targetEntity))
            {
                ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            var attackerCamp = _campLookup[attackerEntity];
            var targetCamp   = _campLookup[targetEntity];

            // 友伤不算
            if (attackerCamp.Value == targetCamp.Value)
            {
                ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                ecb.DestroyEntity(rpcEntity);
                continue;
            }

            // —— 真正扣血 —— //
            ecb.AddBuffer<DamageEvent>(targetEntity).Add(new DamageEvent
            {
                amount = attr.AttackDamage,
            });

            // 这一发结算完毕
            ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(entityManager);
        ecb.Dispose();
    }
}
