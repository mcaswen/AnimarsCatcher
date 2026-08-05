using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在服务端按区域上限、波次配置和阻挡检测刷新资源
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerResourceRespawnSystem : ISystem
    {
        // 限制全局生成预算以避免单帧实例化峰值
        private const int MaxSpawnsPerFrame = 2;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<ResourceSpawnArea>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 基准测试期间隔离动态生成造成的负载波动
            if (SystemAPI.HasSingleton<NavigationBenchmarkEnabled>()) return;

            float deltaTime = SystemAPI.Time.DeltaTime;

            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 所有刷新区域共享同一个单帧生成预算
            int globalSpawnBudget = MaxSpawnsPerFrame;

            foreach (var (areaRef, areaEntity) in
                    SystemAPI.Query<RefRW<ResourceSpawnArea>>()
                            .WithEntityAccess())
            {
                // 预算耗尽后其余区域保留计时状态到下一帧
                if (globalSpawnBudget <= 0)
                    break;

                ResourceSpawnArea area = areaRef.ValueRW;

                area.RespawnTimer += deltaTime;

                // 刷新间隔未到时只更新计时器
                if (area.RespawnTimer < area.RespawnInterval)
                {
                    areaRef.ValueRW = area;
                    continue;
                }

                // 保持超时状态，未补满的区域会在后续帧继续获得刷新机会
                int currentFoodCount = 0;
                int currentCrystalCount = 0;

                // 可拾取掉落物只按 Food 计入区域容量
                foreach (var (pickable, transform) in
                        SystemAPI.Query<RefRO<PickableResource>, RefRO<LocalTransform>>())
                {
                    if (pickable.ValueRO.ResourceItemKind != ResourceItemKind.Food)
                        continue;

                    float3 pos = transform.ValueRO.Position;

                    if (!IsInsideArea(pos, area.Center, area.HalfExtentsXZ))
                        continue;

                    currentFoodCount++;
                }

                // 只统计完整水晶，破坏后的掉落物不占水晶上限
                foreach (var (fragileCrystal, transform) in
                        SystemAPI.Query<RefRO<FragileCrystal>, RefRO<LocalTransform>>())
                {
                    float3 pos = transform.ValueRO.Position;

                    if (!IsInsideArea(pos, area.Center, area.HalfExtentsXZ))
                        continue;

                    currentCrystalCount++;
                }

                UnityEngine.Debug.Log($"[ServerResourceRespawnSystem] Area {areaEntity.Index} Food={currentFoodCount}/{area.MaxFoodCount} Crystal={currentCrystalCount}/{area.MaxCrystalCount}");

                int foodSpawnBudget = math.min(area.FoodPerWave,
                                            area.MaxFoodCount - currentFoodCount);

                int crystalSpawnBudget = math.min(area.CrystalPerWave,
                                                area.MaxCrystalCount - currentCrystalCount);

                // 区域达到容量后重新开始波次冷却
                if (foodSpawnBudget <= 0 && crystalSpawnBudget <= 0)
                {
                    area.RespawnTimer = 0f;
                    areaRef.ValueRW   = area;
                    continue;
                }

                DynamicBuffer<FoodResourceSpawnPrefabReference>    foodPrefabs    = default;
                DynamicBuffer<CrystalResourceSpawnPrefabReference> crystalPrefabs = default;

                bool hasFoodPrefabs =
                    SystemAPI.HasBuffer<FoodResourceSpawnPrefabReference>(areaEntity);

                bool hasCrystalPrefabs =
                    SystemAPI.HasBuffer<CrystalResourceSpawnPrefabReference>(areaEntity);

                if (hasFoodPrefabs)
                    foodPrefabs = SystemAPI.GetBuffer<FoodResourceSpawnPrefabReference>(areaEntity);

                if (hasCrystalPrefabs)
                    crystalPrefabs = SystemAPI.GetBuffer<CrystalResourceSpawnPrefabReference>(areaEntity);

                if (foodSpawnBudget > 0 && (!hasFoodPrefabs || foodPrefabs.Length == 0))
                    foodSpawnBudget = 0;

                if (crystalSpawnBudget > 0 && (!hasCrystalPrefabs || crystalPrefabs.Length == 0))
                    crystalSpawnBudget = 0;

                if (foodSpawnBudget <= 0 && crystalSpawnBudget <= 0)
                {
                    // 缺口无法生成时重置冷却，避免每帧重复扫描
                    area.RespawnTimer = 0f;
                    areaRef.ValueRW   = area;
                    continue;
                }

                // 单个区域不能超出当前全局剩余预算
                int areaSpawnBudget = globalSpawnBudget;

                // 同一区域的两类资源共享随机序列，种子只在帧末写回一次
                var random = new Unity.Mathematics.Random(
                    area.RandomSeed == 0 ? 1u : area.RandomSeed);

                // Food 优先占用本帧预算
                if (foodSpawnBudget > 0 && areaSpawnBudget > 0)
                {
                    int spawnNow = math.min(foodSpawnBudget, areaSpawnBudget);

                    for (int i = 0; i < spawnNow; i++)
                    {
                        bool success = TrySpawnFood(
                            ref state,
                            ref entityCommandBuffer,
                            ref random,
                            area,
                            foodPrefabs);

                        // Food 无可用位置时保留预算给 Crystal
                        if (!success)
                            break;

                        globalSpawnBudget--;
                        areaSpawnBudget--;

                        if (globalSpawnBudget <= 0 || areaSpawnBudget <= 0)
                            break;
                    }
                }

                // Crystal 使用 Food 剩余的预算
                if (crystalSpawnBudget > 0 && areaSpawnBudget > 0 && globalSpawnBudget > 0)
                {
                    int spawnNow = math.min(crystalSpawnBudget, areaSpawnBudget);

                    for (int i = 0; i < spawnNow; i++)
                    {
                        bool success = TrySpawnCrystal(
                            ref state,
                            ref entityCommandBuffer,
                            ref random,
                            area,
                            crystalPrefabs);

                        if (!success)
                            break;

                        globalSpawnBudget--;
                        areaSpawnBudget--;

                        if (globalSpawnBudget <= 0 || areaSpawnBudget <= 0)
                            break;
                    }
                }

                // 更新随机种子，避免每次都从同一个状态开始
                area.RandomSeed = random.NextUInt();
                areaRef.ValueRW = area;
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }

        private static bool IsInsideArea(float3 position, float3 center, float2 halfExtentsXZ)
        {
            float dx = position.x - center.x;
            float dz = position.z - center.z;

            return math.abs(dx) <= halfExtentsXZ.x &&
                   math.abs(dz) <= halfExtentsXZ.y;
        }

        private static bool TrySpawnFood(
            ref SystemState state,
            ref EntityCommandBuffer entityCommandBuffer,
            ref Unity.Mathematics.Random random,
            in ResourceSpawnArea area,
            in DynamicBuffer<FoodResourceSpawnPrefabReference> foodPrefabs)
        {
            if (foodPrefabs.Length == 0)
                return false;

            for (int attempt = 0; attempt < area.MaxSpawnAttemptsPerResource; attempt++)
            {
                float3 spawnPosition = GetRandomPositionInArea(ref random, area);

                if (!IsPositionFree(spawnPosition,
                                    area.SpawnCheckRadius,
                                    area.BlockerLayerMask))
                {
                    continue;
                }

                int prefabIndex = random.NextInt(0, foodPrefabs.Length);

                Entity prefabEntity   = foodPrefabs[prefabIndex].Prefab;
                Entity instanceEntity = entityCommandBuffer.Instantiate(prefabEntity);

                entityCommandBuffer.SetComponent(instanceEntity, new LocalTransform
                {
                    Position = spawnPosition,
                    Rotation = quaternion.identity,
                    Scale    = 1f
                });

                return true;
            }

            return false;
        }

        private static bool TrySpawnCrystal(
            ref SystemState state,
            ref EntityCommandBuffer entityCommandBuffer,
            ref Unity.Mathematics.Random random,
            in ResourceSpawnArea area,
            in DynamicBuffer<CrystalResourceSpawnPrefabReference> crystalPrefabs)
        {
            if (crystalPrefabs.Length == 0)
                return false;

            for (int attempt = 0; attempt < area.MaxSpawnAttemptsPerResource; attempt++)
            {
                float3 spawnPosition = GetRandomPositionInArea(ref random, area);

                if (!IsPositionFree(spawnPosition,
                                    area.SpawnCheckRadius,
                                    area.BlockerLayerMask))
                {
                    continue;
                }

                int prefabIndex =
                    random.NextInt(0, crystalPrefabs.Length);

                Entity prefabEntity   = crystalPrefabs[prefabIndex].Prefab;
                Entity instanceEntity = entityCommandBuffer.Instantiate(prefabEntity);

                entityCommandBuffer.SetComponent(instanceEntity, new LocalTransform
                {
                    Position = spawnPosition,
                    Rotation = quaternion.identity,
                    Scale    = 6f
                });

                return true;
            }

            return false;
        }

        private static float3 GetRandomPositionInArea(
            ref Unity.Mathematics.Random random,
            in ResourceSpawnArea area)
        {
            float rx = random.NextFloat(-area.HalfExtentsXZ.x, area.HalfExtentsXZ.x);
            float rz = random.NextFloat(-area.HalfExtentsXZ.y, area.HalfExtentsXZ.y);

            return new float3(
                area.Center.x + rx,
                area.Center.y + area.SpawnHeightOffset,
                area.Center.z + rz);
        }

        private static bool IsPositionFree(float3 position, float radius, int layerMask)
        {
            Vector3 center = new Vector3(position.x, position.y, position.z);

            int mask = layerMask == 0 ? ~0 : layerMask;

            return !Physics.CheckSphere(center,
                                        radius,
                                        mask,
                                        QueryTriggerInteraction.Ignore);
        }
    }
}
