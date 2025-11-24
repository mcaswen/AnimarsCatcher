using Unity.Burst; 
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(NavFollowIntentSystem))]
[BurstCompile]
public partial struct AniPhysicsMoveSystem : ISystem
{

    private ComponentLookup<AniPhysicsConfig> _aniLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, AniMoveIntent, AniPhysicsConfig>()
                .Build());
        
        _aniLookup = state.GetComponentLookup<AniPhysicsConfig>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _aniLookup.Update(ref state);

        var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
        var physicsWorld = physicsWorldSingleton.PhysicsWorld;

        float deltaTime = SystemAPI.Time.DeltaTime;

        // 一个系统一帧共用一个列表即可
        var separationHits = new NativeList<DistanceHit>(16, Allocator.Temp);

        foreach (var (transform, moveIntent, config, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniMoveIntent>, RefRO<AniPhysicsConfig>>()
                          .WithEntityAccess())
        {
            // UnityEngine.Debug.Log(
            // $"[AniPhysicsMoveSystem] entity {entity.Index} filter: belongsTo={config.ValueRO.Filter.BelongsTo}, collidesWith={config.ValueRO.Filter.CollidesWith}");

            float3 currentPosition = transform.ValueRO.Position;
            var filter = config.ValueRO.Filter;

            // 计算“分离方向”，只作为 steering 力，不直接改位置
            float3 separationDir = float3.zero;
            float  maxWeight     = 0f; // 分离权重
            {
                const float separationRadius = 0.8f;

                separationHits.Clear();

                var pointInput = new PointDistanceInput
                {
                    Position    = currentPosition,
                    MaxDistance = separationRadius,
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
                        
                        if (!_aniLookup.HasComponent(hitEntity))
                            continue;

                        float distance    = hit.Distance;
                        float penetration = separationRadius - distance;
                        if (penetration <= 0f)
                            continue;

                        float3 n = hit.SurfaceNormal;
                        n.y = 0;
                        n   = math.normalizesafe(n);

                        if (math.all(n == float3.zero))
                            continue;

                        float weight = math.saturate(penetration / separationRadius);

                        accumulated += n * weight;
                        totalWeight += weight;
                        maxWeight    = math.max(maxWeight, weight);
                    }

                    if (totalWeight > 0f)
                    {
                        separationDir = accumulated / totalWeight;
                        separationDir = math.normalizesafe(separationDir);
                    }
                }
            }

            // 有意图时的移动（Nav + 分离 steering 合成）
            float3 baseVelocity = moveIntent.ValueRO.DesiredVelocity;
            float  baseSpeedSq  = math.lengthsq(baseVelocity);

            const float separationStrength = 2.0f;

            bool isMoving            = baseSpeedSq > 1e-4f;
            bool hasStrongSeparation = maxWeight > 0.4f;  // > 0.4 表示挤得比较厉害

            float3 finalVelocity;

            if (isMoving)
            {
                // 移动时：在原有速度上加上分离 steering
                finalVelocity = baseVelocity;

                if (math.lengthsq(separationDir) > 1e-6f)
                {
                    // 用权重衰减，避免抖动
                    finalVelocity += separationDir * (separationStrength * maxWeight);
                }
            }
            else
            {
                // 不在移动时：只在重叠部分大于阈值的情况下才进行分离
                if (hasStrongSeparation && math.lengthsq(separationDir) > 1e-6f)
                {
                    finalVelocity = separationDir * (separationStrength * maxWeight);
                }
                else
                {
                    finalVelocity = float3.zero;
                }
            }

            float speedSq = math.lengthsq(finalVelocity);

            const float minVisualSpeed = 0.05f;
            float minVisualSpeedSq = minVisualSpeed * minVisualSpeed;

            if (speedSq < minVisualSpeedSq)
            {
                finalVelocity = float3.zero;
                speedSq = 0f;
            }

            // 先把旧的旋转取出来，后面做插值
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
                    // 查出命中的实体
                    var hitBody   = physicsWorld.Bodies[hit.RigidBodyIndex];
                    var hitEntity = hitBody.Entity;

                    // 命中自己忽略
                    if (hitEntity != entity)
                    {
                        float hitDistance    = desiredDistance * hit.Fraction;
                        float travelDistance = math.max(0f, hitDistance - skin);
                        finalDelta           = moveDirection * travelDistance;
                    }
                }

                currentPosition += finalDelta;

                // 只考虑平面（X-Z）方向，让 Up 永远是世界 Y 轴
                float3 flatDir = new float3(moveDirection.x, 0f, moveDirection.z);
                if (math.lengthsq(flatDir) > 1e-6f)
                {
                    quaternion targetRot = quaternion.LookRotationSafe(flatDir, math.up());

                    // 加入简单平滑，避免瞬间转向导致卡顿
                    const float rotationLerpSpeed = 10f;
                    float t = math.saturate(rotationLerpSpeed * deltaTime);

                    newTransform.Rotation = math.slerp(newTransform.Rotation, targetRot, t);
                }
            }

            newTransform.Position = currentPosition;
            transform.ValueRW     = newTransform;
        }

        separationHits.Dispose();
    }
}
