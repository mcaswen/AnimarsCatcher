using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    // 每个工作线程只写当前 Ani 的期望速度，共享导航数据全部通过只读组件索引访问
    [BurstCompile]
    internal partial struct AniFreePreferredVelocityJob : IJobEntity
    {
        public float DeltaTime;
        public byte HasGrid;
        public NavigationGridReference GridReference;

        [ReadOnly]
        public ComponentLookup<AniMovementCohort> CohortLookup;

        [ReadOnly]
        public ComponentLookup<AniMovementCohortPathState> PathStateLookup;

        [ReadOnly]
        public ComponentLookup<NavigationFlowFieldState> FieldStateLookup;

        [ReadOnly]
        public ComponentLookup<NavigationFlowFieldHandle> FieldHandleLookup;

        [ReadOnly]
        public BufferLookup<NavigationFlowFieldCell> FieldLookup;

        public void Execute(
            in LocalTransform transform,
            in AniMovementCohortMembership membership,
            in AniMovementConfig config,
            ref AniGoalAssignment goal,
            ref AniPreferredVelocity preferredVelocity)
        {
            float3 targetVelocity = float3.zero;
            Entity cohortEntity = membership.Cohort;
            AniMovementCohortPathState pathState =
                PathStateLookup.HasComponent(cohortEntity)
                    ? PathStateLookup[cohortEntity]
                    : default;
            bool directRoute = pathState.RouteMode == AniMovementCohortRouteMode.Direct;
            Entity fieldEntity = FieldHandleLookup.HasComponent(cohortEntity)
                ? FieldHandleLookup[cohortEntity].Record
                : cohortEntity;
            bool fieldReady = fieldEntity != Entity.Null &&
                              FieldLookup.HasBuffer(fieldEntity) &&
                              FieldStateLookup.HasComponent(cohortEntity) &&
                              FieldStateLookup[cohortEntity].Status ==
                              NavigationPathStatus.Succeeded;
            bool cohortReady = HasGrid != 0 &&
                               CohortLookup.HasComponent(cohortEntity) &&
                               PathStateLookup.HasComponent(cohortEntity) &&
                               pathState.Status != AniMovementCohortStatus.Failed &&
                               (directRoute || fieldReady) &&
                               goal.TargetVersion == CohortLookup[cohortEntity].TargetVersion;
            if (cohortReady)
            {
                float3 arrivalVelocity = AniMovementCohortAlgorithms.CalculateArrivalVelocity(
                    transform.Position,
                    goal.TargetPosition,
                    config.MaxSpeed,
                    config.MaxAcceleration,
                    goal.ArrivalRadius);
                float distanceToGoal = math.length(
                    PlanarMath.FlattenY(goal.TargetPosition - transform.Position));

                if (directRoute)
                {
                    // Cohort 已在目标换代时验证整条路线，本 Tick 只需处理制动和加速度
                    targetVelocity = arrivalVelocity;
                    preferredVelocity.Value = VectorMath.MoveTowards(
                        preferredVelocity.Value,
                        targetVelocity,
                        math.max(0f, config.MaxAcceleration) * DeltaTime);
                    return;
                }

                ref NavigationGridBlob grid = ref GridReference.Value.Value;
                float3 flowDirection = float3.zero;
                bool hasCurrentCell = NavigationGridQuery.TryWorldToCell(
                    ref grid,
                    transform.Position,
                    out _,
                    out int currentCellIndex);
                bool hasFlowDirection = false;
                if (hasCurrentCell)
                {
                    hasFlowDirection = AniMovementCohortAlgorithms.TryGetFlowDirection(
                                           FieldLookup[fieldEntity],
                                           currentCellIndex,
                                           out flowDirection) &&
                                       math.lengthsq(flowDirection) > 1e-6f;
                    if (hasFlowDirection)
                    {
                        // 朝下一 Cell 中心修正离散方向，避免单位从格子边缘漂入相邻障碍
                        float3 currentCellCenter = NavigationGridQuery.GetCellWorldPosition(
                            ref grid,
                            currentCellIndex);
                        float3 nextCellCenter = currentCellCenter + new float3(
                            math.sign(flowDirection.x) * grid.CellSize,
                            0f,
                            math.sign(flowDirection.z) * grid.CellSize);
                        flowDirection = PlanarMath.NormalizeXZOrDefault(
                            nextCellCenter - transform.Position,
                            flowDirection);
                    }
                }
                if (hasCurrentCell &&
                    !hasFlowDirection &&
                    NavigationGridQuery.TryProjectToNearestCell(
                        ref grid,
                        transform.Position,
                        config.AgentRadius,
                        0.05f,
                        4,
                        out int recoveryCellIndex) &&
                    recoveryCellIndex != currentCellIndex &&
                    AniMovementCohortAlgorithms.TryGetFlowDirection(
                        FieldLookup[fieldEntity],
                        recoveryCellIndex,
                        out _))
                {
                    // 偶发漂入障碍 Cell 时先返回最近的 Field Cell，避免零方向永久停住
                    float3 recoveryPosition = NavigationGridQuery.GetCellWorldPosition(
                        ref grid,
                        recoveryCellIndex);
                    flowDirection = PlanarMath.NormalizeXZOrDefault(
                        recoveryPosition - transform.Position,
                        float3.zero);
                    hasFlowDirection = math.lengthsq(flowDirection) > 1e-6f;
                }

                bool canApproachDirectly = goal.DirectApproach != 0;
                // 常规直线检查限制在目标影响范围，离开稀疏 Field 时则用直达路径脱离零速度死区
                if (!canApproachDirectly &&
                    hasCurrentCell &&
                    (distanceToGoal <= goal.InfluenceRadius || !hasFlowDirection))
                {
                    canApproachDirectly = NavigationGridQuery.TryCalculateLineCost(
                        ref grid,
                        currentCellIndex,
                        goal.TargetCellIndex,
                        config.AgentRadius,
                        0.05f,
                        0.2f,
                        out _);
                }
                if (canApproachDirectly)
                {
                    goal.DirectApproach = 1;
                }

                targetVelocity = AniMovementCohortAlgorithms.BlendGoalVelocity(
                    flowDirection,
                    arrivalVelocity,
                    distanceToGoal,
                    goal.InfluenceRadius,
                    canApproachDirectly);
            }

            // 请求失效时也按加速度减速，不能把上一轮速度直接清零
            preferredVelocity.Value = VectorMath.MoveTowards(
                preferredVelocity.Value,
                targetVelocity,
                math.max(0f, config.MaxAcceleration) * DeltaTime);
        }
    }

    /// <summary>
    /// 让 Ani 远距离跟随导航分组的流向场方向，接近目标后转向自己的自然落点
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
        private ComponentLookup<NavigationFlowFieldHandle> _fieldHandleLookup;
        private BufferLookup<NavigationFlowFieldCell> _fieldLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _cohortLookup = state.GetComponentLookup<AniMovementCohort>(true);
            _pathStateLookup = state.GetComponentLookup<AniMovementCohortPathState>(true);
            _fieldStateLookup = state.GetComponentLookup<NavigationFlowFieldState>(true);
            _fieldHandleLookup = state.GetComponentLookup<NavigationFlowFieldHandle>(true);
            _fieldLookup = state.GetBufferLookup<NavigationFlowFieldCell>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _cohortLookup.Update(ref state);
            _pathStateLookup.Update(ref state);
            _fieldStateLookup.Update(ref state);
            _fieldHandleLookup.Update(ref state);
            _fieldLookup.Update(ref state);
            NavigationGridReference gridReference = default;
            bool hasGrid = SystemAPI.TryGetSingleton(out gridReference) &&
                           gridReference.Value.IsCreated;
            var job = new AniFreePreferredVelocityJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                HasGrid = (byte)(hasGrid ? 1 : 0),
                GridReference = gridReference,
                CohortLookup = _cohortLookup,
                PathStateLookup = _pathStateLookup,
                FieldStateLookup = _fieldStateLookup,
                FieldHandleLookup = _fieldHandleLookup,
                FieldLookup = _fieldLookup,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }

    // 每个 Cohort 只归约自己的有界成员 Buffer，多个 Cohort 可以并行判断到达
    [BurstCompile]
    internal partial struct AniFreeCohortProgressJob : IJobEntity
    {
        [ReadOnly]
        public ComponentLookup<AniMovementResult> ResultLookup;

        public void Execute(
            in AniMovementCohort cohort,
            in AniMovementCohortTarget target,
            ref AniMovementCohortPathState pathState,
            in NavigationFlowFieldState fieldState,
            in DynamicBuffer<AniMovementCohortMember> members)
        {
            if (pathState.Status == AniMovementCohortStatus.Failed ||
                pathState.Status == AniMovementCohortStatus.Completed ||
                pathState.Status == AniMovementCohortStatus.Holding)
            {
                return;
            }

            if (fieldState.Status == NavigationPathStatus.Failed &&
                fieldState.RequestVersion == pathState.ActiveRequestVersion)
            {
                pathState.Status = AniMovementCohortStatus.Failed;
                pathState.SettledTicks = 0;
                return;
            }

            if (fieldState.Status != NavigationPathStatus.Succeeded ||
                !AreMembersSettled(members, cohort.TargetVersion))
            {
                pathState.Status = fieldState.Status == NavigationPathStatus.Succeeded
                    ? AniMovementCohortStatus.Moving
                    : AniMovementCohortStatus.AwaitingPath;
                pathState.SettledTicks = 0;
                return;
            }

            pathState.SettledTicks++;
            if (pathState.SettledTicks >= 5)
            {
                pathState.Status = target.Mode == AniSquadCommandMode.Follow
                    ? AniMovementCohortStatus.Holding
                    : AniMovementCohortStatus.Completed;
            }
        }

        private bool AreMembersSettled(
            in DynamicBuffer<AniMovementCohortMember> members,
            uint targetVersion)
        {
            if (members.IsEmpty)
            {
                return false;
            }

            for (int index = 0; index < members.Length; index++)
            {
                Entity ani = members[index].Ani;
                if (!ResultLookup.HasComponent(ani))
                {
                    return false;
                }

                AniMovementResult result = ResultLookup[ani];
                // 目标换代和位移提交可能落在相邻 Job 链，旧版本结果不能提前完成新请求
                if (result.TargetVersion != targetVersion || result.Settled == 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    // 请求只遍历创建时记录的 Cohort 引用，销毁后的引用由组件存在性检查过滤
    [BurstCompile]
    internal partial struct AniMovementOrderProgressJob : IJobEntity
    {
        [ReadOnly]
        public ComponentLookup<AniMovementCohort> CohortLookup;

        [ReadOnly]
        public ComponentLookup<AniMovementCohortPathState> PathStateLookup;

        public void Execute(
            Entity orderEntity,
            in AniMovementOrder order,
            ref AniMovementOrderState orderState,
            in DynamicBuffer<AniMovementOrderCohort> cohorts)
        {
            if (orderState.Status != AniMovementOrderStatus.Active)
            {
                return;
            }

            int liveCohortCount = 0;
            bool anyFailed = false;
            bool anyMoving = false;
            for (int index = 0; index < cohorts.Length; index++)
            {
                Entity cohortEntity = cohorts[index].Cohort;
                if (!CohortLookup.HasComponent(cohortEntity) ||
                    CohortLookup[cohortEntity].Order != orderEntity)
                {
                    continue;
                }

                liveCohortCount++;
                if (!PathStateLookup.HasComponent(cohortEntity))
                {
                    // 存活 Cohort 缺少路径状态属于结构损坏，不能被当作已经完成
                    anyFailed = true;
                    continue;
                }

                AniMovementCohortStatus status = PathStateLookup[cohortEntity].Status;
                anyFailed |= status == AniMovementCohortStatus.Failed;
                anyMoving |= status == AniMovementCohortStatus.AwaitingPath ||
                             status == AniMovementCohortStatus.Moving;
            }

            orderState.ActiveCohortCount = liveCohortCount;
            if (anyFailed)
            {
                orderState.Status = AniMovementOrderStatus.Failed;
            }
            else if (liveCohortCount == 0)
            {
                orderState.Status = AniMovementOrderStatus.Superseded;
            }
            else if (!anyMoving && order.Mode != AniSquadCommandMode.Follow)
            {
                orderState.Status = AniMovementOrderStatus.Completed;
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
        private ComponentLookup<AniMovementCohort> _cohortLookup;
        private ComponentLookup<AniMovementCohortPathState> _pathStateLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _resultLookup = state.GetComponentLookup<AniMovementResult>(true);
            _cohortLookup = state.GetComponentLookup<AniMovementCohort>(true);
            _pathStateLookup = state.GetComponentLookup<AniMovementCohortPathState>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _resultLookup.Update(ref state);
            _cohortLookup.Update(ref state);
            _pathStateLookup.Update(ref state);

            var cohortJob = new AniFreeCohortProgressJob
            {
                ResultLookup = _resultLookup,
            };
            state.Dependency = cohortJob.ScheduleParallel(state.Dependency);

            // 请求归约必须等待 Cohort 状态写回，避免读取上一 Tick 的部分结果
            var orderJob = new AniMovementOrderProgressJob
            {
                CohortLookup = _cohortLookup,
                PathStateLookup = _pathStateLookup,
            };
            state.Dependency = orderJob.ScheduleParallel(state.Dependency);
        }
    }
}
