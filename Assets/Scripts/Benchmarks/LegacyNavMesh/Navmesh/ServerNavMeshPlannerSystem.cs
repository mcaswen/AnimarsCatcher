using AnimarsCatcher.Core.Fsm;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;
using Unity.NetCode;
using UnityEngine.AI;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在服务端响应导航请求并将 NavMesh 路径写入实体缓冲区
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerNavMeshPlannerSystem : ISystem
    {
        private BufferLookup<FsmVar> _blackboardLookup;
        private BufferTypeHandle<NavWaypoint> _waypointBufferHandle;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(SystemAPI.QueryBuilder()
                .WithAll<NavAgent, LocalTransform>()
                .Build());

            _blackboardLookup = state.GetBufferLookup<FsmVar>(false);
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
                bool navStop = blackboard.GetBool(AniMovementBlackboardKeys.NavStop);
                int requestVersion = blackboard.GetInt(AniMovementBlackboardKeys.NavRequestVersion);

                // 请求版本未变化时复用现有路径 避免每帧重复计算
                if (requestVersion == navAgent.ValueRO.LastHandledNavRequestVersion)
                    continue;

                navAgent.ValueRW.LastHandledNavRequestVersion = requestVersion;

                // 停止请求会清空路径并撤销转向目标
                if (navStop)
                {
                    if (state.EntityManager.HasBuffer<NavWaypoint>(entity))
                        state.EntityManager.GetBuffer<NavWaypoint>(entity).Clear();
                    navSteering.ValueRW.HasPath = 0;
                    return;
                }

                float3 targetPosition = blackboard.GetFloat3(AniMovementBlackboardKeys.NavTargetPosition);
                float3 startPosition = transform.ValueRO.Position;

                // UnityEngine.AI API 只能在主线程执行 此处保持同步规划
                var path = new NavMeshPath();
                bool hasPath = CheckPathOnNavMesh(startPosition, targetPosition, ref path);

                if (!hasPath || path.corners == null || path.corners.Length == 0)
                {
                    // 不可达时递增版本并回写停止状态 防止持续重试
                    blackboard.SetBool(AniMovementBlackboardKeys.NavStop, true);
                    blackboard.SetInt(AniMovementBlackboardKeys.NavRequestVersion, requestVersion + 1);
                    navSteering.ValueRW.HasPath = 0;
                    continue;
                }

                // 覆盖路径缓冲区 保证索引与本次规划结果对应
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

                // 第零个角点通常是当前位置 优先从后续角点开始移动
                navAgent.ValueRW.CurrentWaypointIndex = math.min(1, waypoints.Length - 1);
                float3 steeringTarget = waypoints[navAgent.ValueRO.CurrentWaypointIndex].Position;

                // 将当前目标和版本写入 Ghost 数据供客户端一致跟随
                navSteering.ValueRW.SteeringTarget = steeringTarget;
                navSteering.ValueRW.PathVersion = requestVersion;
                navSteering.ValueRW.HasPath = 1;
            }
        }

        // 将端点投影到 NavMesh 后只接受完整路径
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
}
