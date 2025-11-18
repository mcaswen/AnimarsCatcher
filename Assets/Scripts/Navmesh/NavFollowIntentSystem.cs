using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;

[UpdateInGroup(typeof(SimulationSystemGroup))]
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
            // 默认不动
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

                // 这个帧的期望移动距离
                float maxStepDistance = navAgent.ValueRO.Speed * deltaTime;

                float3 desiredVelocity;

                if (distance <= maxStepDistance)
                {
                    // 防止 overshoot：刚好停在目标点
                    desiredVelocity = toTarget / deltaTime;
                }
                else
                {
                    desiredVelocity = direction * navAgent.ValueRO.Speed;
                }

                moveIntent.ValueRW.DesiredVelocity = desiredVelocity;
            }

            // 仅服务端推进路径点  

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
                        // 已到终点：通知黑板、清路径
                        var blackboard = SystemAPI.GetBuffer<FsmVar>(entity);
                        blackboard.SetBool(BlasterAniBlackFsmBoardKeys.K_NavStop, true);
                        int version = blackboard.GetInt(BlasterAniBlackFsmBoardKeys.K_NavRequestVersion);
                        blackboard.SetInt(BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, version + 1);

                        navSteering.ValueRW.HasPath = 0;
                    }
                }
            }
        }
    }
}
