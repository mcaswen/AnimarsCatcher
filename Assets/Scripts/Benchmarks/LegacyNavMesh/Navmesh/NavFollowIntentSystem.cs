using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Gameplay;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 将同步后的导航目标转换为角色移动意图
    /// 客户端负责平滑跟随 服务端额外负责推进路径点
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GameplayPostMovementSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial struct NavFollowIntentSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<NavAgent, NavSteering, LocalTransform, AniMoveIntent>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            bool isServer = state.WorldUnmanaged.Flags.HasFlag(WorldFlags.GameServer);
            float deltaTime = SystemAPI.Time.DeltaTime;

            foreach (var (navAgent, navSteering, transform, moveIntent, entity) in
                     SystemAPI.Query<
                             RefRW<NavAgent>,
                             RefRW<NavSteering>,
                             RefRO<LocalTransform>,
                             RefRW<AniMoveIntent>>()
                         .WithEntityAccess())
            {
                // 每帧先清空意图 防止失效路径沿用上一帧速度
                moveIntent.ValueRW.DesiredVelocity = float3.zero;

                if (navSteering.ValueRO.HasPath == 0)
                    continue;

                float3 currentPosition  = transform.ValueRO.Position;
                float3 steeringTarget   = navSteering.ValueRO.SteeringTarget;
                float  stoppingDistance = navAgent.ValueRO.StoppingDistance;

                float3 toTarget = steeringTarget - currentPosition;
                float  distance = math.length(toTarget);

                if (distance > 1e-4f)
                {
                    float3 direction = toTarget / distance;

                    // 根据帧时长限制最大步长 避免低帧率下越过目标
                    float maxStepDistance = navAgent.ValueRO.Speed * deltaTime;

                    float3 desiredVelocity;

                    if (distance <= maxStepDistance)
                    {
                        // 用剩余位移反推速度 使本帧恰好停在目标点
                        desiredVelocity = toTarget / deltaTime;
                    }
                    else
                    {
                        desiredVelocity = direction * navAgent.ValueRO.Speed;
                    }

                    moveIntent.ValueRW.DesiredVelocity = desiredVelocity;
                }

                // 路径点索引属于权威状态 仅由服务端推进

                if (!isServer)
                    continue;

                if (distance <= math.max(stoppingDistance, 0.05f))
                {
                    if (state.EntityManager.HasBuffer<NavWaypoint>(entity))
                    {
                        var waypoints = state.EntityManager.GetBuffer<NavWaypoint>(entity);
                        int nextIndex = navAgent.ValueRO.CurrentWaypointIndex + 1;

                        if (nextIndex < waypoints.Length)
                        {
                            navAgent.ValueRW.CurrentWaypointIndex = nextIndex;
                            float3 nextTarget = waypoints[nextIndex].Position;
                            navSteering.ValueRW.SteeringTarget = nextTarget;
                        }
                        else
                        {
                            // 到达终点后更新黑板版本并停止当前路径
                            var blackboard = SystemAPI.GetBuffer<FsmVar>(entity);
                            blackboard.SetBool(AniMovementBlackboardKeys.NavStop, true);
                            int version = blackboard.GetInt(AniMovementBlackboardKeys.NavRequestVersion);
                            blackboard.SetInt(AniMovementBlackboardKeys.NavRequestVersion, version + 1);

                            navSteering.ValueRW.HasPath = 0;
                        }
                    }
                }
            }
        }
    }
}
