using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.AI;

public struct AniNavFindArrivalTracker : IComponentData
{
    public byte PreviousHasPath; // 0 或 1
}

// [BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerResourceCarrySetupSystem : ISystem
{
    private ComponentLookup<AniNavFindArrivalTracker> arrivalTrackerLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<PickableResource, ResourceCarryAssignment, LocalTransform>()
                .WithAll<PickableResourceCarrierSlot>()  // 有槽位
                .Build());
        
        arrivalTrackerLookup = state.GetComponentLookup<AniNavFindArrivalTracker>(isReadOnly: false);
    }

    // [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        arrivalTrackerLookup.Update(ref state);

        UnityEngine.Debug.Log("[ServerResourceCarrySetupSystem] OnUpdate start");

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (pickable, assignmentRef, transform, resourceEntity) in
                 SystemAPI.Query<RefRO<PickableResource>, RefRW<ResourceCarryAssignment>, RefRO<LocalTransform>>()
                     .WithEntityAccess())
        {
            var assignment = assignmentRef.ValueRO;

            // 已经在搬运了就不用再处理
            if (assignment.IsCarryStarted != 0)
                continue;

            if (!SystemAPI.HasBuffer<PickableResourceCarrierSlot>(resourceEntity))
                continue;

            var slots = SystemAPI.GetBuffer<PickableResourceCarrierSlot>(resourceEntity);

            int readyCount = 0;

            // —— 统计当前这个资源的 Ani，就位就吸附 + 上锁
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

                bool hasTracker = arrivalTrackerLookup.HasComponent(aniEntity);
                AniNavFindArrivalTracker tracker;

                if (!hasTracker)
                {
                    // 第一次看到这个 Ani：我们还没有历史状态，
                    // 约定 previousHasPath = currentHasPath
                    tracker = new AniNavFindArrivalTracker
                    {
                        PreviousHasPath = (byte)(currentHasPath ? 1 : 0)
                    };

                    // 在 ECB 里添加组件（真正加到实体上要等 Playback）
                    entityCommandBuffer.AddComponent(aniEntity, tracker);
                }
                else
                {
                    tracker = arrivalTrackerLookup[aniEntity];
                }

                bool previousHasPath  = hasTracker && (tracker.PreviousHasPath != 0);
                bool justFinishedPath = previousHasPath && !currentHasPath;

                // ---------- 更新后的状态：通过 ECB 写回，而不是写 Lookup ----------

                tracker.PreviousHasPath = (byte)(currentHasPath ? 1 : 0);

                if (hasTracker)
                {
                    // 已经有这个组件了，用 SetComponent 更新它
                    entityCommandBuffer.SetComponent(aniEntity, tracker);
                }

                // ---------------- Find 到终点判定：只认 1 -> 0 这一瞬间 ----------------

                bool isFindArrived = false;

                if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                {
                    var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                    int commandMode = Blackboard.GetInt(
                        ref blackboard,
                        AniMovementBlackboardKeys.K_CommandMode);

                    // 处于 Find 状态，并且刚刚完成了一条 Nav 路径（从有路到没路）
                    if (commandMode == (int)AniMovementCommandMode.Find &&
                        justFinishedPath)
                    {
                        isFindArrived = true;
                    }
                }

                // ------------ 旧逻辑 + 新逻辑合并在一起 ------------

                if (isFindArrived || distance <= pickable.ValueRO.StartCarryDistance)
                {
                    readyCount++;

                    // 吸附到槽位
                    entityCommandBuffer.SetComponent(aniEntity, new LocalTransform
                    {
                        Position = slotWorldPosition,
                        Rotation = transform.ValueRO.Rotation,
                        Scale    = aniTransform.ValueRO.Scale
                    });

                    // 锁命令 Tag：没有就加，有就 Enable
                    if (!SystemAPI.HasComponent<AniCommandLockedTag>(aniEntity))
                    {
                        entityCommandBuffer.AddComponent<AniCommandLockedTag>(aniEntity);
                    }
                    entityCommandBuffer.SetComponentEnabled<AniCommandLockedTag>(aniEntity, true);

                    if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                    {
                        var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                        // CommandMode = Idle
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.K_CommandMode,
                            (int)AniMovementCommandMode.Idle);

                        // 清掉目标
                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.K_TargetEntity,
                            Entity.Null);

                        // 通知 Nav 停止
                        Blackboard.SetBool(ref blackboard,
                            AniMovementBlackboardKeys.K_NavStop,
                            true);
                    }

                    UnityEngine.Debug.Log(
                        $"[ServerResourceCarrySetupSystem] Carrier Ani Entity {aniEntity.Index} is ready to carry Resource Entity {resourceEntity.Index} in Slot {slotIndex}.");
                }
            }

            if (readyCount == 0)
                continue;

            assignment.ReadyCarrierAniCount = readyCount;

            // 所有分配的 Ani 都就位 → 开始搬运
            if (readyCount >= assignment.AssignedCarrierAniCount)
            {
                assignment.IsCarryStarted = 1;

                entityCommandBuffer.SetComponent(resourceEntity, assignment);
                entityCommandBuffer.AddComponent<ResourceCarryingTag>(resourceEntity);

                // 在这里给资源规划 NavMesh 路径（如果资源身上有 Nav 组件）
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
                // 还没满员，但有人到位了，就先刷新 Ready 数量
                entityCommandBuffer.SetComponent(resourceEntity, assignment);
            }
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }

    // 给资源算一条从资源当前位置 -> 玩家机器人 的 NavMesh 路径
    private void TryPlanNavPathForResource(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity resourceEntity,
        float3 resourcePosition,
        Entity playerRobotEntity)
    {
        // 必须有 NavAgent + NavSteering 才算 NavMesh 路径
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
            // 找不到路径，就退回直线 MoveTowards，那边会处理
            return;
        }

        // 读当前的 NavAgent / NavSteering 值
        var navAgent   = SystemAPI.GetComponent<NavAgent>(resourceEntity);
        var navSteering = SystemAPI.GetComponent<NavSteering>(resourceEntity);

        // 用 ECB 写 NavWaypoint Buffer（SetBuffer 会保证有这个 Buffer）
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

        // 像 Ani 一样，从第 1 个点开始走，避免往回走到起点
        int startIndex = math.min(1, waypoints.Length - 1);
        navAgent.CurrentWaypointIndex = startIndex;
        navSteering.SteeringTarget    = waypoints[startIndex].Position;
        navSteering.PathVersion       = navSteering.PathVersion + 1;
        navSteering.HasPath           = 1;

        entityCommandBuffer.SetComponent(resourceEntity, navAgent);
        entityCommandBuffer.SetComponent(resourceEntity, navSteering);
    }

    // 直接复制你 Nav 系统里的这个函数即可
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
