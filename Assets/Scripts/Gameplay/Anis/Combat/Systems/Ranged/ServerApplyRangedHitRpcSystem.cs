using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器验证远程攻击序号、模式、目标能力和阵营后写入伤害事件
    /// </summary>
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

            // Ghost 生命周期会改变映射，因此每帧从服务器世界重建
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

                // 先将客户端提供的 GhostId 映射回服务器权威攻击者实体
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

                var pending    = _pendingLookup[attackerEntity];
                var attributes = _attributesLookup[attackerEntity];

                // 攻击模式必须与远程 RPC 链路匹配
                if (attributes.AttackMode != AniAttackMode.Ranged)
                {
                    ecb.DestroyEntity(rpcEntity);
                    continue;
                }

                // ShotId 不匹配表示事件过期、乱序或重复
                if (pending.ShotId != shotId)
                {
                    ecb.DestroyEntity(rpcEntity);
                    continue;
                }

                // 未命中网络实体时仍需消费本次待结算快照
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

                // 阵营相同的候选目标不产生伤害
                if (attackerCamp.Value == targetCamp.Value)
                {
                    ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                    ecb.DestroyEntity(rpcEntity);
                    continue;
                }

                // 伤害写入缓冲区，由统一伤害系统在后续阶段汇总
                ecb.AddBuffer<DamageEvent>(targetEntity).Add(new DamageEvent
                {
                    Amount = attributes.AttackDamage,
                });

                // 待结算快照只能成功消费一次
                ecb.RemoveComponent<AniPendingAttack>(attackerEntity);
                ecb.DestroyEntity(rpcEntity);
            }

            ecb.Playback(entityManager);
            ecb.Dispose();
        }
    }
}
