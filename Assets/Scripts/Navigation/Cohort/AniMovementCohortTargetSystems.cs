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
    /// 解析 MovementOrder 的固定或动态目标，并通知 Cohort 更新目标版本
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniMovementCohortPartitionSystem))]
    [UpdateBefore(typeof(AniGoalRegionAssignmentSystem))]
    public partial struct AniCohortTargetResolveSystem : ISystem
    {
        private ComponentLookup<LocalTransform> _transformLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            _transformLookup.Update(ref state);
            float cellSize = 1f;
            if (SystemAPI.TryGetSingleton(out NavigationGridReference gridReference) &&
                gridReference.Value.IsCreated)
            {
                cellSize = math.max(0.1f, gridReference.Value.Value.CellSize);
            }

            foreach (var (order, orderState, orderEntity) in
                     SystemAPI.Query<RefRO<AniMovementOrder>, RefRW<AniMovementOrderState>>()
                              .WithEntityAccess())
            {
                if (orderState.ValueRO.Status != AniMovementOrderStatus.Active)
                {
                    continue;
                }

                float3 targetPosition = order.ValueRO.TargetPosition;
                if (order.ValueRO.Mode != AniSquadCommandMode.MoveTo)
                {
                    Entity targetEntity = order.ValueRO.TargetEntity;
                    if (targetEntity == Entity.Null || !_transformLookup.HasComponent(targetEntity))
                    {
                        FailOrderAndCohorts(ref state, orderEntity);
                        continue;
                    }

                    targetPosition = _transformLookup[targetEntity].Position;
                }

                if (!VectorMath.IsFinite(targetPosition))
                {
                    FailOrderAndCohorts(ref state, orderEntity);
                    continue;
                }

                bool goalRegionMoved = math.distancesq(
                    targetPosition,
                    orderState.ValueRO.GoalRegionCenterPosition) >= cellSize * cellSize;
                orderState.ValueRW.ResolvedTargetPosition = targetPosition;
                if (goalRegionMoved)
                {
                    // 动态目标至少跨过一个 Cell 才重分配落点，过滤格子内的微小抖动
                    orderState.ValueRW.TargetVersion = NextNonZero(
                        orderState.ValueRO.TargetVersion);
                    orderState.ValueRW.GoalAssignmentPending = 1;
                }

                // 同一订单的 Cohort 共享目标版本，迟到的旧 Field 不会重新激活成员
                foreach (var (cohort, pathState) in
                         SystemAPI.Query<
                             RefRW<AniMovementCohort>,
                             RefRW<AniMovementCohortPathState>>())
                {
                    if (cohort.ValueRO.Order != orderEntity)
                    {
                        continue;
                    }

                    cohort.ValueRW.TargetVersion = orderState.ValueRO.TargetVersion;
                    pathState.ValueRW.ResolvedTargetPosition = targetPosition;
                    if (goalRegionMoved &&
                        pathState.ValueRO.Status != AniMovementCohortStatus.Failed)
                    {
                        pathState.ValueRW.Status = AniMovementCohortStatus.AwaitingPath;
                    }
                }
            }
        }

        private void FailOrderAndCohorts(ref SystemState state, Entity orderEntity)
        {
            AniMovementOrderState orderState =
                state.EntityManager.GetComponentData<AniMovementOrderState>(orderEntity);
            orderState.Status = AniMovementOrderStatus.Failed;
            orderState.GoalAssignmentPending = 0;
            state.EntityManager.SetComponentData(orderEntity, orderState);

            foreach (var (cohort, pathState) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohort>,
                         RefRW<AniMovementCohortPathState>>())
            {
                if (cohort.ValueRO.Order == orderEntity)
                {
                    pathState.ValueRW.Status = AniMovementCohortStatus.Failed;
                }
            }
        }

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }
    }

    /// <summary>
    /// 为 MovementOrder 生成可容纳全体成员的自然目标区域并分配唯一落点
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniCohortTargetResolveSystem))]
    [UpdateBefore(typeof(AniMovementCohortPathRequestSystem))]
    public partial struct AniGoalRegionAssignmentSystem : ISystem
    {
        private const ulong HashOffset = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton(out NavigationGridReference gridReference) ||
                !gridReference.Value.IsCreated)
            {
                return;
            }

            Entity gridEntity = SystemAPI.GetSingletonEntity<NavigationGridReference>();
            NativeArray<NavigationDynamicOverlayCell> overlay = default;
            // 目标区域使用当前只读 Overlay，动态阻挡 Cell 不会取得新落点
            if (state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity))
            {
                overlay = state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(
                    gridEntity,
                    true).AsNativeArray();
            }

            foreach (var (order, orderState, orderEntity) in
                     SystemAPI.Query<RefRO<AniMovementOrder>, RefRW<AniMovementOrderState>>()
                              .WithEntityAccess())
            {
                if (orderState.ValueRO.Status != AniMovementOrderStatus.Active ||
                    orderState.ValueRO.GoalAssignmentPending == 0)
                {
                    continue;
                }

                AssignOrderGoals(
                    ref state,
                    orderEntity,
                    order.ValueRO,
                    ref orderState.ValueRW,
                    ref gridReference.Value.Value,
                    overlay);
            }
        }

        private void AssignOrderGoals(
            ref SystemState state,
            Entity orderEntity,
            AniMovementOrder order,
            ref AniMovementOrderState orderState,
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay)
        {
            EntityManager entityManager = state.EntityManager;
            using var members = new NativeList<GoalMember>(
                math.max(1, orderState.ValidMemberCount),
                Allocator.Temp);
            float maximumRadius = 0f;
            bool membersValid = true;

            // 先收集整份订单而不是逐 Cohort 分配，多个 Cohort 不会重复占用同一落点
            foreach (var (cohort, cohortMembers) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohort>,
                         DynamicBuffer<AniMovementCohortMember>>())
            {
                if (cohort.ValueRO.Order != orderEntity)
                {
                    continue;
                }

                for (int index = 0; index < cohortMembers.Length; index++)
                {
                    AniMovementCohortMember cohortMember = cohortMembers[index];
                    Entity ani = cohortMember.Ani;
                    if (!entityManager.Exists(ani) ||
                        !entityManager.HasComponent<LocalTransform>(ani) ||
                        !entityManager.HasComponent<AniMovementConfig>(ani) ||
                        !entityManager.HasComponent<AniGoalAssignment>(ani))
                    {
                        membersValid = false;
                        break;
                    }

                    LocalTransform transform =
                        entityManager.GetComponentData<LocalTransform>(ani);
                    AniMovementConfig config =
                        entityManager.GetComponentData<AniMovementConfig>(ani);
                    if (!VectorMath.IsFinite(transform.Position) ||
                        !math.isfinite(config.AgentRadius) ||
                        config.AgentRadius <= 0f)
                    {
                        membersValid = false;
                        break;
                    }

                    int spatialCell = 0;
                    if (NavigationGridQuery.TryWorldToCell(
                            ref grid,
                            transform.Position,
                            out int2 coordinate,
                            out _))
                    {
                        ulong morton = AniMovementCohortAlgorithms.CalculateMortonKey(coordinate);
                        spatialCell = unchecked((int)(morton ^ (morton >> 32)));
                    }

                    float3 offset = PlanarMath.FlattenY(
                        transform.Position - orderState.ResolvedTargetPosition);
                    members.Add(new GoalMember
                    {
                        Ani = ani,
                        StableId = cohortMember.StableId,
                        Angle = math.atan2(offset.z, offset.x),
                        SpatialKey = spatialCell,
                        AgentRadius = config.AgentRadius,
                    });
                    maximumRadius = math.max(maximumRadius, config.AgentRadius);
                }

                if (!membersValid)
                {
                    break;
                }
            }

            if (!membersValid || members.IsEmpty)
            {
                FailGoalAssignment(ref state, orderEntity, ref orderState);
                return;
            }

            members.Sort(new GoalMemberComparer());
            // 目标中心先投影到可站立 Cell，地图外点击不会被无界夹到远处
            if (!NavigationGridQuery.TryProjectToNearestCell(
                    ref grid,
                    orderState.ResolvedTargetPosition,
                    maximumRadius,
                    0.05f,
                    math.max(grid.Width, grid.Height),
                    overlay,
                    out int centerCellIndex))
            {
                FailGoalAssignment(ref state, orderEntity, ref orderState);
                return;
            }

            float3 centerPosition = NavigationGridQuery.GetCellWorldPosition(
                ref grid,
                centerCellIndex);
            using var candidates = new NativeList<GoalCellCandidate>(
                math.max(1, grid.Cells.Length),
                Allocator.Temp);
            // 候选生成只线性扫描 Grid，随后排序，不建立成员乘落点的成本矩阵
            for (int cellIndex = 0; cellIndex < grid.Cells.Length; cellIndex++)
            {
                if (!NavigationGridTraversal.CanAgentOccupyDynamic(
                        ref grid,
                        cellIndex,
                        maximumRadius,
                        0.05f,
                        overlay))
                {
                    continue;
                }

                int capacity = AniMovementCohortAlgorithms.CalculateCellCapacity(
                    grid.CellSize,
                    maximumRadius,
                    order.GoalCellCapacityScale,
                    out int slotsPerAxis);
                float3 cellPosition = NavigationGridQuery.GetCellWorldPosition(
                    ref grid,
                    cellIndex);
                candidates.Add(new GoalCellCandidate
                {
                    CellIndex = cellIndex,
                    Capacity = capacity,
                    SlotsPerAxis = slotsPerAxis,
                    DistanceSquared = math.lengthsq(
                        PlanarMath.FlattenY(cellPosition - centerPosition)),
                    TerrainCost = grid.Cells[cellIndex].TerrainCost,
                    Clearance = grid.Cells[cellIndex].Clearance,
                });
            }

            candidates.Sort(new GoalCellCandidateComparer());
            int availableCapacity = 0;
            // 分配前确认总容量，避免写到一半才发现剩余成员没有合法位置
            for (int index = 0; index < candidates.Length && availableCapacity < members.Length; index++)
            {
                availableCapacity += candidates[index].Capacity;
            }
            if (availableCapacity < members.Length)
            {
                FailGoalAssignment(ref state, orderEntity, ref orderState);
                return;
            }

            ulong goalHash = HashOffset;
            int candidateIndex = 0;
            int slotIndex = 0;
            // 两个有序列表线性配对，成员数量增长不会引入平方级匹配成本
            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
            {
                while (slotIndex >= candidates[candidateIndex].Capacity)
                {
                    candidateIndex++;
                    slotIndex = 0;
                }

                GoalMember member = members[memberIndex];
                GoalCellCandidate candidate = candidates[candidateIndex];
                float3 cellPosition = NavigationGridQuery.GetCellWorldPosition(
                    ref grid,
                    candidate.CellIndex);
                float3 goalPosition = AniMovementCohortAlgorithms.CalculateGoalPosition(
                    cellPosition,
                    grid.CellSize,
                    slotIndex,
                    candidate.SlotsPerAxis);
                float distanceFromCenter = math.length(
                    PlanarMath.FlattenY(goalPosition - centerPosition));
                AniMovementConfig config =
                    entityManager.GetComponentData<AniMovementConfig>(member.Ani);
                entityManager.SetComponentData(member.Ani, new AniGoalAssignment
                {
                    TargetCellIndex = candidate.CellIndex,
                    TargetPosition = goalPosition,
                    ArrivalRadius = math.max(0.1f, math.min(
                        config.ArrivalRadius,
                        math.max(0.1f, order.TargetStoppingDistance))),
                    InfluenceRadius = math.max(
                        math.max(grid.CellSize * 2f, order.GoalInfluenceRadius),
                        distanceFromCenter + grid.CellSize * 2f),
                    TargetVersion = orderState.TargetVersion,
                });

                // 落点 Hash 只使用 StableId、Cell 和 Cell 内位置序号
                goalHash = Mix(goalHash, (uint)member.StableId);
                goalHash = Mix(goalHash, unchecked((uint)candidate.CellIndex));
                goalHash = Mix(goalHash, unchecked((uint)slotIndex));
                slotIndex++;
            }

            foreach (RefRW<AniMovementCohort> cohort in
                     SystemAPI.Query<RefRW<AniMovementCohort>>())
            {
                if (cohort.ValueRO.Order == orderEntity)
                {
                    cohort.ValueRW.TargetVersion = orderState.TargetVersion;
                }
            }

            orderState.GoalRegionHash = goalHash;
            // 记录实际投影中心，动态目标跨 Cell 后才会再次触发本系统
            orderState.GoalRegionCenterPosition = centerPosition;
            orderState.GoalAssignmentPending = 0;
        }

        private void FailGoalAssignment(
            ref SystemState state,
            Entity orderEntity,
            ref AniMovementOrderState orderState)
        {
            orderState.Status = AniMovementOrderStatus.Failed;
            // 目标容量不足属于整份订单失败，不能只让部分 Cohort 继续挤向中心
            orderState.GoalAssignmentPending = 0;
            foreach (var (cohort, pathState) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohort>,
                         RefRW<AniMovementCohortPathState>>())
            {
                if (cohort.ValueRO.Order == orderEntity)
                {
                    pathState.ValueRW.Status = AniMovementCohortStatus.Failed;
                }
            }
        }

        private static ulong Mix(ulong hash, uint value)
        {
            hash ^= value;
            return hash * HashPrime;
        }

        private struct GoalMember
        {
            public Entity Ani;
            public int StableId;
            public float Angle;
            public int SpatialKey;
            public float AgentRadius;
        }

        private struct GoalMemberComparer : IComparer<GoalMember>
        {
            public int Compare(GoalMember left, GoalMember right)
            {
                int comparison = left.Angle.CompareTo(right.Angle);
                if (comparison != 0) return comparison;
                comparison = left.SpatialKey.CompareTo(right.SpatialKey);
                if (comparison != 0) return comparison;
                return left.StableId.CompareTo(right.StableId);
            }
        }

        private struct GoalCellCandidate
        {
            public int CellIndex;
            public int Capacity;
            public int SlotsPerAxis;
            public float DistanceSquared;
            public float TerrainCost;
            public float Clearance;
        }

        private struct GoalCellCandidateComparer : IComparer<GoalCellCandidate>
        {
            public int Compare(GoalCellCandidate left, GoalCellCandidate right)
            {
                int comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
                if (comparison != 0) return comparison;
                comparison = left.TerrainCost.CompareTo(right.TerrainCost);
                if (comparison != 0) return comparison;
                comparison = right.Clearance.CompareTo(left.Clearance);
                if (comparison != 0) return comparison;
                return left.CellIndex.CompareTo(right.CellIndex);
            }
        }
    }
}
