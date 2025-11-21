using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using Unity.Collections;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[BurstCompile]
public partial struct AniPhysicsProbeSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<AniPhysicsProbe, AniPhysicsConfig, LocalTransform>()
                .Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;

        foreach (var (probe, config, transform) in
                 SystemAPI.Query<RefRW<AniPhysicsProbe>, RefRO<AniPhysicsConfig>, RefRO<LocalTransform>>())
        {
            float3 origin = transform.ValueRO.Position + config.ValueRO.ProbeOffset;
            float3 up = math.up();

            // 向下射线：地面
            {
                float3 start = origin;
                float3 end   = origin - up * config.ValueRO.GroundRayLength;

                var input = new RaycastInput
                {
                    Start  = start,
                    End    = end,
                    Filter = config.ValueRO.Filter
                };

                if (physicsWorld.CastRay(input, out RaycastHit hit))
                {
                    float dist = config.ValueRO.GroundRayLength * hit.Fraction;
                    probe.ValueRW.IsGrounded     = dist < 0.2f; // 自己定个 grounded 阈值
                    probe.ValueRW.GroundDistance = dist;
                    probe.ValueRW.GroundNormal   = hit.SurfaceNormal;
                }
                else
                {
                    probe.ValueRW.IsGrounded     = false;
                    probe.ValueRW.GroundDistance = config.ValueRO.GroundRayLength;
                    probe.ValueRW.GroundNormal   = up;
                }
            }

            // 向前射线：障碍
            {
                float3 forward = math.mul(transform.ValueRO.Rotation, new float3(0, 0, 1));
                forward = math.normalizesafe(forward);

                float3 start = origin;
                float3 end   = origin + forward * config.ValueRO.ForwardRayLength;

                var input = new RaycastInput
                {
                    Start  = start,
                    End    = end,
                    Filter = config.ValueRO.Filter
                };

                if (physicsWorld.CastRay(input, out RaycastHit hit))
                {
                    probe.ValueRW.HasObstacleAhead = true;
                    probe.ValueRW.ObstacleDistance = config.ValueRO.ForwardRayLength * hit.Fraction;
                    probe.ValueRW.ObstacleNormal   = hit.SurfaceNormal;
                }
                else
                {
                    probe.ValueRW.HasObstacleAhead = false;
                    probe.ValueRW.ObstacleDistance = config.ValueRO.ForwardRayLength;
                    probe.ValueRW.ObstacleNormal   = float3.zero;
                }
            }
        }
    }
}
