using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 沿 Squad 的局部 Flow Field 推进独立 Anchor
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridFlowFieldSystem))]
    [UpdateAfter(typeof(ServerNavigationGridBenchmarkTimingEndSystem))]
    [UpdateBefore(typeof(AniFormationLayoutSystem))]
    public partial struct AniSquadAnchorAdvanceSystem : ISystem
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
                // 没有已发布的 Grid 时不推进 Anchor，避免用默认 Blob 产生越界 Cell
                return;
            }

            float deltaTime = SystemAPI.Time.DeltaTime;
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            foreach (var (squad, order, pathState, anchor, fieldState, field) in
                     SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadOrder>,
                         RefRW<AniSquadPathState>,
                         RefRW<AniSquadAnchor>,
                         RefRO<NavigationFlowFieldState>,
                         DynamicBuffer<NavigationFlowFieldCell>>())
            {
                float3 targetVelocity = float3.zero;

                // 只有当前请求版本成功且订单仍在移动，Flow Direction 才能驱动 Anchor
                bool fieldReady = fieldState.ValueRO.Status == NavigationPathStatus.Succeeded &&
                                  fieldState.ValueRO.RequestVersion ==
                                  pathState.ValueRO.ActiveRequestVersion;

                // 失败、完成和 Holding 状态都冻结 Anchor，动态 Follow 由规划系统重新提交
                if (fieldReady &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Failed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Completed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Holding &&
                    NavigationGridPathAlgorithms.TryProjectToNearestCell(
                        ref grid,
                        anchor.ValueRO.Position,
                        squad.ValueRO.MaximumAgentRadius,
                        0.05f,
                        2,
                        out int currentCellIndex))
                {
                    // 投影失败时不写入 CurrentCellIndex，保留上次合法 Cell
                    anchor.ValueRW.CurrentCellIndex = currentCellIndex;

                    // 先按目标距离计算制动速度，再用 Field 方向投影到可行走网格
                    float3 brakingVelocity = AniSquadMovementAlgorithms.CalculateAnchorVelocity(
                        anchor.ValueRO.Position,
                        pathState.ValueRO.ResolvedTargetPosition,
                        squad.ValueRO.MinimumMaxSpeed,
                        squad.ValueRO.MinimumMaxAcceleration,
                        order.ValueRO.TargetStoppingDistance);
                    if (math.lengthsq(brakingVelocity) > 1e-6f &&
                        AniSquadMovementAlgorithms.TryGetFlowDirection(
                            field,
                            currentCellIndex,
                        out float3 flowDirection))
                    {
                        // Flow 只提供方向，速度大小由到达制动和 Squad 最小能力决定
                        targetVelocity = flowDirection * math.length(brakingVelocity);
                    }
                }

                // 无可用方向时 targetVelocity 保持零，MoveTowards 会按加速度平滑制动
                float maximumVelocityDelta =
                    math.max(0f, squad.ValueRO.MinimumMaxAcceleration) * deltaTime;

                // Anchor 速度按加速度上限渐进，避免 Field 方向切换造成瞬时速度跳变
                float3 velocity = AniSquadMovementAlgorithms.MoveTowards(
                    anchor.ValueRO.Velocity,
                    targetVelocity,
                    maximumVelocityDelta);
                float3 position = anchor.ValueRO.Position + velocity * deltaTime;

                // 先计算连续位置，再尝试投影到合法 Cell，投影失败时保留上一步高度
                // 位置更新仍遵守 DeltaTime，验收和生产 Tick 使用同一积分模型
                if (NavigationGridPathAlgorithms.TryWorldToCell(
                        ref grid,
                        position,
                        out _,
                        out int nextCellIndex))
                {
                    // 位置可能落在 Cell 内部，沿 Grid 高度回写保持 Anchor 与地面一致
                    position.y = NavigationGridPathAlgorithms.GetCellWorldPosition(
                        ref grid,
                        nextCellIndex).y;
                    anchor.ValueRW.CurrentCellIndex = nextCellIndex;
                }

                anchor.ValueRW.Position = position;
                anchor.ValueRW.Velocity = velocity;
                // Position 和 Velocity 必须来自同一积分结果，避免 Progress 看到混合帧
                float3 flatVelocity = new(velocity.x, 0f, velocity.z);
                if (math.lengthsq(flatVelocity) > 1e-5f)
                {
                    // 有有效水平速度时朝运动方向转向，停止后保留订单指定朝向
                    anchor.ValueRW.Forward = math.normalize(flatVelocity);
                }
                else if (pathState.ValueRO.Status == AniSquadMovementStatus.Completed ||
                         pathState.ValueRO.Status == AniSquadMovementStatus.Holding)
                {
                    // 完成后不再跟随残余速度，恢复订单的稳定朝向
                    anchor.ValueRW.Forward = math.normalizesafe(
                        order.ValueRO.DesiredForward,
                        new float3(0f, 0f, 1f));
                }
            }
        }
    }

    /// <summary>
    /// 根据 Anchor 前馈速度和槽位误差生成成员期望速度
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSlotTargetSystem))]
    [UpdateBefore(typeof(AniMovementCommitSystem))]
    public partial struct AniPreferredVelocitySystem : ISystem
    {
        private ComponentLookup<AniSquadAnchor> _anchorLookup;
        private ComponentLookup<AniSquadPathState> _pathStateLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _anchorLookup = state.GetComponentLookup<AniSquadAnchor>(true);
            _pathStateLookup = state.GetComponentLookup<AniSquadPathState>(true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _anchorLookup.Update(ref state);
            _pathStateLookup.Update(ref state);
            float deltaTime = SystemAPI.Time.DeltaTime;

            // 成员只读取所属 Squad 的 Anchor 和路径状态，避免每 Ani 复制一份路径结果
            foreach (var (transform, membership, config, slotTarget, preferredVelocity) in
                     SystemAPI.Query<
                         RefRO<LocalTransform>,
                         RefRO<AniSquadMembership>,
                         RefRO<AniMovementConfig>,
                         RefRO<AniSlotTarget>,
                         RefRW<AniPreferredVelocity>>())
            {
                Entity squadEntity = membership.ValueRO.Squad;
                float3 targetVelocity = float3.zero;
                if (_anchorLookup.HasComponent(squadEntity) &&
                    _pathStateLookup.HasComponent(squadEntity) &&
                    _pathStateLookup[squadEntity].Status != AniSquadMovementStatus.Failed)
                {
                    targetVelocity = AniSquadMovementAlgorithms.CalculateSlotVelocity(
                        transform.ValueRO.Position,
                        slotTarget.ValueRO.Position,
                        _anchorLookup[squadEntity].Velocity,
                        config.ValueRO.MaxSpeed);
                }

                // 成员速度仍受自身加速度限制，阵型误差不会瞬移修正
                // 缺少 Anchor 时 targetVelocity 保持零并自然减速
                preferredVelocity.ValueRW.Value = AniSquadMovementAlgorithms.MoveTowards(
                    preferredVelocity.ValueRO.Value,
                    targetVelocity,
                    config.ValueRO.MaxAcceleration * deltaTime);
            }
        }
    }

    /// <summary>
    /// 提交 Grid 后端的开阔地位移，并独占 Ani 权威 Transform 写入
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniPreferredVelocitySystem))]
    [UpdateBefore(typeof(AniMovementProgressSystem))]
    public partial struct AniMovementCommitSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;

            // Grid 后端的权威 Transform 只在此处写入，碰撞阶段只能改输入速度
            foreach (var (transform, config, slotTarget, preferredVelocity, result) in
                     SystemAPI.Query<
                         RefRW<LocalTransform>,
                         RefRO<AniMovementConfig>,
                         RefRO<AniSlotTarget>,
                         RefRO<AniPreferredVelocity>,
                         RefRW<AniMovementResult>>()
                              .WithAll<AniSquadMembership>())
            {
                float3 velocity = preferredVelocity.ValueRO.Value;

                // 移动模型只在水平面工作，保留 Transform 原有高度
                velocity.y = 0f;
                LocalTransform nextTransform = transform.ValueRO;
                nextTransform.Position += velocity * deltaTime;

                // 位置写入仍使用上一步 Transform，旋转只由当前速度增量决定

                float speedSquared = math.lengthsq(velocity);
                if (speedSquared > 1e-5f)
                {
                    // 用最大角速度限制旋转插值，避免低速抖动放大朝向变化
                    quaternion targetRotation = quaternion.LookRotationSafe(
                        math.normalize(velocity),
                        math.up());
                    float3 currentForward = math.mul(
                        nextTransform.Rotation,
                        new float3(0f, 0f, 1f));

                    // 点积夹紧后再求角度，避免浮点误差让 acos 返回 NaN
                    float dot = math.clamp(
                        math.dot(currentForward, math.normalize(velocity)),
                        -1f,
                        1f);
                    float angle = math.acos(dot);
                    float maximumStep = config.ValueRO.RotationSpeedRadians * deltaTime;
                    float interpolation = angle <= 1e-5f
                        ? 1f
                        : math.saturate(maximumStep / angle);
                    nextTransform.Rotation = math.slerp(
                        nextTransform.Rotation,
                        targetRotation,
                        interpolation);
                }

                // 统一写回位置、速度和提交计数，供验收与后续网络同步读取
                transform.ValueRW = nextTransform;

                // 槽位误差从已提交位置计算，Progress 不会读取提交前的旧距离
                float3 slotOffset = slotTarget.ValueRO.Position - nextTransform.Position;
                slotOffset.y = 0f;
                result.ValueRW.AppliedVelocity = velocity;
                result.ValueRW.DistanceToSlot = math.length(slotOffset);
                // CommitCount 是唯一 Transform 写入者的单调证据，不在其他系统递增
                result.ValueRW.CommitCount++;
            }
        }
    }

    /// <summary>
    /// 根据 Anchor 和全部成员槽位误差提交订单到达状态
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniMovementCommitSystem))]
    public partial struct AniMovementProgressSystem : ISystem
    {
        private ComponentLookup<LocalTransform> _transformLookup;
        private ComponentLookup<AniMovementConfig> _configLookup;
        private ComponentLookup<AniSlotTarget> _slotTargetLookup;
        private ComponentLookup<AniPreferredVelocity> _velocityLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
            _configLookup = state.GetComponentLookup<AniMovementConfig>(true);
            _slotTargetLookup = state.GetComponentLookup<AniSlotTarget>(true);
            _velocityLookup = state.GetComponentLookup<AniPreferredVelocity>(true);
        }

        public void OnUpdate(ref SystemState state)
        {
            _transformLookup.Update(ref state);
            _configLookup.Update(ref state);
            _slotTargetLookup.Update(ref state);
            _velocityLookup.Update(ref state);

            // Progress 只读取上一链路写入的结果，不直接修改成员 Transform
            // Lookup 全部刷新后再遍历，保证死亡成员不会被旧引用判定为到达
            foreach (var (order, anchor, pathState, fieldState, members) in
                     SystemAPI.Query<
                         RefRO<AniSquadOrder>,
                         RefRO<AniSquadAnchor>,
                         RefRW<AniSquadPathState>,
                         RefRO<NavigationFlowFieldState>,
                         DynamicBuffer<AniSquadMember>>())
            {
                if (fieldState.ValueRO.Status == NavigationPathStatus.Failed &&
                    fieldState.ValueRO.RequestVersion == pathState.ValueRO.ActiveRequestVersion)
                {
                    // 当前版本明确失败时终止订单，防止继续消费旧 Field
                    pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                if (fieldState.ValueRO.Status == NavigationPathStatus.Pending ||
                    fieldState.ValueRO.Status == NavigationPathStatus.Searching)
                {
                    // Field 尚未完成时保持等待状态，避免把未准备好的 Anchor 当作移动失败
                    pathState.ValueRW.Status = AniSquadMovementStatus.AwaitingPath;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                // Anchor 到达使用解析后的动态目标，而不是订单中的初始位置快照
                float3 targetOffset =
                    pathState.ValueRO.ResolvedTargetPosition - anchor.ValueRO.Position;
                targetOffset.y = 0f;
                bool anchorArrived = math.length(targetOffset) <=
                                     math.max(0.1f, order.ValueRO.TargetStoppingDistance);
                bool membersArrived = AreMembersSettled(members);

                // Anchor 到达不代表阵型到达，必须同时满足所有成员误差和速度阈值
                if (!anchorArrived || !membersArrived)
                {
                    // Anchor 和全部槽位都到达前，任何一项偏离都会重置稳定计数
                    pathState.ValueRW.Status = AniSquadMovementStatus.Moving;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                // 只有 Anchor 和所有成员同时满足门限时才累积稳定 Tick
                int settledTicks = pathState.ValueRO.SettledTicks + 1;
                pathState.ValueRW.SettledTicks = settledTicks;
                if (settledTicks < 5)
                {
                    // 连续多个 Tick 满足条件才确认到达，过滤速度和浮点边界抖动
                    continue;
                }

                // Follow 到达后保持跟随，其余一次性命令进入完成态
                pathState.ValueRW.Status = order.ValueRO.Mode == AniSquadCommandMode.Follow
                    ? AniSquadMovementStatus.Holding
                    : AniSquadMovementStatus.Completed;
            }
        }

        private bool AreMembersSettled(DynamicBuffer<AniSquadMember> members)
        {
            if (members.IsEmpty)
            {
                // 空 Squad 不应被判定为到达，生命周期会在下一次清理中销毁它
                return false;
            }

            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                if (!_transformLookup.HasComponent(aniEntity) ||
                    !_configLookup.HasComponent(aniEntity) ||
                    !_slotTargetLookup.HasComponent(aniEntity) ||
                    !_velocityLookup.HasComponent(aniEntity))
                {
                    // 任一成员缺少运行时组件都表示槽位状态不完整
                    return false;
                }

                float3 slotOffset =
                    _slotTargetLookup[aniEntity].Position - _transformLookup[aniEntity].Position;
                slotOffset.y = 0f;
                if (math.length(slotOffset) > _configLookup[aniEntity].ArrivalRadius ||
                    math.lengthsq(_velocityLookup[aniEntity].Value) > 0.0225f)
                {
                    // 同时约束位置误差和残余速度，防止成员经过目标后仍被算作稳定
                    return false;
                }
            }

            return true;
        }
    }
}
