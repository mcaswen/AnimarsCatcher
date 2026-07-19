using AnimarsCatcher.Gameplay.Contracts;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务器按敌方 Ani、敌方基地、资源的优先级选择范围内最近目标
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AniAttackSenseSystem : ISystem
    {
        private EntityQuery _enemyAniQuery;
        private EntityQuery _resourceQuery;
        private EntityQuery _baseQuery;

        /// <summary>
        /// 建立三类候选目标查询并等待至少一个可攻击 Ani
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            // 感知依赖攻击属性、位置和阵营三类基础数据
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<AniAttributes, LocalTransform, Camp>()
                    .WithAny<PickerAniTag, BlasterAniTag>()
                    .Build());

            // Ani 候选需要位置、阵营和属性以排除自身类型缺失实体
            _enemyAniQuery = SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, Camp, AniAttributes>()
                .Build();

            // 资源候选只包含显式允许攻击的资源
            _resourceQuery = SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, AttackableResourceTag>()
                .Build();

            // 基地感知使用中心点距离，并排除已经失去生命值的基地
            _baseQuery = SystemAPI.QueryBuilder()
                .WithAll<BaseTag, Camp, LocalTransform, Health>()
                .Build();
        }

        /// <summary>
        /// 为每个 Ani 计算范围内的最高优先级最近目标并更新目标组件
        /// </summary>
        /// <param name="state">系统运行状态</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 查询快照在整帧复用，避免为每个感知主体重复收集候选
            var enemyEntities   = _enemyAniQuery.ToEntityArray(Allocator.Temp);
            var enemyTransforms = _enemyAniQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var enemyCamps      = _enemyAniQuery.ToComponentDataArray<Camp>(Allocator.Temp);

            // 资源不需要阵营数据，只参与 Blaster 的最低优先级选择
            var resourceEntities   = _resourceQuery.ToEntityArray(Allocator.Temp);
            var resourceTransforms = _resourceQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            // 基地需要阵营和生命值以排除友方及已摧毁目标
            var baseEntities   = _baseQuery.ToEntityArray(Allocator.Temp);
            var baseCamps      = _baseQuery.ToComponentDataArray<Camp>(Allocator.Temp);
            var baseTransforms = _baseQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var baseHealth     = _baseQuery.ToComponentDataArray<Health>(Allocator.Temp);

            // 每个 Ani 独立按攻击范围和类型执行目标仲裁
            foreach (var (attributes, transform, camp, entity) in
                     SystemAPI.Query<RefRO<AniAttributes>, RefRO<LocalTransform>, RefRO<Camp>>()
                         .WithAny<PickerAniTag, BlasterAniTag>()
                         .WithEntityAccess())
            {
                bool isPicker  = SystemAPI.HasComponent<PickerAniTag>(entity);
                bool isBlaster = SystemAPI.HasComponent<BlasterAniTag>(entity);

                // Picker 搬运资源期间不应被攻击行为打断
                bool isPicking = SystemAPI.HasComponent<AniCarryResourceOrder>(entity);
                if (isPicker && isPicking)
                    continue;

                float3   attackerPosition = transform.ValueRO.Position;
                float    range   = attributes.ValueRO.AttackRange;
                float    rangeSq = range * range;
                CampType attackerCamp = camp.ValueRO.Value;

                Entity              bestTarget = Entity.Null;
                AniAttackTargetKind bestKind   = AniAttackTargetKind.None;
                float               bestDistSq = float.MaxValue;

                // 敌方 Ani 始终是最高优先级，并选择范围内最近者
                for (int i = 0; i < enemyEntities.Length; i++)
                {
                    Entity   targetEntity = enemyEntities[i];
                    float3   targetPos    = enemyTransforms[i].Position;
                    CampType targetCamp   = enemyCamps[i].Value;

                    // 阵营相同的 Ani 不进入候选
                    if (targetCamp == attackerCamp)
                        continue;

                    float distSq = math.lengthsq(targetPos - attackerPosition);
                    if (distSq > rangeSq)
                        continue;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestTarget = targetEntity;
                        bestKind   = AniAttackTargetKind.EnemyAni;
                    }
                }

                // 敌方基地不会覆盖已找到的敌方 Ani
                for (int i = 0; i < baseEntities.Length; i++)
                {
                    Entity baseEntity = baseEntities[i];
                    var    baseCamp   = baseCamps[i];

                    // 友方基地不进入候选
                    if (baseCamp.Value == attackerCamp)
                        continue;

                    // 已摧毁基地由胜负系统处理，不再作为攻击目标
                    if (baseHealth[i].current <= 0f)
                        continue;

                    // 当前规则使用基地中心点距离判断攻击范围
                    float3 basePos       = baseTransforms[i].Position;
                    float  distSqToBase  = math.lengthsq(basePos - attackerPosition);
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
                    // 已选中敌方 Ani 时保持目标，基地不参与替换

                    if (!replace)
                        continue;

                    bestTarget = baseEntity;
                    bestKind   = AniAttackTargetKind.EnemyBase;
                    bestDistSq = distSqToBase;
                }

                // 只有 Blaster 在没有战斗目标时才把资源作为最低优先级目标
                if (isBlaster && bestKind == AniAttackTargetKind.None)
                {
                    for (int i = 0; i < resourceEntities.Length; i++)
                    {
                        Entity targetEntity = resourceEntities[i];
                        float3 targetPos    = resourceTransforms[i].Position;

                        float distSq = math.lengthsq(targetPos - attackerPosition);
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

                // 目标组件只保存当前帧有效结果，无候选时立即移除
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
            baseTransforms.Dispose();
            baseHealth.Dispose();

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
