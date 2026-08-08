using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 解析 Follow 和 Find 的动态目标，给路径请求提供稳定目标位置
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
            // Lookup 在每次结构变更后刷新，动态目标解析始终读取当前 Transform
            _transformLookup.Update(ref state);
            foreach (var (command, pathState, entity) in
                     SystemAPI.Query<RefRO<AniSquadCommand>, RefRW<AniSquadPathState>>()
                              .WithEntityAccess())
            {
                AniSquadCommand commandValue = command.ValueRO;
                float3 targetPosition = commandValue.TargetPosition;

                // MoveTo 使用指令快照，动态指令每 Tick 从权威目标刷新位置
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

                // Target Entity 也可能产生无效 Transform，不能让非有限值进入路径请求
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
    /// 把 Squad 目标变化转换为现有异步 Flow Field 请求
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
                // Grid 尚未发布时保留指令，待数据源就绪后再创建第一个请求
                return;
            }

            float cellSize = math.max(0.1f, gridReference.Value.Value.CellSize);
            // CellSize 同时作为目标移动阈值和请求投影的最小空间尺度
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
                    // 失败指令保持终止态，不能因目标仍存在而自动复活
                    // 只有新指令替换 SubmittedCommandSequence 后才允许恢复
                    continue;
                }

                NavigationPathStatus completedStatus = fieldState.ValueRO.Status;

                // 每个请求版本只计数一次，避免完成结果在后续 Tick 重复累加
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

                        // CacheHit 属于完成请求属性，仅在首次观察该版本时计数
                        if (fieldState.ValueRO.CacheHit != 0)
                        {
                            pathState.ValueRW.CacheHitCount++;
                        }
                    }
                    else
                    {
                        // 失败版本只计数一次，报告可区分 Field 失败和未完成请求
                        pathState.ValueRW.FailedFieldRequestCount++;
                    }
                }

                int cooldown = pathState.ValueRO.RepathCooldownTicks;
                if (cooldown > 0)
                {
                    // 冷却只限制动态目标重规划，新指令仍可立即提交
                    pathState.ValueRW.RepathCooldownTicks = cooldown - 1;
                }

                bool newCommand = pathState.ValueRO.SubmittedCommandSequence != command.ValueRO.Sequence;

                // 目标跨越至少一个 Cell 才值得失效当前 Field，过滤亚 Cell 抖动
                float targetDeltaSquared = math.distancesq(
                    pathState.ValueRO.LastSubmittedTargetPosition,
                    pathState.ValueRO.ResolvedTargetPosition);
                bool targetMoved = targetDeltaSquared >= cellSize * cellSize;
                bool dynamicTarget = command.ValueRO.Mode != AniSquadCommandMode.MoveTo;
                bool canRepath = pathState.ValueRO.RepathCooldownTicks <= 0;

                // 新指令、无 Field 或动态目标跨 Cell 是唯一三种重规划原因
                // 静态 MoveTo 忽略亚 Cell 目标漂移，避免反复销毁成功 Field
                bool needsRequest = newCommand ||
                                    fieldState.ValueRO.Status == NavigationPathStatus.None ||
                                    (dynamicTarget && targetMoved && canRepath);
                if (!needsRequest)
                {
                    // 已完成或 Holding 的指令不能被旧成功结果重新激活为 Moving
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
                        // 结果版本匹配时才把 Field 失败传播到 Squad，忽略旧请求结果
                        pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                    }

                    continue;
                }

                uint requestVersion = pathState.ValueRO.ActiveRequestVersion + 1;

                // 零是未发起请求的哨兵值，版本环绕时从一重新开始
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

                // 新请求从 Anchor 当前 Cell 投影，避免沿用旧指令起点

                // Request 与 Pending State 必须同 Tick 同版本写入，Flow 系统据此认领结果
                request.ValueRW = NavigationFlowFieldRequest.Create(pathRequest);
                fieldState.ValueRW = NavigationFlowFieldState.CreatePending(requestVersion);
                pathState.ValueRW.SubmittedCommandSequence = command.ValueRO.Sequence;
                pathState.ValueRW.ActiveRequestVersion = requestVersion;

                // 记录本次目标快照，后续距离比较只针对动态目标变化
                pathState.ValueRW.LastSubmittedTargetPosition =
                    pathState.ValueRO.ResolvedTargetPosition;

                // 八 Tick 冷却限制 Follow/Find 的重规划频率，不影响当前 Field 消费
                pathState.ValueRW.RepathCooldownTicks = 8;
                pathState.ValueRW.SettledTicks = 0;
                // 新请求提交后必须回到 AwaitingPath，Progress 不得消费旧到达状态
                pathState.ValueRW.Status = AniSquadMovementStatus.AwaitingPath;
                // FieldRequestCount 统计真实提交次数，不统计被冷却过滤的目标变化
                pathState.ValueRW.FieldRequestCount++;
            }
        }
    }
}
