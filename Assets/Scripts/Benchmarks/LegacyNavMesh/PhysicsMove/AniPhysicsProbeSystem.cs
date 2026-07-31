using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;
using Unity.Collections;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在客户端和服务器采样地面及前向障碍并更新 Ani 探测状态
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [BurstCompile]
    public partial struct AniPhysicsProbeSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
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

                // 向下射线提供接地状态、地面距离和表面法线
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
                        probe.ValueRW.IsGrounded     = dist < 0.2f; // 小阈值允许模型脚底与碰撞面存在轻微偏差
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

                // 前向射线为移动和表现系统提供障碍距离及法线
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
}
