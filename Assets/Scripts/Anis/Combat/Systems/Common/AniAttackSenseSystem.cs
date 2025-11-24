using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct AniAttackSenseSystem : ISystem
{
    private EntityQuery _enemyAniQuery;
    private EntityQuery _resourceQuery;
    private EntityQuery _baseQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // 有 Ani + Camp 就开始跑
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<AniAttributes, LocalTransform, Camp>()
                .WithAny<PickerAniTag, BlasterAniTag>()
                .Build());

        // 敌 Ani：位置 + 阵营 + 属性
        _enemyAniQuery = SystemAPI.QueryBuilder()
            .WithAll<LocalTransform, Camp, AniAttributes>()
            .Build();

        // 资源：位置 + 可攻击标记
        _resourceQuery = SystemAPI.QueryBuilder()
            .WithAll<LocalTransform, AttackableResourceTag>()
            .Build();

        // ★ 基地：改成用 LocalTransform 做中心点，不再依赖 BaseWorldAABB 做体积检测
        _baseQuery = SystemAPI.QueryBuilder()
            .WithAll<BaseTag, Camp, LocalTransform, Health>()  // ★ 改这里
            .Build();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        // —— 敌 Ani
        var enemyEntities   = _enemyAniQuery.ToEntityArray(Allocator.Temp);
        var enemyTransforms = _enemyAniQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var enemyCamps      = _enemyAniQuery.ToComponentDataArray<Camp>(Allocator.Temp);

        // —— 资源
        var resourceEntities   = _resourceQuery.ToEntityArray(Allocator.Temp);
        var resourceTransforms = _resourceQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        // —— 基地
        var baseEntities   = _baseQuery.ToEntityArray(Allocator.Temp);
        var baseCamps      = _baseQuery.ToComponentDataArray<Camp>(Allocator.Temp);
        var baseTransforms = _baseQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp); // ★ 新增
        var baseHealth     = _baseQuery.ToComponentDataArray<Health>(Allocator.Temp);

        // —— 对每个 Ani 自己找目标
        foreach (var (attributes, transform, camp, entity) in
                 SystemAPI.Query<RefRO<AniAttributes>, RefRO<LocalTransform>, RefRO<Camp>>()
                     .WithAny<PickerAniTag, BlasterAniTag>()
                     .WithEntityAccess())
        {
            bool isPicker  = SystemAPI.HasComponent<PickerAniTag>(entity);
            bool isBlaster = SystemAPI.HasComponent<BlasterAniTag>(entity);

            // Picker 在 Pick 状态下不攻击
            bool isPicking = SystemAPI.HasComponent<AniCarryResourceOrder>(entity);
            if (isPicker && isPicking)
                continue;

            float3   myPos   = transform.ValueRO.Position;
            float    range   = attributes.ValueRO.AttackRange;
            float    rangeSq = range * range;
            CampType myCamp  = camp.ValueRO.Value;

            Entity              bestTarget = Entity.Null;
            AniAttackTargetKind bestKind   = AniAttackTargetKind.None;
            float               bestDistSq = float.MaxValue;

            // —— 敌 Ani
            for (int i = 0; i < enemyEntities.Length; i++)
            {
                Entity   targetEntity = enemyEntities[i];
                float3   targetPos    = enemyTransforms[i].Position;
                CampType targetCamp   = enemyCamps[i].Value;

                // 友军跳过
                if (targetCamp == myCamp)
                    continue;

                float distSq = math.lengthsq(targetPos - myPos);
                if (distSq > rangeSq)
                    continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestTarget = targetEntity;
                    bestKind   = AniAttackTargetKind.EnemyAni;
                }
            }

            // —— 敌方基地（不抢走 EnemyAni，只抢 None/Resource 或更近的 EnemyBase）
            for (int i = 0; i < baseEntities.Length; i++)
            {
                Entity baseEntity = baseEntities[i];
                var    baseCamp   = baseCamps[i];

                // 友军基地不算
                if (baseCamp.Value == myCamp)
                    continue;

                // 已经没血了的基地忽略
                if (baseHealth[i].current <= 0f)
                    continue;

                // ★ 用基地中心点做检测
                float3 basePos       = baseTransforms[i].Position;
                float  distSqToBase  = math.lengthsq(basePos - myPos);   // ★ 改这里
                if (distSqToBase > rangeSq)
                    continue;

                bool replace = false;

                if (bestKind == AniAttackTargetKind.None ||
                    bestKind == AniAttackTargetKind.Resource)
                {
                    replace = true;
                }
                else if (bestKind == AniAttackTargetKind.EnemyBase &&
                         distSqToBase < bestDistSq)
                {
                    replace = true;
                }
                // bestKind == EnemyAni 时不替换：基地不抢 Ani 目标

                if (!replace)
                    continue;

                bestTarget = baseEntity;
                bestKind   = AniAttackTargetKind.EnemyBase;
                bestDistSq = distSqToBase;
            }

            // —— Blaster：如果还没找到敌 Ani / 基地，再考虑资源
            if (isBlaster && bestKind == AniAttackTargetKind.None)
            {
                for (int i = 0; i < resourceEntities.Length; i++)
                {
                    Entity targetEntity = resourceEntities[i];
                    float3 targetPos    = resourceTransforms[i].Position;

                    float distSq = math.lengthsq(targetPos - myPos);
                    if (distSq > rangeSq)
                        continue;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestTarget = targetEntity;
                        bestKind   = AniAttackTargetKind.Resource;
                    }
                }
            }

            // —— 写 / 删 AniAttackTarget
            if (bestKind != AniAttackTargetKind.None)
            {
                var data = new AniAttackTarget
                {
                    Target = bestTarget,
                    Kind   = bestKind
                };

                if (SystemAPI.HasComponent<AniAttackTarget>(entity))
                    SystemAPI.SetComponent(entity, data);
                else
                    ecb.AddComponent(entity, data);
            }
            else
            {
                if (SystemAPI.HasComponent<AniAttackTarget>(entity))
                    ecb.RemoveComponent<AniAttackTarget>(entity);
            }
        }

        enemyEntities.Dispose();
        enemyTransforms.Dispose();
        enemyCamps.Dispose();

        resourceEntities.Dispose();
        resourceTransforms.Dispose();

        baseEntities.Dispose();
        baseCamps.Dispose();
        baseTransforms.Dispose();   // ★ 注意这里换成 Transform
        baseHealth.Dispose();

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
