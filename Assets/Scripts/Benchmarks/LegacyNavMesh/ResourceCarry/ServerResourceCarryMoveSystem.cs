using AnimarsCatcher.Core.Fsm;
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
        /// <summary>
        /// 仅在存在已进入搬运阶段的资源时启用系统
        /// </summary>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder()
                    .WithAll<PickableResource, ResourceCarryAssignment, ResourceCarryingTag, LocalTransform>()
                    .Build());
        }

        /// <summary>
        /// 推进资源位置并处理到达 结算和 Ani 释放
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
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

                // ☆ 移动前的位置，后面用来算朝向
                float3 previousPosition = resourceTransform.Position;

                float3 currentPosition = resourceTransform.Position;
                float3 targetPosition  = playerTransform.Position;

                // ---------- 计算人数缩放后的搬运速度 ----------
                int assignedCarrierCount = assignment.AssignedCarrierAniCount;
                if (assignedCarrierCount <= 0)
                {
                    // 理论上不会出现，没有 Ani 搬就直接跳过
                    continue;
                }

                int maxCarrierFromConfig = math.max(1, pickable.ValueRO.MaximumCarrierAniCount);
                int effectiveCarrierCount = math.clamp(assignedCarrierCount, 0, maxCarrierFromConfig);

                float speedScale = (float)effectiveCarrierCount / maxCarrierFromConfig;
                float baseSpeed  = pickable.ValueRO.CarryMoveSpeed;
                float moveSpeed  = baseSpeed * speedScale;

                if (moveSpeed <= 0f)
                    continue;

                // ---------- 先尝试用 NavMesh 路径移动 ----------
                bool movedByNavMesh = false;

                // 只有距离足够远才动
                float distanceToPlayer = math.distance(currentPosition, targetPosition);
                if (distanceToPlayer > 0.0001f)
                {
                    // 在 NavMesh 上采样起点和终点
                    if (NavMesh.SamplePosition(currentPosition, out var startHit, 2.0f, NavMesh.AllAreas) &&
                        NavMesh.SamplePosition(targetPosition, out var endHit, 2.0f, NavMesh.AllAreas))
                    {
                        var path = new NavMeshPath();
                        if (NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path) &&
                            path.status == NavMeshPathStatus.PathComplete &&
                            path.corners != null &&
                            path.corners.Length > 1)
                        {
                            // corner[0] 基本是当前点，沿 corner[1] 的方向走
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

                // ---------- NavMesh 不可用时，退回原来的直线移动 ----------
                if (!movedByNavMesh)
                {
                    float3 toTarget = targetPosition - currentPosition;
                    float  distance = math.length(toTarget);

                    if (distance > 0.0001f)
                    {
                        float3 direction = toTarget / distance;
                        float  step      = moveSpeed * deltaTime;   // ★ 用 moveSpeed，而不是 CarryMoveSpeed

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

                // 面朝移动方向（仅在本帧有实际位移时更新）
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

                // 检查是否到达玩家机器人附近（这里仍然用 DeliveryArrivalRadius）
                float remainingDistance =
                    math.distance(resourceTransform.Position, playerTransform.Position);

                if (remainingDistance <= pickable.ValueRO.DeliveryArrivalRadius)
                {
                    // 资源送到了：给玩家加资源
                    GrantPlayerResource(
                        ref state,
                        pickable.ValueRO,
                        assignment);

                    // 释放所有搬运这个资源的 Ani
                    ReleaseCarrierAnis(ref state, ref entityCommandBuffer, resourceEntity, assignment);

                    // 销毁资源实体
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
                         DynamicBuffer<FoodAmountChangedEvent>,
                         DynamicBuffer<CrystalAmountChangedEvent>>()
                         .WithAll<ResourceEventHubTag>()
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
                        foodBuffer.Add(new FoodAmountChangedEvent
                        {
                            OwnerNetworkId = networkId,
                            Amount = totalAmount
                        });

                        break;

                    case ResourceItemKind.Crystal:
                        crystalBuffer.Add(new CrystalAmountChangedEvent
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

                // 去掉搬运指令
                entityCommandBuffer.RemoveComponent<AniCarryResourceOrder>(aniEntity);

                // 解锁命令：如果有 AniCommandLockedTag 就关掉
                if (SystemAPI.HasComponent<AniCommandLockedTag>(aniEntity))
                {
                    entityCommandBuffer.SetComponentEnabled<AniCommandLockedTag>(aniEntity, false);
                }

                // 搬运结束：切回跟随玩家
                if (SystemAPI.HasBuffer<FsmVar>(aniEntity))
                {
                    var blackboard = SystemAPI.GetBuffer<FsmVar>(aniEntity);

                        // 切换到 Follow 命令模式
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
