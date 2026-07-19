using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using System.Diagnostics;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器按冷却生成唯一攻击序号、视图请求和目标快照
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AniAttackSenseSystem))]
    public partial struct AniAttackFireSystem : ISystem
    {
        private uint _shotCounter;

        /// <summary>
        /// 等待具有攻击属性和冷却状态的 Ani 实体可用
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<AniAttributes, AniAttackState>()
                    .WithAny<PickerAniTag, BlasterAniTag>()
                    .Build());
        }

        /// <summary>
        /// 为冷却结束且目标有效的 Ani 创建一次不可重复的攻击快照
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (attributes, attackState, entity) in
                     SystemAPI.Query<RefRO<AniAttributes>, RefRW<AniAttackState>>()
                         .WithAny<PickerAniTag, BlasterAniTag>()
                         .WithEntityAccess())
            {

                ref AniAttackState attackStateData = ref attackState.ValueRW;

                attackStateData.CooldownRemaining -= deltaTime;
                if (attackStateData.CooldownRemaining > 0f)
                    continue;

                // 没有目标时保留已完成的冷却，使新目标出现后可立即攻击
                if (!SystemAPI.HasComponent<AniAttackTarget>(entity))
                    continue;

                var target = SystemAPI.GetComponent<AniAttackTarget>(entity);
                if (target.Target == Entity.Null || target.Kind == AniAttackTargetKind.None)
                    continue;

                // 仅在确认发起攻击后重置冷却
                attackStateData.CooldownRemaining = attributes.ValueRO.AttackInterval;

                uint shotId = ++_shotCounter;

                // FireRequest 通过 Ghost 同步驱动视图动画
                var fireRequest = new AniAttackFireRequest
                {
                    ShotId = shotId
                };

                if (SystemAPI.HasComponent<AniAttackFireRequest>(entity))
                    entityCommandBuffer.SetComponent(entity, fireRequest);
                else
                    entityCommandBuffer.AddComponent(entity, fireRequest);

                // PendingAttack 冻结本次目标，避免动画期间感知变化改写结算对象
                var pending = new AniPendingAttack
                {
                    Target = target.Target,
                    Kind   = target.Kind,
                    ShotId = shotId
                };

                if (SystemAPI.HasComponent<AniPendingAttack>(entity))
                    entityCommandBuffer.SetComponent(entity, pending);
                else
                    entityCommandBuffer.AddComponent(entity, pending);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
