using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 持续读取 Follow 和 Find 目标的当前位置，供队伍重新寻路
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSquadLifecycleSystem))]
    [UpdateBefore(typeof(AniSquadPathRequestSystem))]
    public partial struct AniSquadTargetResolveSystem : ISystem
    {
        private ComponentLookup<LocalTransform> _transformLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            // 每帧刷新组件查询，确保读取的是动态目标当前的 Transform
            _transformLookup.Update(ref state);
            foreach (var (command, pathState, entity) in
                     SystemAPI.Query<RefRO<AniSquadCommand>, RefRW<AniSquadPathState>>()
                              .WithEntityAccess())
            {
                AniSquadCommand commandValue = command.ValueRO;
                float3 targetPosition = commandValue.TargetPosition;

                // MoveTo 使用指令中的固定坐标，动态指令每 Tick 从目标 Entity 刷新位置
                if (commandValue.Mode != AniSquadCommandMode.MoveTo)
                {
                    // 目标消失后继续使用旧坐标会伪造成功状态，因此立即终止指令
                    if (commandValue.TargetEntity == Entity.Null ||
                        !_transformLookup.HasComponent(commandValue.TargetEntity))
                    {
                        pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                        continue;
                    }

                    targetPosition = _transformLookup[commandValue.TargetEntity].Position;
                }

                // 目标 Transform 如果包含 NaN 或无穷值，就不能用于寻路
                if (!VectorMath.IsFinite(targetPosition))
                {
                    pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                    continue;
                }

                pathState.ValueRW.ResolvedTargetPosition = targetPosition;
            }
        }
    }

    /// <summary>
    /// 在队伍收到新指令或动态目标移动后，提交相应的异步 Flow Field 请求
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSquadTargetResolveSystem))]
    [UpdateBefore(typeof(ServerNavigationGridFlowFieldSystem))]
    public partial struct AniSquadPathRequestSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<NavigationGridReference>(out NavigationGridReference gridReference) ||
                !gridReference.Value.IsCreated)
            {
                // 导航网格尚未加载时先保留指令，等网格可用后再发起寻路
                return;
            }

            float cellSize = math.max(0.1f, gridReference.Value.Value.CellSize);
            // 动态目标至少跨过一个格子才重新寻路，过滤格子内部的小幅移动
            foreach (var (squad, command, anchor, pathState, request, fieldState, entity) in
                     SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadCommand>,
                         RefRO<AniSquadAnchor>,
                         RefRW<AniSquadPathState>,
                         RefRW<NavigationFlowFieldRequest>,
                         RefRW<NavigationFlowFieldState>>()
                              .WithEntityAccess())
            {
                if (pathState.ValueRO.Status == AniSquadMovementStatus.Failed)
                {
                    // 已失败的指令不会因为目标仍然存在就自行恢复，只能由新指令替换
                    continue;
                }

                NavigationPathStatus completedStatus = fieldState.ValueRO.Status;

                // 每个请求只统计一次，避免同一个完成结果在后续帧重复计数
                bool requestFinished =
                    fieldState.ValueRO.RequestVersion == pathState.ValueRO.ActiveRequestVersion &&
                    pathState.ValueRO.ActiveRequestVersion != 0 &&
                    pathState.ValueRO.CountedRequestVersion !=
                    pathState.ValueRO.ActiveRequestVersion &&
                    (completedStatus == NavigationPathStatus.Succeeded ||
                     completedStatus == NavigationPathStatus.Failed);
                if (requestFinished)
                {
                    pathState.ValueRW.CountedRequestVersion =
                        pathState.ValueRO.ActiveRequestVersion;
                    if (completedStatus == NavigationPathStatus.Succeeded)
                    {
                        pathState.ValueRW.SuccessfulFieldRequestCount++;

                        // 缓存命中属于这次请求的结果，也只在首次看到结果时统计
                        if (fieldState.ValueRO.CacheHit != 0)
                        {
                            pathState.ValueRW.CacheHitCount++;
                        }
                    }
                    else
                    {
                        // 失败请求同样只统计一次，便于区分“仍在计算”和“计算失败”
                        pathState.ValueRW.FailedFieldRequestCount++;
                    }
                }

                int cooldown = pathState.ValueRO.RepathCooldownTicks;
                if (cooldown > 0)
                {
                    // 冷却时间只限制动态目标的频繁重算，不会延迟玩家的新指令
                    pathState.ValueRW.RepathCooldownTicks = cooldown - 1;
                }

                bool newCommand = pathState.ValueRO.SubmittedCommandSequence != command.ValueRO.Sequence;

                // 目标移动不足一个格子时继续使用当前 Flow Field，避免轻微抖动触发重算
                float targetDeltaSquared = math.distancesq(
                    pathState.ValueRO.LastSubmittedTargetPosition,
                    pathState.ValueRO.ResolvedTargetPosition);
                bool targetMoved = targetDeltaSquared >= cellSize * cellSize;
                bool dynamicTarget = command.ValueRO.Mode != AniSquadCommandMode.MoveTo;
                bool canRepath = pathState.ValueRO.RepathCooldownTicks <= 0;

                // 只有新指令、尚无可用 Flow Field，或动态目标跨格移动时才重新请求
                // 固定坐标的 MoveTo 不会因格子内的微小差异反复重算
                bool needsRequest = newCommand ||
                                    fieldState.ValueRO.Status == NavigationPathStatus.None ||
                                    (dynamicTarget && targetMoved && canRepath);
                if (!needsRequest)
                {
                    // 已完成或保持跟随的指令不能被旧请求结果重新改回移动状态
                    if (fieldState.ValueRO.Status == NavigationPathStatus.Succeeded &&
                        fieldState.ValueRO.RequestVersion == pathState.ValueRO.ActiveRequestVersion)
                    {
                        AniSquadMovementStatus currentStatus = pathState.ValueRO.Status;
                        pathState.ValueRW.Status =
                            currentStatus == AniSquadMovementStatus.Holding ||
                            currentStatus == AniSquadMovementStatus.Completed
                                ? currentStatus
                                : AniSquadMovementStatus.Moving;
                    }
                    else if (fieldState.ValueRO.Status == NavigationPathStatus.Failed &&
                             fieldState.ValueRO.RequestVersion == pathState.ValueRO.ActiveRequestVersion)
                    {
                        // 只有当前请求失败才影响队伍；迟到的旧结果直接忽略
                        pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                    }

                    continue;
                }

                uint requestVersion = pathState.ValueRO.ActiveRequestVersion + 1;

                // 版本号 0 表示从未请求；计数溢出后跳过 0，从 1 重新开始
                if (requestVersion == 0)
                {
                    requestVersion = 1;
                }

                NavigationPathRequest pathRequest = NavigationPathRequest.Create(
                    anchor.ValueRO.Position,
                    pathState.ValueRO.ResolvedTargetPosition,
                    squad.ValueRO.MaximumAgentRadius,
                    requestVersion,
                    clearanceMargin: 0.05f,
                    maximumProjectionRadiusInCells: 16);

                // 新请求从队伍锚点当前所在格子开始，不沿用上一次指令的起点

                // 请求和等待状态使用同一个版本号，Flow Field 系统才能把结果交还给正确的队伍
                request.ValueRW = NavigationFlowFieldRequest.Create(pathRequest);
                fieldState.ValueRW = NavigationFlowFieldState.CreatePending(requestVersion);
                pathState.ValueRW.SubmittedCommandSequence = command.ValueRO.Sequence;
                pathState.ValueRW.ActiveRequestVersion = requestVersion;

                // 记住本次请求的目标位置，之后用它判断动态目标是否移动得足够远
                pathState.ValueRW.LastSubmittedTargetPosition =
                    pathState.ValueRO.ResolvedTargetPosition;

                // Follow 和 Find 至少间隔 8 帧才能再次寻路，期间仍沿当前 Flow Field 移动
                pathState.ValueRW.RepathCooldownTicks = 8;
                pathState.ValueRW.SettledTicks = 0;
                // 提交新请求后回到等待状态，避免进度系统沿用上一次的到达结果
                pathState.ValueRW.Status = AniSquadMovementStatus.AwaitingPath;
                // 请求计数只记录真正提交的任务，不包含冷却期间被忽略的目标移动
                pathState.ValueRW.FieldRequestCount++;
            }
        }
    }
}
