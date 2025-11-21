using Unity.Burst; 
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(NavFollowIntentSystem))]
[BurstCompile]
public partial struct AniPhysicsMoveSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, AniMoveIntent, AniPhysicsConfig>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
        var physicsWorld = physicsWorldSingleton.PhysicsWorld;

        float deltaTime = SystemAPI.Time.DeltaTime;

        // 一个系统一帧共用一个列表即可，循环里 Clear
        var separationHits = new NativeList<DistanceHit>(16, Allocator.Temp);

        foreach (var (transform, moveIntent, config, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniMoveIntent>, RefRO<AniPhysicsConfig>>()
                          .WithEntityAccess())
        {
            float3 currentPosition = transform.ValueRO.Position;
            var filter = config.ValueRO.Filter;

            // ===== A. 计算“分离方向”，只作为 steering 力，不直接改位置 =====
            float3 separationDir = float3.zero;

            {
                const float separationRadius = 1.0f;   // 个人空间半径，可以 0.7~1.2 自己调

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

                        // 查出命中的实体
                        var hitBody   = physicsWorld.Bodies[hit.RigidBodyIndex];
                        var hitEntity = hitBody.Entity;

                        // 自己跳过
                        if (hitEntity == entity)
                            continue;

                        // 这里可以只对“其他 Ani”做分离，如果你有专门的 AniTag，就用那个
                        // 比如：if (!SystemAPI.HasComponent<AniMoveIntent>(hitEntity)) continue;

                        float distance = hit.Distance; // <0: 重叠，>0: 外部

                        // 只对在 separationRadius 内的做处理
                        float penetration = separationRadius - distance;
                        if (penetration <= 0f)
                            continue;

                        // 法线只用水平分量
                        float3 n = hit.SurfaceNormal;
                        n.y = 0;
                        n   = math.normalizesafe(n);

                        if (math.all(n == float3.zero))
                            continue;

                        // 距离越近，权重越大
                        float weight = math.saturate(penetration / separationRadius);

                        accumulated += n * weight;
                        totalWeight += weight;
                    }

                    if (totalWeight > 0f)
                    {
                        separationDir = accumulated / totalWeight;
                        separationDir = math.normalizesafe(separationDir);
                    }
                }
            }

            // ===== B. 有意图时的移动（Nav + 分离 steering 合成） =====
            float3 baseVelocity = moveIntent.ValueRO.DesiredVelocity;

            // 把分离当额外 steering 力
            float3 finalVelocity = baseVelocity;
            {
                const float separationStrength = 3.0f; // 分离强度，数值越大“互相排斥”越明显

                if (math.lengthsq(separationDir) > 1e-6f)
                {
                    finalVelocity += separationDir * separationStrength;
                }
            }

            float speedSq = math.lengthsq(finalVelocity);

            if (speedSq > 1e-6f)
            {
                float3 desiredDelta    = finalVelocity * deltaTime;
                float  desiredDistance = math.length(desiredDelta);
                float3 moveDirection   = desiredDelta / desiredDistance;

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
            }

            var newTransform = transform.ValueRO;
            newTransform.Position = currentPosition;
            transform.ValueRW = newTransform;
        }

        separationHits.Dispose();
    }
}
