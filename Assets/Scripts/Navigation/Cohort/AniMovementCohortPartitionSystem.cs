using System.Collections.Generic;
using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将 MovementOrder 按通行配置和空间位置稳定拆成有界 Cohort
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(AniSquadLifecycleSystem))]
    public partial struct AniMovementCohortPartitionSystem : ISystem
    {
        private const ulong HashOffset = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;

        private EntityQuery _orderQuery;
        private EntityQuery _cohortQuery;
        private uint _nextCohortId;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _orderQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniMovementOrderRequest>(),
                ComponentType.ReadOnly<AniMovementOrder>(),
                ComponentType.ReadOnly<AniMovementOrderMember>());
            _cohortQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<AniMovementCohort>(),
                ComponentType.ReadWrite<AniMovementCohortPathState>(),
                ComponentType.ReadWrite<AniMovementCohortMember>());
            _nextCohortId = 1;
        }

        public void OnUpdate(ref SystemState state)
        {
            // 先回收死亡或换令成员，后续新订单不会继承失效归属
            CleanupCohorts(ref state);

            // Grid 未发布时保留订单请求，避免用临时坐标完成不可逆切分
            if (!SystemAPI.TryGetSingleton<NavigationGridReference>(
                    out NavigationGridReference gridReference) ||
                !gridReference.Value.IsCreated)
            {
                return;
            }

            AniMovementCohortSettings settings = default;
            SystemAPI.TryGetSingleton(out settings);
            // 运行配置只能收紧或放宽首选容量，不能突破代码中的安全硬上限
            int cohortCapacity = AniMovementCohortAlgorithms.ResolveMemberCapacity(settings);

            using NativeArray<Entity> orders = _orderQuery.ToEntityArray(Allocator.Temp);
            // 同一 Tick 的多个订单按服务器序号执行，最后一条重叠命令自然取得成员
            SortOrders(state.EntityManager, orders);
            for (int index = 0; index < orders.Length; index++)
            {
                if (state.EntityManager.Exists(orders[index]))
                {
                    ConsumeOrder(
                        ref state,
                        orders[index],
                        ref gridReference.Value.Value,
                        cohortCapacity);
                }
            }

            CleanupCohorts(ref state);
            RefreshOrderSummaries(ref state);
        }

        private void ConsumeOrder(
            ref SystemState state,
            Entity orderEntity,
            ref NavigationGridBlob grid,
            int cohortCapacity)
        {
            EntityManager entityManager = state.EntityManager;
            AniMovementOrder order = entityManager.GetComponentData<AniMovementOrder>(orderEntity);
            DynamicBuffer<AniMovementOrderMember> sourceMembers =
                entityManager.GetBuffer<AniMovementOrderMember>(orderEntity, true);
            using var members = new NativeList<PartitionMember>(
                math.max(1, sourceMembers.Length),
                Allocator.Temp);
            using var stableIds = new NativeParallelHashSet<int>(
                math.max(1, sourceMembers.Length),
                Allocator.Temp);

            // 任一重复编号或无效成员都会拒绝整单，不能悄悄形成部分 Cohort
            bool validOrder = VectorMath.IsFinite(order.TargetPosition);
            for (int index = 0; validOrder && index < sourceMembers.Length; index++)
            {
                AniMovementOrderMember source = sourceMembers[index];
                if (!stableIds.Add(source.GhostId) ||
                    !TryCreatePartitionMember(
                        entityManager,
                        source,
                        ref grid,
                        out PartitionMember member))
                {
                    validOrder = false;
                    break;
                }

                members.Add(member);
            }

            if (!validOrder || members.IsEmpty)
            {
                SetOrderFailed(entityManager, orderEntity, order.TargetPosition);
                entityManager.RemoveComponent<AniMovementOrderRequest>(orderEntity);
                return;
            }

            // 排序键只来自业务快照和 Grid，不依赖 EntityQuery 返回顺序
            members.Sort(new PartitionMemberComparer());
            ulong partitionHash = HashOffset;
            int cohortCount = 0;
            int rangeStart = 0;
            while (rangeStart < members.Length)
            {
                PartitionMember first = members[rangeStart];
                int rangeEnd = rangeStart + 1;
                // Profile 或起始 Cluster 改变时立即断组，避免共用不适合的路线入口
                while (rangeEnd < members.Length &&
                       rangeEnd - rangeStart < cohortCapacity &&
                       members[rangeEnd].AgentProfile == first.AgentProfile &&
                       members[rangeEnd].ClusterId == first.ClusterId)
                {
                    rangeEnd++;
                }

                Entity cohortEntity = CreateCohort(
                    ref state,
                    orderEntity,
                    order,
                    members,
                    rangeStart,
                    rangeEnd - rangeStart);
                uint cohortId = entityManager.GetComponentData<AniMovementCohort>(cohortEntity)
                    .CohortId;

                // 结构变更与 Buffer 写入分成两轮，避免成员换 Archetype 后使 Buffer 引用失效
                for (int memberIndex = rangeStart; memberIndex < rangeEnd; memberIndex++)
                {
                    PartitionMember member = members[memberIndex];
                    DetachFromPreviousMovement(ref state, member.Ani);
                    EnsureMovementComponents(
                        entityManager,
                        member,
                        cohortEntity,
                        cohortId);
                }

                DynamicBuffer<AniMovementCohortMember> cohortMembers =
                    entityManager.GetBuffer<AniMovementCohortMember>(cohortEntity);
                // Cohort Buffer 保留已经排序的快照，后续系统无需再次猜测成员顺序
                for (int memberIndex = rangeStart; memberIndex < rangeEnd; memberIndex++)
                {
                    PartitionMember member = members[memberIndex];
                    cohortMembers.Add(new AniMovementCohortMember
                    {
                        Ani = member.Ani,
                        StableId = member.StableId,
                    });

                    // Hash 不包含运行时 Entity，独立 World 可以直接比较切分结果
                    partitionHash = Mix(partitionHash, (uint)cohortCount);
                    partitionHash = Mix(partitionHash, (uint)member.StableId);
                    partitionHash = Mix(partitionHash, member.AgentProfile);
                    partitionHash = Mix(partitionHash, unchecked((uint)member.ClusterId));
                }

                cohortCount++;
                rangeStart = rangeEnd;
            }

            // Order Entity 在请求标记移除后继续保存意图和可诊断的执行摘要
            var orderState = new AniMovementOrderState
            {
                Status = AniMovementOrderStatus.Active,
                ValidMemberCount = members.Length,
                ActiveCohortCount = cohortCount,
                MemberVersion = 1,
                TargetVersion = 1,
                GoalAssignmentPending = 1,
                ResolvedTargetPosition = order.TargetPosition,
                GoalRegionCenterPosition = order.TargetPosition,
                CohortPartitionHash = partitionHash,
            };
            SetOrAdd(entityManager, orderEntity, orderState);
            entityManager.RemoveComponent<AniMovementOrderRequest>(orderEntity);
        }

        private Entity CreateCohort(
            ref SystemState state,
            Entity orderEntity,
            AniMovementOrder order,
            NativeList<PartitionMember> members,
            int startIndex,
            int count)
        {
            EntityManager entityManager = state.EntityManager;
            float3 center = float3.zero;
            float maximumRadius = 0f;
            float minimumSpeed = float.PositiveInfinity;
            float minimumAcceleration = float.PositiveInfinity;
            // Cohort 使用最保守的速度和体型，保证共享路径适用于每名成员
            for (int index = startIndex; index < startIndex + count; index++)
            {
                PartitionMember member = members[index];
                center += member.Position;
                maximumRadius = math.max(maximumRadius, member.AgentRadius);
                minimumSpeed = math.min(minimumSpeed, member.MaxSpeed);
                minimumAcceleration = math.min(minimumAcceleration, member.MaxAcceleration);
            }

            uint cohortId = NextCohortId();
            Entity cohortEntity = entityManager.CreateEntity(
                typeof(AniMovementCohort),
                typeof(AniMovementCohortTarget),
                typeof(AniMovementCohortPathState),
                typeof(NavigationFlowFieldRequest),
                typeof(NavigationFlowFieldState));
            entityManager.SetComponentData(cohortEntity, new AniMovementCohort
            {
                CohortId = cohortId,
                Order = orderEntity,
                OrderSequence = order.Sequence,
                OwnerNetworkId = order.OwnerNetworkId,
                AgentProfile = members[startIndex].AgentProfile,
                StartClusterId = members[startIndex].ClusterId,
                MemberCount = count,
                MemberVersion = 1,
                TargetVersion = 1,
                MaximumAgentRadius = maximumRadius,
                MinimumMaxSpeed = float.IsInfinity(minimumSpeed) ? 0f : minimumSpeed,
                MinimumMaxAcceleration = float.IsInfinity(minimumAcceleration)
                    ? 0f
                    : minimumAcceleration,
                RepresentativePosition = center / count,
            });
            entityManager.SetComponentData(cohortEntity, new AniMovementCohortTarget
            {
                Mode = order.Mode,
                TargetEntity = order.TargetEntity,
                TargetStoppingDistance = order.TargetStoppingDistance,
            });
            entityManager.SetComponentData(cohortEntity, new AniMovementCohortPathState
            {
                Status = AniMovementCohortStatus.AwaitingPath,
                ResolvedTargetPosition = order.TargetPosition,
            });
            entityManager.SetComponentData(
                cohortEntity,
                NavigationFlowFieldState.CreatePending(0));
            entityManager.AddBuffer<AniMovementCohortMember>(cohortEntity);
            entityManager.AddBuffer<NavigationCorridorCluster>(cohortEntity);
            entityManager.AddBuffer<NavigationCorridorPortal>(cohortEntity);
            entityManager.AddBuffer<NavigationHierarchicalWaypoint>(cohortEntity);
            // 6A.3 上线共享 Store 前仍沿用现有 Flow 结果 Buffer，所有权边界保持清晰
            entityManager.AddBuffer<NavigationFlowFieldCell>(cohortEntity);
            return cohortEntity;
        }

        private static bool TryCreatePartitionMember(
            EntityManager entityManager,
            AniMovementOrderMember source,
            ref NavigationGridBlob grid,
            out PartitionMember member)
        {
            member = default;
            if (source.Ani == Entity.Null ||
                !entityManager.Exists(source.Ani) ||
                !entityManager.HasComponent<LocalTransform>(source.Ani) ||
                source.AgentProfile == 0 ||
                source.GhostId <= 0 ||
                !math.isfinite(source.MaxSpeed) ||
                !math.isfinite(source.MaxAcceleration) ||
                !math.isfinite(source.AgentRadius) ||
                source.MaxSpeed < 0f ||
                source.MaxAcceleration <= 0f ||
                source.AgentRadius <= 0f)
            {
                return false;
            }

            float3 position = entityManager.GetComponentData<LocalTransform>(source.Ani).Position;
            if (!VectorMath.IsFinite(position))
            {
                return false;
            }

            // 起点不在可站立 Cell 时允许有限投影，超出范围则整单失败
            if (!NavigationGridQuery.TryWorldToCell(
                    ref grid,
                    position,
                    out int2 coordinate,
                    out int cellIndex) ||
                !NavigationGridQuery.CanAgentOccupy(
                    ref grid,
                    cellIndex,
                    source.AgentRadius,
                    0.05f))
            {
                if (!NavigationGridQuery.TryProjectToNearestCell(
                        ref grid,
                        position,
                        source.AgentRadius,
                        0.05f,
                        16,
                        out cellIndex))
                {
                    return false;
                }

                coordinate = new int2(cellIndex % grid.Width, cellIndex / grid.Width);
            }

            member = new PartitionMember
            {
                Ani = source.Ani,
                StableId = source.GhostId,
                AgentProfile = source.AgentProfile,
                AgentRadius = source.AgentRadius,
                MaxSpeed = source.MaxSpeed,
                MaxAcceleration = source.MaxAcceleration,
                ClusterId = grid.Cells[cellIndex].ClusterId,
                MortonKey = AniMovementCohortAlgorithms.CalculateMortonKey(coordinate),
                Position = position,
            };
            return true;
        }

        private void CleanupCohorts(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;
            using NativeArray<Entity> cohorts = _cohortQuery.ToEntityArray(Allocator.Temp);
            // Cohort 成员上限受控，清理只在小 Buffer 内执行，不会退化成全局两两扫描
            for (int cohortIndex = 0; cohortIndex < cohorts.Length; cohortIndex++)
            {
                Entity cohortEntity = cohorts[cohortIndex];
                if (!entityManager.Exists(cohortEntity))
                {
                    continue;
                }

                DynamicBuffer<AniMovementCohortMember> members =
                    entityManager.GetBuffer<AniMovementCohortMember>(cohortEntity);
                bool changed = false;
                for (int memberIndex = members.Length - 1; memberIndex >= 0; memberIndex--)
                {
                    Entity ani = members[memberIndex].Ani;
                    // Membership 和 Cohort Buffer 必须双向一致，单边残留也按失效处理
                    bool valid = entityManager.Exists(ani) &&
                                 entityManager.HasComponent<LocalTransform>(ani) &&
                                 entityManager.HasComponent<AniMovementConfig>(ani) &&
                                 entityManager.HasComponent<AniMovementCohortMembership>(ani) &&
                                 entityManager.GetComponentData<AniMovementCohortMembership>(ani)
                                     .Cohort == cohortEntity;
                    if (valid)
                    {
                        continue;
                    }

                    members.RemoveAt(memberIndex);
                    changed = true;
                }

                AniMovementCohort cohort =
                    entityManager.GetComponentData<AniMovementCohort>(cohortEntity);
                if (members.IsEmpty)
                {
                    MarkOrderMembersChanged(entityManager, cohort.Order);
                    entityManager.DestroyEntity(cohortEntity);
                    continue;
                }

                // 代表位置随剩余成员更新，动态重规划会从当前群体位置重新出发
                float3 center = float3.zero;
                float maximumRadius = 0f;
                float minimumSpeed = float.PositiveInfinity;
                float minimumAcceleration = float.PositiveInfinity;
                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    Entity ani = members[memberIndex].Ani;
                    AniMovementConfig config =
                        entityManager.GetComponentData<AniMovementConfig>(ani);
                    center += entityManager.GetComponentData<LocalTransform>(ani).Position;
                    maximumRadius = math.max(maximumRadius, config.AgentRadius);
                    minimumSpeed = math.min(minimumSpeed, config.MaxSpeed);
                    minimumAcceleration = math.min(minimumAcceleration, config.MaxAcceleration);
                }

                cohort.MemberCount = members.Length;
                cohort.RepresentativePosition = center / members.Length;
                cohort.MaximumAgentRadius = maximumRadius;
                cohort.MinimumMaxSpeed = float.IsInfinity(minimumSpeed) ? 0f : minimumSpeed;
                cohort.MinimumMaxAcceleration = float.IsInfinity(minimumAcceleration)
                    ? 0f
                    : minimumAcceleration;
                if (changed)
                {
                    // 成员版本同时驱动目标区域重分配，死亡后不会永久占用旧落点
                    cohort.MemberVersion = NextNonZero(cohort.MemberVersion);
                    MarkOrderMembersChanged(entityManager, cohort.Order);
                }

                entityManager.SetComponentData(cohortEntity, cohort);
            }
        }

        private static void EnsureMovementComponents(
            EntityManager entityManager,
            PartitionMember member,
            Entity cohortEntity,
            uint cohortId)
        {
            if (entityManager.HasComponent<AniSquadMembership>(member.Ani))
            {
                // 正式 Cohort 接管成员时移除严格阵型归属，两个 Pipeline 不会双写 Transform
                entityManager.RemoveComponent<AniSquadMembership>(member.Ani);
            }
            if (entityManager.HasComponent<AniSlotTarget>(member.Ani))
            {
                entityManager.RemoveComponent<AniSlotTarget>(member.Ani);
            }

            SetOrAdd(entityManager, member.Ani, new AniMovementCohortMembership
            {
                Cohort = cohortEntity,
                CohortId = cohortId,
                StableId = member.StableId,
                AgentProfile = member.AgentProfile,
            });
            SetOrAdd(entityManager, member.Ani, new AniMovementConfig
            {
                MaxSpeed = member.MaxSpeed,
                MaxAcceleration = member.MaxAcceleration,
                AgentRadius = member.AgentRadius,
                ArrivalRadius = math.max(0.1f, member.AgentRadius * 0.75f),
                RotationSpeedRadians = math.radians(540f),
            });
            SetOrAdd(entityManager, member.Ani, new AniPreferredVelocity());
            SetOrAdd(entityManager, member.Ani, new AniGoalAssignment());
            if (!entityManager.HasComponent<AniMovementResult>(member.Ani))
            {
                entityManager.AddComponentData(member.Ani, new AniMovementResult());
            }
            // 已有 MovementResult 不重置，CommitCount 可以跨命令证明唯一写入边界
        }

        private static void DetachFromPreviousMovement(ref SystemState state, Entity ani)
        {
            EntityManager entityManager = state.EntityManager;
            if (!entityManager.HasComponent<AniMovementCohortMembership>(ani))
            {
                return;
            }

            AniMovementCohortMembership membership =
                entityManager.GetComponentData<AniMovementCohortMembership>(ani);
            Entity oldCohort = membership.Cohort;
            // 先从旧 Buffer 脱离再替换 Membership，新命令不会留下重复所有权
            if (oldCohort != Entity.Null &&
                entityManager.Exists(oldCohort) &&
                entityManager.HasBuffer<AniMovementCohortMember>(oldCohort))
            {
                DynamicBuffer<AniMovementCohortMember> members =
                    entityManager.GetBuffer<AniMovementCohortMember>(oldCohort);
                for (int index = members.Length - 1; index >= 0; index--)
                {
                    if (members[index].Ani == ani)
                    {
                        members.RemoveAt(index);
                        break;
                    }
                }

                if (entityManager.HasComponent<AniMovementCohort>(oldCohort))
                {
                    AniMovementCohort cohort =
                        entityManager.GetComponentData<AniMovementCohort>(oldCohort);
                    cohort.MemberCount = members.Length;
                    cohort.MemberVersion = NextNonZero(cohort.MemberVersion);
                    entityManager.SetComponentData(oldCohort, cohort);
                    MarkOrderMembersChanged(entityManager, cohort.Order);
                }
            }

            entityManager.RemoveComponent<AniMovementCohortMembership>(ani);
        }

        private void RefreshOrderSummaries(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;
            int cohortCount = math.max(
                1,
                SystemAPI.QueryBuilder().WithAll<AniMovementCohort>().Build()
                    .CalculateEntityCount());
            var counts = new NativeParallelHashMap<Entity, int>(
                cohortCount,
                Allocator.Temp);
            var memberCounts = new NativeParallelHashMap<Entity, int>(
                cohortCount,
                Allocator.Temp);
            // 汇总使用订单 Entity 作为键，成本随 Cohort 数而不是成员总数增长
            foreach (RefRO<AniMovementCohort> cohort in
                     SystemAPI.Query<RefRO<AniMovementCohort>>())
            {
                Entity order = cohort.ValueRO.Order;
                if (counts.TryGetValue(order, out int currentCount))
                {
                    counts[order] = currentCount + 1;
                    memberCounts.TryGetValue(order, out int currentMemberCount);
                    memberCounts[order] = currentMemberCount + cohort.ValueRO.MemberCount;
                }
                else
                {
                    counts.TryAdd(order, 1);
                    memberCounts.TryAdd(order, cohort.ValueRO.MemberCount);
                }
            }

            foreach (var (orderState, orderEntity) in
                     SystemAPI.Query<RefRW<AniMovementOrderState>>().WithEntityAccess())
            {
                counts.TryGetValue(orderEntity, out int activeCohorts);
                memberCounts.TryGetValue(orderEntity, out int activeMembers);
                orderState.ValueRW.ActiveCohortCount = activeCohorts;
                orderState.ValueRW.ValidMemberCount = activeMembers;
                if (activeCohorts == 0 &&
                    orderState.ValueRO.Status == AniMovementOrderStatus.Active)
                {
                    // 活动订单失去全部 Cohort 说明已被新命令覆盖，而不是正常到达
                    orderState.ValueRW.Status = AniMovementOrderStatus.Superseded;
                    orderState.ValueRW.GoalAssignmentPending = 0;
                }
            }

            counts.Dispose();
            memberCounts.Dispose();
        }

        private static void MarkOrderMembersChanged(EntityManager entityManager, Entity order)
        {
            if (order == Entity.Null ||
                !entityManager.Exists(order) ||
                !entityManager.HasComponent<AniMovementOrderState>(order))
            {
                return;
            }

            AniMovementOrderState orderState =
                entityManager.GetComponentData<AniMovementOrderState>(order);
            orderState.MemberVersion = NextNonZero(orderState.MemberVersion);
            orderState.GoalAssignmentPending = 1;
            entityManager.SetComponentData(order, orderState);
        }

        private static void SetOrderFailed(
            EntityManager entityManager,
            Entity orderEntity,
            float3 targetPosition)
        {
            SetOrAdd(entityManager, orderEntity, new AniMovementOrderState
            {
                Status = AniMovementOrderStatus.Failed,
                ResolvedTargetPosition = targetPosition,
            });
        }

        private static void SortOrders(EntityManager entityManager, NativeArray<Entity> orders)
        {
            for (int index = 1; index < orders.Length; index++)
            {
                Entity value = orders[index];
                uint valueSequence = entityManager.GetComponentData<AniMovementOrder>(value).Sequence;
                int insertionIndex = index - 1;
                while (insertionIndex >= 0)
                {
                    Entity previous = orders[insertionIndex];
                    uint previousSequence =
                        entityManager.GetComponentData<AniMovementOrder>(previous).Sequence;
                    bool previousAfter = previousSequence > valueSequence ||
                                         (previousSequence == valueSequence &&
                                          IsEntityAfter(previous, value));
                    if (!previousAfter)
                    {
                        break;
                    }

                    orders[insertionIndex + 1] = previous;
                    insertionIndex--;
                }

                orders[insertionIndex + 1] = value;
            }
        }

        private static bool IsEntityAfter(Entity left, Entity right)
        {
            return left.Index > right.Index ||
                   (left.Index == right.Index && left.Version > right.Version);
        }

        private uint NextCohortId()
        {
            uint value = _nextCohortId++;
            if (_nextCohortId == 0)
            {
                _nextCohortId = 1;
            }

            return value == 0 ? NextCohortId() : value;
        }

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * HashPrime;
        }

        private static void SetOrAdd<T>(EntityManager entityManager, Entity entity, T value)
            where T : unmanaged, IComponentData
        {
            if (entityManager.HasComponent<T>(entity))
            {
                entityManager.SetComponentData(entity, value);
            }
            else
            {
                entityManager.AddComponentData(entity, value);
            }
        }

        private struct PartitionMember
        {
            public Entity Ani;
            public int StableId;
            public uint AgentProfile;
            public float AgentRadius;
            public float MaxSpeed;
            public float MaxAcceleration;
            public int ClusterId;
            public ulong MortonKey;
            public float3 Position;
        }

        private struct PartitionMemberComparer : IComparer<PartitionMember>
        {
            public int Compare(PartitionMember left, PartitionMember right)
            {
                int comparison = left.AgentProfile.CompareTo(right.AgentProfile);
                if (comparison != 0) return comparison;
                comparison = left.ClusterId.CompareTo(right.ClusterId);
                if (comparison != 0) return comparison;
                comparison = left.MortonKey.CompareTo(right.MortonKey);
                if (comparison != 0) return comparison;
                return left.StableId.CompareTo(right.StableId);
            }
        }
    }
}
