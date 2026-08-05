using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Benchmarks.LegacyNavigation.Harness;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;
using UnityEngine.AI;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在服务端移动已启动搬运的资源并在交付后结算玩家资源
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AniPhysicsMoveSystem))]
    public partial struct ServerResourceCarryMoveSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<PickableResource, ResourceCarryAssignment, ResourceCarryingTag, LocalTransform>()
                    .Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<LegacyNavigationBenchmarkConfig>())
            {
                return;
            }

            float deltaTime = SystemAPI.Time.DeltaTime;

            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            ComponentLookup<LocalTransform> transformLookup =
                state.GetComponentLookup<LocalTransform>(false);

            foreach (var (pickable, assignmentRef, resourceTransformRef, resourceEntity) in
                     SystemAPI.Query<RefRO<PickableResource>, RefRO<ResourceCarryAssignment>, RefRW<LocalTransform>>()
                         .WithAll<ResourceCarryingTag>()
                         .WithEntityAccess())
            {
                var assignment = assignmentRef.ValueRO;
                var resourceTransform = resourceTransformRef.ValueRO;

                if (!transformLookup.HasComponent(assignment.PlayerRobotEntity))
                    continue;

                LocalTransform playerTransform =
                    transformLookup[assignment.PlayerRobotEntity];

                // 保存移动前位置，用于根据本帧位移更新朝向
                float3 previousPosition = resourceTransform.Position;

                float3 currentPosition = resourceTransform.Position;
                float3 targetPosition  = playerTransform.Position;

                // 搬运速度按有效人数占最大人数的比例缩放
                int assignedCarrierCount = assignment.AssignedCarrierAniCount;
                if (assignedCarrierCount <= 0)
                {
                    // 分配状态异常时等待上游修复，不移动无人搬运的资源
                    continue;
                }

                int maxCarrierFromConfig = math.max(1, pickable.ValueRO.MaximumCarrierAniCount);
                int effectiveCarrierCount = math.clamp(assignedCarrierCount, 0, maxCarrierFromConfig);

                float speedScale = (float)effectiveCarrierCount / maxCarrierFromConfig;
                float baseSpeed  = pickable.ValueRO.CarryMoveSpeed;
                float moveSpeed  = baseSpeed * speedScale;

                if (moveSpeed <= 0f)
                    continue;

                // 优先沿 NavMesh 路径移动，失败时再走直线回退
                bool movedByNavMesh = false;

                float distanceToPlayer = math.distance(currentPosition, targetPosition);
                if (distanceToPlayer > 0.0001f)
                {
                    if (NavMesh.SamplePosition(currentPosition, out var startHit, 2.0f, NavMesh.AllAreas) &&
                        NavMesh.SamplePosition(targetPosition, out var endHit, 2.0f, NavMesh.AllAreas))
                    {
                        var path = new NavMeshPath();
                        if (NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path) &&
                            path.status == NavMeshPathStatus.PathComplete &&
                            path.corners != null &&
                            path.corners.Length > 1)
                        {
                            // corner[0] 通常是当前位置，本帧朝下一个拐点推进
                            float3 navTarget = (float3)path.corners[1];
                            float3 toCorner  = navTarget - currentPosition;
                            float  distToCorner = math.length(toCorner);

                            if (distToCorner > 1e-4f)
                            {
                                float3 dir  = toCorner / distToCorner;
                                float  step = moveSpeed * deltaTime;

                                if (step >= distToCorner)
                                {
                                    currentPosition = navTarget;
                                }
                                else
                                {
                                    currentPosition += dir * step;
                                }

                                movedByNavMesh = true;
                            }
                        }
                    }
                }

                // NavMesh 不可用时仍以相同搬运速度直线接近玩家
                if (!movedByNavMesh)
                {
                    float3 toTarget = targetPosition - currentPosition;
                    float  distance = math.length(toTarget);

                    if (distance > 0.0001f)
                    {
                        float3 direction = toTarget / distance;
                        float  step      = moveSpeed * deltaTime;

                        if (step >= distance)
                        {
                            currentPosition = targetPosition;
                        }
                        else
                        {
                            currentPosition += direction * step;
                        }
                    }
                }

                // 仅在本帧产生实际位移时更新朝向
                {
                    float3 moveDelta   = currentPosition - previousPosition;
                    float3 moveDeltaXZ = new float3(moveDelta.x, 0f, moveDelta.z);

                    if (math.lengthsq(moveDeltaXZ) > 1e-6f)
                    {
                        float3 forward = math.normalizesafe(moveDeltaXZ, new float3(0f, 0f, 1f));
                        quaternion newRotation = quaternion.LookRotationSafe(forward, math.up());

                        resourceTransform.Rotation = newRotation;
                    }
                }

                resourceTransform.Position = currentPosition;

                // 写回资源本身的位置和朝向
                entityCommandBuffer.SetComponent(resourceEntity, resourceTransform);

                // 更新所有 Ani 在资源周围的位置
                if (SystemAPI.HasBuffer<PickableResourceCarrierSlot>(resourceEntity))
                {
                    DynamicBuffer<PickableResourceCarrierSlot> slots =
                        SystemAPI.GetBuffer<PickableResourceCarrierSlot>(resourceEntity);

                    foreach (var (aniTransformRef, carryOrder, aniEntity) in
                             SystemAPI.Query<RefRW<LocalTransform>, RefRO<AniCarryResourceOrder>>()
                                 .WithEntityAccess())
                    {
                        if (carryOrder.ValueRO.ResourceEntity != resourceEntity)
                            continue;

                        int slotIndex = carryOrder.ValueRO.SlotIndex;

                        if (slotIndex < 0 || slotIndex >= slots.Length)
                            continue;

                        float3 slotWorldPosition =
                            resourceTransform.Position + slots[slotIndex].LocalOffset;

                        LocalTransform aniTransform = aniTransformRef.ValueRO;
                        aniTransform.Position = slotWorldPosition;

                        entityCommandBuffer.SetComponent(aniEntity, aniTransform);
                    }
                }

                // DeliveryArrivalRadius 是交付完成的唯一距离阈值
                float remainingDistance =
                    math.distance(resourceTransform.Position, playerTransform.Position);

                if (remainingDistance <= pickable.ValueRO.DeliveryArrivalRadius)
                {
                    // 交付完成后依次结算、释放搬运者并销毁资源
                    GrantPlayerResource(
                        ref state,
                        pickable.ValueRO,
                        assignment);

                    ReleaseCarrierAnis(ref state, ref entityCommandBuffer, resourceEntity, assignment);

                    entityCommandBuffer.DestroyEntity(resourceEntity);
                }
            }

            entityCommandBuffer.Playback(state.EntityManager);
        }

        private void GrantPlayerResource(
            ref SystemState state,
            PickableResource pickable,
            ResourceCarryAssignment assignment)
        {
            foreach (var (foodBuffer, crystalBuffer, hubEntity) in
                     SystemAPI.Query<
                         DynamicBuffer<FoodResourceDeltaEvent>,
                         DynamicBuffer<CrystalResourceDeltaEvent>>()
                         .WithAll<PlayerResourceDeltaHubTag>()
                         .WithEntityAccess())
            {
                if (!SystemAPI.HasComponent<GhostOwner>(assignment.PlayerRobotEntity))
                    continue;

                var ghostOwner = SystemAPI.GetComponent<GhostOwner>(assignment.PlayerRobotEntity);
                int networkId = ghostOwner.NetworkId;

                int totalAmount = pickable.TotalResourceAmount;

                switch (pickable.ResourceItemKind)
                {
                    case ResourceItemKind.Food:
                        foodBuffer.Add(new FoodResourceDeltaEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = totalAmount
                        });

                        break;

                    case ResourceItemKind.Crystal:
                        crystalBuffer.Add(new CrystalResourceDeltaEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = totalAmount
                        });

                        break;
                }
            }
        }

        private void ReleaseCarrierAnis(
            ref SystemState state,
            ref EntityCommandBuffer entityCommandBuffer,
            Entity resourceEntity,
            ResourceCarryAssignment assignment)
        {
            foreach (var (carryOrder, aniEntity) in
                    SystemAPI.Query<RefRO<AniCarryResourceOrder>>()
                        .WithEntityAccess())
            {
                if (carryOrder.ValueRO.ResourceEntity != resourceEntity)
                    continue;

                // 移除搬运指令并解除命令锁
                entityCommandBuffer.RemoveComponent<AniCarryResourceOrder>(aniEntity);

                if (SystemAPI.HasComponent<AniCommandLockedTag>(aniEntity))
                {
                    entityCommandBuffer.SetComponentEnabled<AniCommandLockedTag>(aniEntity, false);
                }

                // 搬运结束后恢复跟随玩家
                if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                {
                    var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                    if (assignment.PlayerRobotEntity != Entity.Null)
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.Follow);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.PlayerEntity,
                            assignment.PlayerRobotEntity);

                        Blackboard.SetBool(ref blackboard,
                            AniMovementBlackboardKeys.NavStop,
                            false);
                    }
                }
            }
        }
    }
}
