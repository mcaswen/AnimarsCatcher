using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 让 Ani 远距离跟随 Cohort Flow Direction，接近目标后转向自己的自然落点
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridFlowFieldSystem))]
    [UpdateBefore(typeof(AniMovementCommitSystem))]
    public partial struct AniFreePreferredVelocitySystem : ISystem
    {
        private ComponentLookup<AniMovementCohort> _cohortLookup;
        private ComponentLookup<AniMovementCohortPathState> _pathStateLookup;
        private ComponentLookup<NavigationFlowFieldState> _fieldStateLookup;
        private BufferLookup<NavigationFlowFieldCell> _fieldLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _cohortLookup = state.GetComponentLookup<AniMovementCohort>(true);
            _pathStateLookup = state.GetComponentLookup<AniMovementCohortPathState>(true);
            _fieldStateLookup = state.GetComponentLookup<NavigationFlowFieldState>(true);
            _fieldLookup = state.GetBufferLookup<NavigationFlowFieldCell>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _cohortLookup.Update(ref state);
            _pathStateLookup.Update(ref state);
            _fieldStateLookup.Update(ref state);
            _fieldLookup.Update(ref state);
            float deltaTime = SystemAPI.Time.DeltaTime;

            bool hasGrid = SystemAPI.TryGetSingleton(out NavigationGridReference gridReference) &&
                           gridReference.Value.IsCreated;
            // 成员只保存 Cohort 引用，共享 Field 在使用时通过 Lookup 读取
            foreach (var (transform, membership, config, goal, preferredVelocity) in
                     SystemAPI.Query<
                         RefRO<LocalTransform>,
                         RefRO<AniMovementCohortMembership>,
                         RefRO<AniMovementConfig>,
                         RefRO<AniGoalAssignment>,
                         RefRW<AniPreferredVelocity>>())
            {
                float3 targetVelocity = float3.zero;
                Entity cohortEntity = membership.ValueRO.Cohort;
                bool cohortReady = hasGrid &&
                                   _cohortLookup.HasComponent(cohortEntity) &&
                                   _pathStateLookup.HasComponent(cohortEntity) &&
                                   _fieldStateLookup.HasComponent(cohortEntity) &&
                                   _fieldLookup.HasBuffer(cohortEntity) &&
                                   _pathStateLookup[cohortEntity].Status !=
                                   AniMovementCohortStatus.Failed &&
                                   _fieldStateLookup[cohortEntity].Status ==
                                   NavigationPathStatus.Succeeded &&
                                   goal.ValueRO.TargetVersion ==
                                   _cohortLookup[cohortEntity].TargetVersion;
                if (cohortReady)
                {
                    ref NavigationGridBlob grid = ref gridReference.Value.Value;
                    float3 arrivalVelocity =
                        AniMovementCohortAlgorithms.CalculateArrivalVelocity(
                            transform.ValueRO.Position,
                            goal.ValueRO.TargetPosition,
                            config.ValueRO.MaxSpeed,
                            config.ValueRO.MaxAcceleration,
                            goal.ValueRO.ArrivalRadius);
                    float distanceToGoal = math.length(
                        PlanarMath.FlattenY(
                            goal.ValueRO.TargetPosition - transform.ValueRO.Position));

                    float3 flowDirection = float3.zero;
                    bool hasCurrentCell = NavigationGridQuery.TryWorldToCell(
                        ref grid,
                        transform.ValueRO.Position,
                        out _,
                        out int currentCellIndex);
                    if (hasCurrentCell)
                    {
                        AniMovementCohortAlgorithms.TryGetFlowDirection(
                            _fieldLookup[cohortEntity],
                            currentCellIndex,
                            out flowDirection);
                    }

                    bool canApproachDirectly = false;
                    // 直线检查只在目标影响范围内发生，远距离仍完全服从共享 Flow
                    if (hasCurrentCell &&
                        distanceToGoal <= goal.ValueRO.InfluenceRadius)
                    {
                        canApproachDirectly = NavigationGridQuery.TryCalculateLineCost(
                            ref grid,
                            currentCellIndex,
                            goal.ValueRO.TargetCellIndex,
                            config.ValueRO.AgentRadius,
                            0.05f,
                            0.2f,
                            out _);
                    }

                    targetVelocity = AniMovementCohortAlgorithms.BlendGoalVelocity(
                        flowDirection,
                        arrivalVelocity,
                        distanceToGoal,
                        goal.ValueRO.InfluenceRadius,
                        canApproachDirectly);
                }

                // 新速度仍受 Ani 自身加速度约束，切换个人落点时不会瞬间转向
                preferredVelocity.ValueRW.Value = VectorMath.MoveTowards(
                    preferredVelocity.ValueRO.Value,
                    targetVelocity,
                    math.max(0f, config.ValueRO.MaxAcceleration) * deltaTime);
            }
        }
    }

    /// <summary>
    /// 按 Cohort 归约成员到达状态，并同步 MovementOrder 的最终结果
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniMovementCommitSystem))]
    public partial struct AniFreeMovementProgressSystem : ISystem
    {
        private ComponentLookup<AniMovementResult> _resultLookup;
        private ComponentLookup<AniGoalAssignment> _goalLookup;
        private ComponentLookup<LocalTransform> _transformLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _resultLookup = state.GetComponentLookup<AniMovementResult>(true);
            _goalLookup = state.GetComponentLookup<AniGoalAssignment>(true);
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            _resultLookup.Update(ref state);
            _goalLookup.Update(ref state);
            _transformLookup.Update(ref state);

            foreach (var (target, pathState, fieldState, members) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohortTarget>,
                         RefRW<AniMovementCohortPathState>,
                         RefRO<NavigationFlowFieldState>,
                         DynamicBuffer<AniMovementCohortMember>>())
            {
                if (pathState.ValueRO.Status == AniMovementCohortStatus.Failed ||
                    pathState.ValueRO.Status == AniMovementCohortStatus.Completed ||
                    pathState.ValueRO.Status == AniMovementCohortStatus.Holding)
                {
                    continue;
                }

                if (fieldState.ValueRO.Status == NavigationPathStatus.Failed &&
                    fieldState.ValueRO.RequestVersion ==
                    pathState.ValueRO.ActiveRequestVersion)
                {
                    pathState.ValueRW.Status = AniMovementCohortStatus.Failed;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                // 成员检查只扫描当前 Cohort 的有界 Buffer，不会退化成全军两两比较
                if (fieldState.ValueRO.Status != NavigationPathStatus.Succeeded ||
                    !AreMembersSettled(members))
                {
                    pathState.ValueRW.Status = fieldState.ValueRO.Status ==
                                               NavigationPathStatus.Succeeded
                        ? AniMovementCohortStatus.Moving
                        : AniMovementCohortStatus.AwaitingPath;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                pathState.ValueRW.SettledTicks++;
                if (pathState.ValueRO.SettledTicks >= 5)
                {
                    pathState.ValueRW.Status = target.ValueRO.Mode ==
                                               AniSquadCommandMode.Follow
                        ? AniMovementCohortStatus.Holding
                        : AniMovementCohortStatus.Completed;
                }
            }

            // Cohort 先提交终态，订单随后只汇总小规模上下文状态
            UpdateOrderProgress(ref state);
        }

        private bool AreMembersSettled(DynamicBuffer<AniMovementCohortMember> members)
        {
            if (members.IsEmpty)
            {
                return false;
            }

            for (int index = 0; index < members.Length; index++)
            {
                Entity ani = members[index].Ani;
                if (!_resultLookup.HasComponent(ani) ||
                    !_goalLookup.HasComponent(ani) ||
                    !_transformLookup.HasComponent(ani))
                {
                    return false;
                }

                AniGoalAssignment goal = _goalLookup[ani];
                float distance = math.length(
                    PlanarMath.FlattenY(goal.TargetPosition - _transformLookup[ani].Position));
                if (distance > goal.ArrivalRadius ||
                    math.lengthsq(_resultLookup[ani].AppliedVelocity) > 0.0225f)
                {
                    return false;
                }
            }

            return true;
        }

        private void UpdateOrderProgress(ref SystemState state)
        {
            foreach (var (order, orderState, orderEntity) in
                     SystemAPI.Query<RefRO<AniMovementOrder>, RefRW<AniMovementOrderState>>()
                              .WithEntityAccess())
            {
                if (orderState.ValueRO.Status != AniMovementOrderStatus.Active)
                {
                    continue;
                }

                int cohortCount = 0;
                bool anyFailed = false;
                bool anyMoving = false;
                // Follow 的全部 Cohort 进入 Holding 后订单仍保持活动，目标移动可再次唤醒
                foreach (var (cohort, pathState) in
                         SystemAPI.Query<
                             RefRO<AniMovementCohort>,
                             RefRO<AniMovementCohortPathState>>())
                {
                    if (cohort.ValueRO.Order != orderEntity)
                    {
                        continue;
                    }

                    cohortCount++;
                    anyFailed |= pathState.ValueRO.Status == AniMovementCohortStatus.Failed;
                    anyMoving |= pathState.ValueRO.Status ==
                                 AniMovementCohortStatus.AwaitingPath ||
                                 pathState.ValueRO.Status == AniMovementCohortStatus.Moving;
                }

                if (anyFailed)
                {
                    orderState.ValueRW.Status = AniMovementOrderStatus.Failed;
                }
                else if (cohortCount == 0)
                {
                    orderState.ValueRW.Status = AniMovementOrderStatus.Superseded;
                }
                else if (!anyMoving && order.ValueRO.Mode != AniSquadCommandMode.Follow)
                {
                    orderState.ValueRW.Status = AniMovementOrderStatus.Completed;
                }
            }
        }
    }
}
