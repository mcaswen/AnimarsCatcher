using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 为每个 Cohort 提交一份共享 Flow Field 请求并维护重规划版本
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniGoalRegionAssignmentSystem))]
    [UpdateBefore(typeof(ServerNavigationSharedFlowFieldSystem))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    public partial struct AniMovementCohortPathRequestSystem : ISystem
    {
        private ComponentLookup<LocalTransform> _transformLookup;
        private ComponentLookup<AniGoalAssignment> _goalLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
            _goalLookup = state.GetComponentLookup<AniGoalAssignment>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton(out NavigationGridReference gridReference) ||
                !gridReference.Value.IsCreated)
            {
                return;
            }

            _transformLookup.Update(ref state);
            _goalLookup.Update(ref state);
            Entity gridEntity = SystemAPI.GetSingletonEntity<NavigationGridReference>();
            NativeArray<NavigationDynamicOverlayCell> overlay =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(
                        gridEntity,
                        true).AsNativeArray()
                    : default;
            uint overlayVersion = state.EntityManager.HasComponent<NavigationDynamicOverlayState>(
                    gridEntity)
                ? state.EntityManager.GetComponentData<NavigationDynamicOverlayState>(gridEntity)
                    .Version
                : 1u;
            int cachedRadiusBits = 0;
            bool cachedGridIsOpen = false;
            bool hasCachedGridCheck = false;

            foreach (var (cohort, pathState, request, fieldState, fieldHandle, members) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohort>,
                         RefRW<AniMovementCohortPathState>,
                         RefRW<NavigationFlowFieldRequest>,
                         RefRW<NavigationFlowFieldState>,
                         RefRW<NavigationFlowFieldHandle>,
                         DynamicBuffer<AniMovementCohortMember>>())
            {
                if (pathState.ValueRO.Status == AniMovementCohortStatus.Failed ||
                    pathState.ValueRO.Status == AniMovementCohortStatus.Completed ||
                    pathState.ValueRO.Status == AniMovementCohortStatus.Holding)
                {
                    continue;
                }

                NavigationPathStatus resultStatus = fieldState.ValueRO.Status;
                // 只统计活动版本首次进入的终态，迟到的旧结果不会污染指标
                bool requestFinished = pathState.ValueRO.ActiveRequestVersion != 0 &&
                                       fieldState.ValueRO.RequestVersion ==
                                       pathState.ValueRO.ActiveRequestVersion &&
                                       pathState.ValueRO.CountedRequestVersion !=
                                       pathState.ValueRO.ActiveRequestVersion &&
                                       (resultStatus == NavigationPathStatus.Succeeded ||
                                        resultStatus == NavigationPathStatus.Failed);
                if (requestFinished &&
                    pathState.ValueRO.RouteMode == AniMovementCohortRouteMode.FlowField)
                {
                    pathState.ValueRW.CountedRequestVersion =
                        pathState.ValueRO.ActiveRequestVersion;
                    if (resultStatus == NavigationPathStatus.Succeeded)
                    {
                        pathState.ValueRW.SuccessfulFieldRequestCount++;
                        if (fieldState.ValueRO.CacheHit != 0)
                        {
                            pathState.ValueRW.CacheHitCount++;
                        }
                    }
                    else
                    {
                        pathState.ValueRW.FailedFieldRequestCount++;
                    }
                }

                if (pathState.ValueRO.RepathCooldownTicks > 0)
                {
                    pathState.ValueRW.RepathCooldownTicks--;
                }

                bool targetChanged = pathState.ValueRO.SubmittedTargetVersion !=
                                     cohort.ValueRO.TargetVersion;
                // 新 Cohort 立即请求，动态目标则受冷却限制后再替换旧版本
                bool needsRequest = pathState.ValueRO.ActiveRequestVersion == 0 ||
                                    fieldState.ValueRO.Status == NavigationPathStatus.None ||
                                    (targetChanged &&
                                     pathState.ValueRO.RepathCooldownTicks <= 0);
                if (!needsRequest)
                {
                    if (fieldState.ValueRO.RequestVersion ==
                        pathState.ValueRO.ActiveRequestVersion)
                    {
                        if (fieldState.ValueRO.Status == NavigationPathStatus.Succeeded)
                        {
                            pathState.ValueRW.Status = AniMovementCohortStatus.Moving;
                        }
                        else if (fieldState.ValueRO.Status == NavigationPathStatus.Failed)
                        {
                            pathState.ValueRW.Status = AniMovementCohortStatus.Failed;
                        }
                    }

                    continue;
                }

                uint requestVersion = NextNonZero(pathState.ValueRO.ActiveRequestVersion);
                int radiusBits = math.asint(cohort.ValueRO.MaximumAgentRadius);
                if (!hasCachedGridCheck || radiusBits != cachedRadiusBits)
                {
                    cachedRadiusBits = radiusBits;
                    cachedGridIsOpen = overlayVersion <= 1u && IsGridFullyOpen(
                        ref gridReference.Value.Value);
                    hasCachedGridCheck = true;
                }
                bool directRoute = CanCohortReachGoalsDirectly(
                    ref gridReference.Value.Value,
                    overlay,
                    members,
                    cohort.ValueRO.MaximumAgentRadius,
                    overlayVersion,
                    cachedGridIsOpen,
                    out float3 representativePosition);
                // 重规划从成员当前中心出发，避免沿用请求创建时已经过期的位置
                NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                    representativePosition,
                    pathState.ValueRO.GoalRegionCenterPosition,
                    cohort.ValueRO.MaximumAgentRadius,
                    requestVersion,
                    clearanceMargin: 0.05f,
                    maximumProjectionRadiusInCells: 32);
                request.ValueRW = NavigationFlowFieldRequest.Create(
                    pathRequest,
                    cohort.ValueRO.Priority,
                    cohort.ValueRO.CancellationVersion,
                    NavigationFlowFieldCoverageMode.GoalRegion);
                pathState.ValueRW.ActiveRequestVersion = requestVersion;
                pathState.ValueRW.SubmittedTargetVersion = cohort.ValueRO.TargetVersion;
                pathState.ValueRW.LastSubmittedTargetPosition =
                    pathState.ValueRO.GoalRegionCenterPosition;
                pathState.ValueRW.RepathCooldownTicks = 8;
                pathState.ValueRW.SettledTicks = 0;

                if (directRoute)
                {
                    // 直达路线不进入共享调度器，后续 Tick 也不再逐 Ani 扫描整条 Grid 线段
                    fieldHandle.ValueRW = default;
                    fieldState.ValueRW = CreateDirectSuccessState(
                        ref gridReference.Value.Value,
                        pathRequest,
                        overlay,
                        requestVersion);
                    pathState.ValueRW.RouteMode = AniMovementCohortRouteMode.Direct;
                    pathState.ValueRW.CountedRequestVersion = requestVersion;
                    pathState.ValueRW.Status = AniMovementCohortStatus.Moving;
                    pathState.ValueRW.DirectRouteCount++;
                }
                else
                {
                    fieldState.ValueRW = NavigationFlowFieldState.CreatePending(requestVersion);
                    pathState.ValueRW.RouteMode = AniMovementCohortRouteMode.FlowField;
                    pathState.ValueRW.Status = AniMovementCohortStatus.AwaitingPath;
                    pathState.ValueRW.FieldRequestCount++;
                }
            }
        }

        private bool CanCohortReachGoalsDirectly(
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            in DynamicBuffer<AniMovementCohortMember> members,
            float agentRadius,
            uint overlayVersion,
            bool gridIsOpen,
            out float3 representativePosition)
        {
            representativePosition = float3.zero;
            if (members.IsEmpty)
            {
                return false;
            }

            int validMemberCount = 0;
            bool everyMemberCanReachGoal = true;
            for (int index = 0; index < members.Length; index++)
            {
                Entity ani = members[index].Ani;
                if (!_transformLookup.HasComponent(ani) || !_goalLookup.HasComponent(ani))
                {
                    everyMemberCanReachGoal = false;
                    continue;
                }

                representativePosition += _transformLookup[ani].Position;
                validMemberCount++;
                if (!gridIsOpen && !CanMemberReachGoal(
                        ref grid,
                        overlay,
                        ani,
                        agentRadius))
                {
                    everyMemberCanReachGoal = false;
                }
            }

            if (validMemberCount == 0)
            {
                return false;
            }

            representativePosition /= validMemberCount;
            if (validMemberCount != members.Length || overlayVersion > 1u)
            {
                // 动态障碍出现后改用目标场，避免直达结果在后续 Overlay 变化时失效
                return false;
            }

            // 全开放 Grid 一次检查后整批直达，含障碍地图仍逐成员验证实际起终点
            return gridIsOpen || everyMemberCanReachGoal;
        }

        private bool CanMemberReachGoal(
            ref NavigationGridBlob grid,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            Entity ani,
            float agentRadius)
        {
            return _transformLookup.HasComponent(ani) &&
                   _goalLookup.HasComponent(ani) &&
                   NavigationGridQuery.TryWorldToCell(
                       ref grid,
                       _transformLookup[ani].Position,
                       out _,
                       out int startCellIndex) &&
                   CanReachDirectly(
                       ref grid,
                       startCellIndex,
                       _goalLookup[ani].TargetCellIndex,
                       agentRadius,
                       overlay);
        }

        private static bool IsGridFullyOpen(ref NavigationGridBlob grid)
        {
            int regionId = grid.Cells.Length == 0 ? 0 : grid.Cells[0].RegionId;
            if (regionId <= 0)
            {
                return false;
            }

            float height = grid.Cells[0].Height;
            float3 surfaceNormal = grid.Cells[0].SurfaceNormal;
            for (int cellIndex = 0; cellIndex < grid.Cells.Length; cellIndex++)
            {
                NavigationGridCell cell = grid.Cells[cellIndex];
                if (cell.Walkable == 0 ||
                    cell.RegionId != regionId ||
                    math.abs(cell.Height - height) > 0.0001f ||
                    math.distancesq(cell.SurfaceNormal, surfaceNormal) > 0.000001f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CanReachDirectly(
            ref NavigationGridBlob grid,
            int startCellIndex,
            int endCellIndex,
            float agentRadius,
            NativeArray<NavigationDynamicOverlayCell> overlay)
        {
            return NavigationGridQuery.TryCalculateLineCost(
                ref grid,
                startCellIndex,
                endCellIndex,
                agentRadius,
                0.05f,
                0.2f,
                overlay,
                out _);
        }

        private static NavigationFlowFieldState CreateDirectSuccessState(
            ref NavigationGridBlob grid,
            NavigationPathRequest request,
            NativeArray<NavigationDynamicOverlayCell> overlay,
            uint requestVersion)
        {
            NavigationGridQuery.TryProjectToNearestCell(
                ref grid,
                request.StartPosition,
                request.AgentRadius,
                request.ClearanceMargin,
                request.MaximumProjectionRadiusInCells,
                overlay,
                out int startCellIndex);
            NavigationGridQuery.TryProjectToNearestCell(
                ref grid,
                request.EndPosition,
                request.AgentRadius,
                request.ClearanceMargin,
                request.MaximumProjectionRadiusInCells,
                overlay,
                out int endCellIndex);
            return new NavigationFlowFieldState
            {
                Status = NavigationPathStatus.Succeeded,
                FailureReason = NavigationPathFailureReason.None,
                RequestVersion = requestVersion,
                ProjectedStartCellIndex = startCellIndex,
                ProjectedEndCellIndex = endCellIndex,
            };
        }

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }
    }
}
