using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Benchmarks.LegacyNavigation.Harness;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.AI;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 记录 Ani 上一帧导航状态以识别首次到达站位槽
    /// </summary>
    public struct AniNavFindArrivalTracker : IComponentData
    {
        // 零表示无路径，非零表示仍在寻路
        public byte PreviousHasPath;
    }

    /// <summary>
    /// 为搬运 Ani 规划站位路径并在全部就位后启动资源移动
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerResourceCarrySetupSystem : ISystem
    {
        private ComponentLookup<AniNavFindArrivalTracker> _arrivalTrackerLookup;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<PickableResource, ResourceCarryAssignment, LocalTransform>()
                    .WithAll<PickableResourceCarrierSlot>()
                    .Build());

            _arrivalTrackerLookup = state.GetComponentLookup<AniNavFindArrivalTracker>(isReadOnly: false);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<LegacyNavigationBenchmarkConfig>())
            {
                return;
            }

            _arrivalTrackerLookup.Update(ref state);

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (pickable, assignmentRef, transform, resourceEntity) in
                     SystemAPI.Query<RefRO<PickableResource>, RefRW<ResourceCarryAssignment>, RefRO<LocalTransform>>()
                         .WithEntityAccess())
            {
                var assignment = assignmentRef.ValueRO;

                // 搬运已启动的资源由移动系统接管
                if (assignment.IsCarryStarted != 0)
                    continue;

                if (!SystemAPI.HasBuffer<PickableResourceCarrierSlot>(resourceEntity))
                    continue;

                var slots = SystemAPI.GetBuffer<PickableResourceCarrierSlot>(resourceEntity);

                int readyCount = 0;

                // 统计分配给当前资源的 Ani，并将已到位成员吸附到槽位
                foreach (var (aniTransform, carryOrder, navSteering, aniEntity) in
                        SystemAPI.Query<RefRO<LocalTransform>,
                                        RefRO<AniCarryResourceOrder>,
                                        RefRO<NavSteering>>()
                            .WithEntityAccess())
                {
                    if (carryOrder.ValueRO.ResourceEntity != resourceEntity)
                        continue;

                    int slotIndex = carryOrder.ValueRO.SlotIndex;
                    if (slotIndex < 0 || slotIndex >= slots.Length)
                        continue;

                    float3 slotWorldPosition =
                        transform.ValueRO.Position + slots[slotIndex].LocalOffset;

                    float distance =
                        math.distance(aniTransform.ValueRO.Position, slotWorldPosition);

                    bool currentHasPath = navSteering.ValueRO.HasPath != 0;

                    bool hasTracker = _arrivalTrackerLookup.HasComponent(aniEntity);
                    AniNavFindArrivalTracker tracker;

                    if (!hasTracker)
                    {
                        // 首次观察时以当前值建立基线，避免把无历史状态误判为刚到达
                        tracker = new AniNavFindArrivalTracker
                        {
                            PreviousHasPath = (byte)(currentHasPath ? 1 : 0)
                        };

                        entityCommandBuffer.AddComponent(aniEntity, tracker);
                    }
                    else
                    {
                        tracker = _arrivalTrackerLookup[aniEntity];
                    }

                    bool previousHasPath  = hasTracker && (tracker.PreviousHasPath != 0);
                    bool justFinishedPath = previousHasPath && !currentHasPath;

                    // Lookup 在遍历期间只读，跟踪状态通过 ECB 延迟写回
                    tracker.PreviousHasPath = (byte)(currentHasPath ? 1 : 0);

                    if (hasTracker)
                    {
                        entityCommandBuffer.SetComponent(aniEntity, tracker);
                    }

                    // Find 到达只认 HasPath 从一变为零的瞬间
                    bool isFindArrived = false;

                    if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                    {
                        var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                        int commandMode = Blackboard.GetInt(
                            ref blackboard,
                            AniMovementBlackboardKeys.CommandMode);

                        if (commandMode == (int)AniMovementCommandMode.Find &&
                            justFinishedPath)
                        {
                            isFindArrived = true;
                        }
                    }

                    if (isFindArrived || distance <= pickable.ValueRO.StartCarryDistance)
                    {
                        readyCount++;

                        // 吸附到资源槽位，并锁定新的玩家命令
                        entityCommandBuffer.SetComponent(aniEntity, new LocalTransform
                        {
                            Position = slotWorldPosition,
                            Rotation = transform.ValueRO.Rotation,
                            Scale    = aniTransform.ValueRO.Scale
                        });

                        if (!SystemAPI.HasComponent<AniCommandLockedTag>(aniEntity))
                        {
                            entityCommandBuffer.AddComponent<AniCommandLockedTag>(aniEntity);
                        }
                        entityCommandBuffer.SetComponentEnabled<AniCommandLockedTag>(aniEntity, true);

                        if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                        {
                            var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                            // 搬运期间切换到 Idle 并清除导航目标
                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.Idle);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.TargetEntity,
                                Entity.Null);

                            Blackboard.SetBool(ref blackboard,
                                AniMovementBlackboardKeys.NavStop,
                                true);
                        }

                        UnityEngine.Debug.Log(
                            $"[ServerResourceCarrySetupSystem] Carrier Ani Entity {aniEntity.Index} is ready to carry Resource Entity {resourceEntity.Index} in Slot {slotIndex}.");
                    }
                }

                if (readyCount == 0)
                    continue;

                assignment.ReadyCarrierAniCount = readyCount;

                // 全部分配成员就位后移交资源给搬运移动系统
                if (readyCount >= assignment.AssignedCarrierAniCount)
                {
                    assignment.IsCarryStarted = 1;

                    entityCommandBuffer.SetComponent(resourceEntity, assignment);
                    entityCommandBuffer.AddComponent<ResourceCarryingTag>(resourceEntity);

                    // 资源具备导航组件时预先规划到玩家的路径
                    TryPlanNavPathForResource(
                        ref state,
                        ref entityCommandBuffer,
                        resourceEntity,
                        transform.ValueRO.Position,
                        assignment.PlayerRobotEntity);

                    UnityEngine.Debug.Log(
                        $"[ServerResourceCarrySetupSystem] All assigned Carrier Ani are ready. Resource Entity {resourceEntity.Index} starts carrying to PlayerRobot Entity {assignment.PlayerRobotEntity.Index}.");
                }
                else
                {
                    // 尚未满员时只刷新就位数量
                    entityCommandBuffer.SetComponent(resourceEntity, assignment);
                }
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }

        // 为资源规划从当前位置到玩家主角的 NavMesh 路径
        private void TryPlanNavPathForResource(
            ref SystemState state,
            ref EntityCommandBuffer entityCommandBuffer,
            Entity resourceEntity,
            float3 resourcePosition,
            Entity playerRobotEntity)
        {
            // 缺少导航组件时保留直线移动回退
            if (!SystemAPI.HasComponent<NavAgent>(resourceEntity) ||
                !SystemAPI.HasComponent<NavSteering>(resourceEntity))
            {
                return;
            }

            if (!SystemAPI.HasComponent<LocalTransform>(playerRobotEntity))
                return;

            var playerTransform = SystemAPI.GetComponent<LocalTransform>(playerRobotEntity);

            var path = new NavMeshPath();
            if (!CheckPathOnNavMesh(resourcePosition, playerTransform.Position, ref path))
            {
                // 路径失败时由资源移动系统执行直线回退
                return;
            }

            var navAgent   = SystemAPI.GetComponent<NavAgent>(resourceEntity);
            var navSteering = SystemAPI.GetComponent<NavSteering>(resourceEntity);

            // SetBuffer 覆盖上一条路径并保证后续索引只引用本次结果
            var waypoints = entityCommandBuffer.SetBuffer<NavWaypoint>(resourceEntity);
            waypoints.Clear();

            for (int i = 0; i < path.corners.Length; i++)
            {
                waypoints.Add(new NavWaypoint
                {
                    Position = path.corners[i]
                });
            }

            if (waypoints.Length == 0)
            {
                navSteering.HasPath = 0;
                entityCommandBuffer.SetComponent(resourceEntity, navSteering);
                return;
            }

            // 第零个拐点通常是当前位置，从后续拐点开始可避免回走
            int startIndex = math.min(1, waypoints.Length - 1);
            navAgent.CurrentWaypointIndex = startIndex;
            navSteering.SteeringTarget    = waypoints[startIndex].Position;
            navSteering.PathVersion       = navSteering.PathVersion + 1;
            navSteering.HasPath           = 1;

            entityCommandBuffer.SetComponent(resourceEntity, navAgent);
            entityCommandBuffer.SetComponent(resourceEntity, navSteering);
        }

        // 端点投影后只接受完整 NavMesh 路径
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
