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
    private static readonly CollisionFilter s_DefaultFilter = new CollisionFilter
    {
        BelongsTo   = ~0u,
        CollidesWith = ~0u,
        GroupIndex  = 0
    };

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<LocalTransform, AniMoveIntent>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
        var physicsWorld = physicsWorldSingleton.PhysicsWorld;

        float deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (transform, moveIntent) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniMoveIntent>>())
        {
            float3 currentPosition = transform.ValueRO.Position;

            // 先做重叠纠正（静止也跑这一段）
            {
                // 这里用点-世界最近表面的距离来做一个“粗暴最小分离”
                var pointInput = new PointDistanceInput
                {
                    Position = currentPosition,
                    MaxDistance = 0.1f, // 最大尝试分离距离
                    Filter = s_DefaultFilter,
                };

                if (physicsWorld.CalculateDistance(pointInput, out DistanceHit hit))
                {
                    // 如果 distance 为负数，说明在 collider 里面
                    if (hit.Distance < -1e-3f)
                    {
                        // 沿着法线方向推出来一点
                        float3 separation = hit.SurfaceNormal * (-hit.Distance + 0.01f);
                        currentPosition += separation;
                    }
                }
            }

            // ====== B. 再处理这一帧的移动（有速度才动） ======
            float3 desiredVelocity = moveIntent.ValueRO.DesiredVelocity;
            float speedSq = math.lengthsq(desiredVelocity);

            if (speedSq > 1e-6f)
            {
                float3 desiredDelta    = desiredVelocity * deltaTime;
                float  desiredDistance = math.length(desiredDelta);
                float3 moveDirection   = desiredDelta / desiredDistance;

                float  probeHeight = 0.5f;
                float  skin        = 0.05f;
                float3 rayStart    = currentPosition + new float3(0, probeHeight, 0);
                float3 rayEnd      = rayStart + desiredDelta;

                var rayInput = new RaycastInput
                {
                    Start  = rayStart,
                    End    = rayEnd,
                    Filter = s_DefaultFilter,
                };

                float3 finalDelta = desiredDelta;

                if (physicsWorld.CastRay(rayInput, out RaycastHit hit))
                {
                    float hitDistance    = desiredDistance * hit.Fraction;
                    float travelDistance = math.max(0f, hitDistance - skin);
                    finalDelta = moveDirection * travelDistance;
                }

                currentPosition += finalDelta;
            }

            var newTransform = transform.ValueRO;
            newTransform.Position = currentPosition;
            transform.ValueRW = newTransform;
        }
    }
}
