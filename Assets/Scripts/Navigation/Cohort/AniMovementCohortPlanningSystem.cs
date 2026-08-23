using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 为每个 Cohort 提交一份共享 Flow Field 请求并维护重规划版本
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniGoalRegionAssignmentSystem))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    public partial struct AniMovementCohortPathRequestSystem : ISystem
    {
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

            foreach (var (cohort, pathState, request, fieldState) in
                     SystemAPI.Query<
                         RefRO<AniMovementCohort>,
                         RefRW<AniMovementCohortPathState>,
                         RefRW<NavigationFlowFieldRequest>,
                         RefRW<NavigationFlowFieldState>>())
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
                if (requestFinished)
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
                // 重规划从成员当前中心出发，避免沿用订单创建时已经过期的位置
                NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                    cohort.ValueRO.RepresentativePosition,
                    pathState.ValueRO.GoalRegionCenterPosition,
                    cohort.ValueRO.MaximumAgentRadius,
                    requestVersion,
                    clearanceMargin: 0.05f,
                    maximumProjectionRadiusInCells: 32);
                request.ValueRW = NavigationFlowFieldRequest.Create(pathRequest);
                fieldState.ValueRW = NavigationFlowFieldState.CreatePending(requestVersion);
                pathState.ValueRW.ActiveRequestVersion = requestVersion;
                pathState.ValueRW.SubmittedTargetVersion = cohort.ValueRO.TargetVersion;
                pathState.ValueRW.LastSubmittedTargetPosition =
                    pathState.ValueRO.GoalRegionCenterPosition;
                pathState.ValueRW.RepathCooldownTicks = 8;
                pathState.ValueRW.SettledTicks = 0;
                pathState.ValueRW.Status = AniMovementCohortStatus.AwaitingPath;
                pathState.ValueRW.FieldRequestCount++;
            }
        }

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }
    }
}
