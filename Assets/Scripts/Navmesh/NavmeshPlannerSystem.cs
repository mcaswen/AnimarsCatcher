using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using Unity.NetCode;
using UnityEngine.AI;

[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerNavmeshPlannerSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookup;
    private BufferTypeHandle<NavWaypoint> _waypointBufferHandle;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<NavAgent, LocalTransform>()
            .Build());

        _blackboardLookup = state.GetBufferLookup<FsmVar>(true);
        _waypointBufferHandle = state.GetBufferTypeHandle<NavWaypoint>();
    }

    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);
        _waypointBufferHandle.Update(ref state);

        foreach (var (navAgent, navSteering, transform, entity) in
                 SystemAPI.Query<RefRW<NavAgent>, RefRW<NavSteering>, RefRO<LocalTransform>>()
                         .WithEntityAccess())
        {
            if (!_blackboardLookup.HasBuffer(entity))
                continue;

            var blackboard = _blackboardLookup[entity];
            bool navStop = blackboard.GetBool(BlasterAniBlackFsmBoardKeys.K_NavStop);
            int requestVersion = blackboard.GetInt(BlasterAniBlackFsmBoardKeys.K_NavRequestVersion);

            // 仅在版本变化时处理该实体
            if (requestVersion == navAgent.ValueRO.LastHandledNavRequestVersion)
                continue;

            navAgent.ValueRW.LastHandledNavRequestVersion = requestVersion;

            // 停止导航
            if (navStop)
            {
                if (state.EntityManager.HasBuffer<NavWaypoint>(entity))
                    state.EntityManager.GetBuffer<NavWaypoint>(entity).Clear();
                navSteering.ValueRW.HasPath = 0;
                return;
            }

            float3 targetPosition = blackboard.GetFloat3(BlasterAniBlackFsmBoardKeys.K_NavTargetPosition);
            float3 startPosition = transform.ValueRO.Position;

            // 对托管组件 UnityEngine.AI NavMesh 的查询
            var path = new NavMeshPath();
            bool hasPath = CheckPathOnNavMesh(startPosition, targetPosition, ref path);

            if (!hasPath || path.corners == null || path.corners.Length == 0)
            {
                // 无法到达
                blackboard.SetBool(BlasterAniBlackFsmBoardKeys.K_NavStop, true);
                blackboard.SetInt(BlasterAniBlackFsmBoardKeys.K_NavRequestVersion, requestVersion + 1);
                navSteering.ValueRW.HasPath = 0;
                continue;
            }

            // 写入路径 Buffer
            DynamicBuffer<NavWaypoint> waypoints;
            if (!state.EntityManager.HasBuffer<NavWaypoint>(entity))
                waypoints = state.EntityManager.AddBuffer<NavWaypoint>(entity);
            else
                waypoints = state.EntityManager.GetBuffer<NavWaypoint>(entity);

            waypoints.Clear();
            for (int i = 0; i < path.corners.Length; i++)
            {
                waypoints.Add(new NavWaypoint { Position = path.corners[i] });
            }

            navAgent.ValueRW.CurrentWaypointIndex = math.min(1, waypoints.Length - 1); // 防止朝路径的起点移动
            float3 steeringTarget = waypoints[navAgent.ValueRO.CurrentWaypointIndex].Position;

            // 产出可用于 Ghost 同步的转向目标
            navSteering.ValueRW.SteeringTarget = steeringTarget;
            navSteering.ValueRW.PathVersion = requestVersion;
            navSteering.ValueRW.HasPath = 1;
        }
    }

    // 主线程对 NavMesh 的调用
    private static bool CheckPathOnNavMesh(float3 start, float3 end, ref NavMeshPath path)
    {
        if (!NavMesh.SamplePosition(start, out var startHit, 2.0f, NavMesh.AllAreas))
            return false;
        if (!NavMesh.SamplePosition(end, out var endHit, 2.0f, NavMesh.AllAreas))
            return false;

        bool ok = NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path);
        return ok && path.status == NavMeshPathStatus.PathComplete;
    }
}