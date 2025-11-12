using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation) ]
public partial struct NavFollowSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        bool isServer = state.WorldUnmanaged.Flags.HasFlag(WorldFlags.GameServer);

        foreach (var (navAgent, navSteering, transform, entity) in
                 SystemAPI.Query<RefRW<NavAgent>, RefRW<NavSteering>, RefRO<LocalTransform>>()
                         .WithEntityAccess())
        {
            if (navSteering.ValueRO.HasPath == 0)
                continue;

            float3 currentPosition = transform.ValueRO.Position;
            float3 steeringTarget  = navSteering.ValueRO.SteeringTarget;
            float  stoppingDistance = navAgent.ValueRO.StoppingDistance;
            float  deltaTime = SystemAPI.Time.DeltaTime;

            // 移动
            float3 toTarget = steeringTarget - currentPosition;
            float  distance = math.length(toTarget);
            if (distance > 1e-4f)
            {
                float3 direction = toTarget / distance;
                float step = navAgent.ValueRO.Speed * deltaTime;
                float3 newPosition = distance <= step ? steeringTarget : currentPosition + direction * step;

                // 写回位置
                var newTransform = transform.ValueRO;
                newTransform.Position = newPosition;
                state.EntityManager.SetComponentData(entity, newTransform);
            }

            // 仅服务端推进路径点
            if (!isServer) continue;

            // 近点推进
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
                        // 已到终点,告知 Planner 停止,并触发一次 NavStop 的版本变化
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
