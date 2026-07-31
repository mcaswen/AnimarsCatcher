using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Gameplay.Contracts;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在服务器将导航速度、邻居分离力和物理射线结果合成为最终移动
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NavFollowIntentSystem))]
    [UpdateBefore(typeof(GameplayPostMovementSystemGroup))]
    [BurstCompile]
    public partial struct AniPhysicsMoveSystem : ISystem
    {

        private ComponentLookup<AniPhysicsConfig> _physicsConfigLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate<PhysicsWorldSingleton>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<LocalTransform, AniMoveIntent, AniPhysicsConfig>()
                    .Build());

            _physicsConfigLookup = state.GetComponentLookup<AniPhysicsConfig>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _physicsConfigLookup.Update(ref state);

            var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
            var physicsWorld = physicsWorldSingleton.PhysicsWorld;

            float deltaTime = SystemAPI.Time.DeltaTime;

            // 整帧复用同一临时列表，避免为每个 Ani 重复分配
            var separationHits = new NativeList<DistanceHit>(16, Allocator.Temp);

            foreach (var (transform, moveIntent, config, entity) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniMoveIntent>, RefRO<AniPhysicsConfig>>()
                              .WithEntityAccess())
            {
                float3 currentPosition = transform.ValueRO.Position;
                var filter = config.ValueRO.Filter;

                // 分离方向只参与速度合成，不能直接改位置以免绕过碰撞截断
                float3 separationDirection = float3.zero;
                float  maxWeight     = 0f; // 分离权重
                {
                    const float SeparationRadius = 0.8f;

                    separationHits.Clear();

                    var pointInput = new PointDistanceInput
                    {
                        Position    = currentPosition,
                        MaxDistance = SeparationRadius,
                        Filter      = filter
                    };

                    if (physicsWorld.CalculateDistance(pointInput, ref separationHits))
                    {
                        float3 accumulated = float3.zero;
                        float   totalWeight = 0f;

                        for (int i = 0; i < separationHits.Length; ++i)
                        {
                            var hit = separationHits[i];

                            var hitBody   = physicsWorld.Bodies[hit.RigidBodyIndex];
                            var hitEntity = hitBody.Entity;

                            if (hitEntity == entity)
                                continue;

                            if (!_physicsConfigLookup.HasComponent(hitEntity))
                                continue;

                            float distance    = hit.Distance;
                            float penetration = SeparationRadius - distance;
                            if (penetration <= 0f)
                                continue;

                            float3 surfaceNormal = hit.SurfaceNormal;
                            surfaceNormal.y = 0;
                            surfaceNormal   = math.normalizesafe(surfaceNormal);

                            if (math.all(surfaceNormal == float3.zero))
                                continue;

                            float weight = math.saturate(penetration / SeparationRadius);

                            accumulated += surfaceNormal * weight;
                            totalWeight += weight;
                            maxWeight    = math.max(maxWeight, weight);
                        }

                        if (totalWeight > 0f)
                        {
                            separationDirection = accumulated / totalWeight;
                            separationDirection = math.normalizesafe(separationDirection);
                        }
                    }
                }

                // 导航速度提供主方向，分离力只用于缓解群体重叠
                float3 baseVelocity = moveIntent.ValueRO.DesiredVelocity;
                float  baseSpeedSq  = math.lengthsq(baseVelocity);

                const float SeparationStrength = 2.0f;

                bool isMoving            = baseSpeedSq > 1e-4f;
                bool hasStrongSeparation = maxWeight > 0.4f;  // 静止时只处理明显重叠，避免轻微接触持续抖动

                float3 finalVelocity;

                if (isMoving)
                {
                    // 移动时保留导航意图并叠加按穿透程度衰减的分离力
                    finalVelocity = baseVelocity;

                    if (math.lengthsq(separationDirection) > 1e-6f)
                    {
                        // 权重随穿透程度变化，避免接触边缘产生突变
                        finalVelocity += separationDirection * (SeparationStrength * maxWeight);
                    }
                }
                else
                {
                    // 静止时只修复严重重叠，避免阵型成员在目标点持续漂移
                    if (hasStrongSeparation && math.lengthsq(separationDirection) > 1e-6f)
                    {
                        finalVelocity = separationDirection * (SeparationStrength * maxWeight);
                    }
                    else
                    {
                        finalVelocity = float3.zero;
                    }
                }

                float speedSq = math.lengthsq(finalVelocity);

                const float MinVisualSpeed = 0.05f;
                float minVisualSpeedSq = MinVisualSpeed * MinVisualSpeed;

                if (speedSq < minVisualSpeedSq)
                {
                    finalVelocity = float3.zero;
                    speedSq = 0f;
                }

                // 基于旧旋转插值到移动方向，避免朝向瞬间跳变
                var newTransform = transform.ValueRO;

                if (speedSq > 0f)
                {
                    float3 desiredDelta    = finalVelocity * deltaTime;
                    float  desiredDistance = math.length(desiredDelta);
                    float3 moveDirection   = desiredDelta / math.max(desiredDistance, 1e-6f);

                    float  probeHeight = config.ValueRO.ProbeOffset.y;
                    float  skin        = 0.05f;
                    float3 rayStart    = currentPosition + new float3(0, probeHeight, 0);
                    float3 rayEnd      = rayStart + desiredDelta;

                    var rayInput = new RaycastInput
                    {
                        Start  = rayStart,
                        End    = rayEnd,
                        Filter = filter
                    };

                    float3 finalDelta = desiredDelta;

                    if (physicsWorld.CastRay(rayInput, out RaycastHit hit))
                    {
                        // 射线距离按皮肤宽度回退，阻止本帧位移穿过障碍
                        var hitBody   = physicsWorld.Bodies[hit.RigidBodyIndex];
                        var hitEntity = hitBody.Entity;

                        // 碰撞过滤器可能包含自身，需要显式忽略
                        if (hitEntity != entity)
                        {
                            float hitDistance    = desiredDistance * hit.Fraction;
                            float travelDistance = math.max(0f, hitDistance - skin);
                            finalDelta           = moveDirection * travelDistance;
                        }
                    }

                    currentPosition += finalDelta;

                    // 朝向仅使用水平分量，保持世界 Y 轴为上方向
                    float3 flatDir = new float3(moveDirection.x, 0f, moveDirection.z);
                    if (math.lengthsq(flatDir) > 1e-6f)
                    {
                        quaternion targetRot = quaternion.LookRotationSafe(flatDir, math.up());

                        // 使用帧时间归一化插值速度，降低急转造成的视觉卡顿
                        const float RotationLerpSpeed = 10f;
                        float t = math.saturate(RotationLerpSpeed * deltaTime);

                        newTransform.Rotation = math.slerp(newTransform.Rotation, targetRot, t);
                    }
                }

                newTransform.Position = currentPosition;
                transform.ValueRW     = newTransform;
            }

            separationHits.Dispose();
        }
    }
}
