using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器验证近战攻击序号、模式、目标能力和阵营后写入伤害事件
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerApplyMeleeHitRpcSystem : ISystem
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
                     SystemAPI.Query<RefRO<MeleeHitRpc>, RefRO<ReceiveRpcCommandRequest>>()
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

                var pending    = _pendingLookup[attackerEntity];
                var attributes = _attributesLookup[attackerEntity];

                // 只有近战模式才能通过这类 RPC 结算
                if (attributes.AttackMode != AniAttackMode.Melee)
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
                    // 目标失效后清除本次攻击记录，避免同一攻击持续重试
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
                    Amount = attributes.AttackDamage,
                });

                // 每条待结算攻击记录只能成功处理一次
                entityCommandBuffer.RemoveComponent<AniPendingAttack>(attackerEntity);

                // RPC Entity 处理后立即销毁
                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(entityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
