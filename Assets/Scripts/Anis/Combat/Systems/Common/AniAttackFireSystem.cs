using Unity.Burst;
using Unity.Entities;
using Unity.Collections;
using System.Diagnostics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(AniAttackSenseSystem))]
public partial struct AniAttackFireSystem : ISystem
{
    private uint _shotCounter;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<AniAttributes, AniAttackState>()
                .WithAny<PickerAniTag, BlasterAniTag>()
                .Build());
    }

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

            ref AniAttackState st = ref attackState.ValueRW;

            st.CooldownRemaining -= deltaTime;
            if (st.CooldownRemaining > 0f)
                continue;

            // 没有目标就不攻击，也不重置 CD
            if (!SystemAPI.HasComponent<AniAttackTarget>(entity))
                continue;

            var target = SystemAPI.GetComponent<AniAttackTarget>(entity);
            if (target.Target == Entity.Null || target.Kind == AniAttackTargetKind.None)
                continue;

            // 冷却时间归位
            st.CooldownRemaining = attributes.ValueRO.AttackInterval;

            // string debugKind = "";

            // switch (target.Kind)
            // {
            //     case AniAttackTargetKind.None:
            //         debugKind = "None";
            //         break;
            //     case AniAttackTargetKind.EnemyAni:
            //         debugKind = "EnemyAni";
            //         break;
            //     case AniAttackTargetKind.Resource:
            //         debugKind = "Resource";
            //         break;  
            // }

            // UnityEngine.Debug.Log($"[AniAttackFireSystem] Ani Entity {entity.Index} firing at target {target.Target} of kind " + debugKind);

            uint shotId = ++_shotCounter;

            // 给视图看的：FireRequest（用于触发动画）
            var fireRequest = new AniAttackFireRequest
            {
                ShotId = shotId
            };

            if (SystemAPI.HasComponent<AniAttackFireRequest>(entity))
                entityCommandBuffer.SetComponent(entity, fireRequest);
            else
                entityCommandBuffer.AddComponent(entity, fireRequest);

            // 给逻辑用的：PendingAttack 快照
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
