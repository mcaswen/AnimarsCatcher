using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 让队伍锚点沿当前 Flow Field 向目标移动，并在接近目标时减速
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(ServerNavigationGridFlowFieldSystem))]
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
                // 导航网格尚未加载时不移动锚点，避免访问未初始化的格子数据
                return;
            }

            float deltaTime = SystemAPI.Time.DeltaTime;
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            foreach (var (squad, command, pathState, anchor, fieldState, field) in
                     SystemAPI.Query<
                         RefRO<AniSquad>,
                         RefRO<AniSquadCommand>,
                         RefRW<AniSquadPathState>,
                         RefRW<AniSquadAnchor>,
                         RefRO<NavigationFlowFieldState>,
                         DynamicBuffer<NavigationFlowFieldCell>>())
            {
                float3 targetVelocity = float3.zero;

                // 只有当前寻路请求成功且队伍仍在移动时，Flow Field 方向才会推动锚点
                bool fieldReady = fieldState.ValueRO.Status == NavigationPathStatus.Succeeded &&
                                  fieldState.ValueRO.RequestVersion ==
                                  pathState.ValueRO.ActiveRequestVersion;

                // 寻路失败、指令完成或保持跟随时锚点停下；目标再次移动后由规划系统重新寻路
                if (fieldReady &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Failed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Completed &&
                    pathState.ValueRO.Status != AniSquadMovementStatus.Holding &&
                    NavigationGridQuery.TryProjectToNearestCell(
                        ref grid,
                        anchor.ValueRO.Position,
                        squad.ValueRO.MaximumAgentRadius,
                        0.05f,
                        2,
                        out int currentCellIndex))
                {
                    // 找不到有效格子时保留上一次位置，不让无效索引覆盖当前状态
                    anchor.ValueRW.CurrentCellIndex = currentCellIndex;

                    // 先根据剩余距离算出应有速度，再让速度方向服从 Flow Field 的可行路线
                    float3 brakingVelocity = AniSquadSteeringAlgorithms.CalculateAnchorVelocity(
                        anchor.ValueRO.Position,
                        pathState.ValueRO.ResolvedTargetPosition,
                        squad.ValueRO.MinimumMaxSpeed,
                        squad.ValueRO.MinimumMaxAcceleration,
                        command.ValueRO.TargetStoppingDistance);
                    if (math.lengthsq(brakingVelocity) > 1e-6f)
                    {
                        bool hasFlowDirection =
                            AniSquadSteeringAlgorithms.TryGetFlowDirection(
                                field,
                                currentCellIndex,
                                out float3 flowDirection) &&
                            math.lengthsq(flowDirection) > 1e-6f;
                        if (hasFlowDirection)
                        {
                            // Flow Field 只负责指路，速度大小由制动距离和全队移动能力决定
                            targetVelocity = flowDirection * math.length(brakingVelocity);
                        }
                        else
                        {
                            float targetDistance = math.length(
                                PlanarMath.FlattenY(
                                    pathState.ValueRO.ResolvedTargetPosition -
                                    anchor.ValueRO.Position));
                            float fallbackRange =
                                math.max(0.1f, command.ValueRO.TargetStoppingDistance) +
                                grid.CellSize;
                            if (targetDistance <= fallbackRange)
                            {
                                // 终点附近的平滑方向可能互相抵消；只在一格范围内直接朝目标补最后一段
                                targetVelocity =
                                    PlanarMath.NormalizeXZOrDefault(
                                        pathState.ValueRO.ResolvedTargetPosition -
                                        anchor.ValueRO.Position,
                                        float3.zero) * math.length(brakingVelocity);
                            }
                        }
                    }
                }

                // 没有可用方向时目标速度为零，锚点会按加速度限制逐渐停下
                float maximumVelocityDelta =
                    math.max(0f, squad.ValueRO.MinimumMaxAcceleration) * deltaTime;

                // 锚点速度逐步接近目标速度，避免路线转向时突然改变速度
                float3 velocity = VectorMath.MoveTowards(
                    anchor.ValueRO.Velocity,
                    targetVelocity,
                    maximumVelocityDelta);
                float3 position = anchor.ValueRO.Position + velocity * deltaTime;

                // 先计算连续移动后的位置，再贴回合法格子；投影失败时保留原来的地面高度
                // 所有位移都乘以 DeltaTime，验证环境和正式运行使用同一套移动方式
                if (NavigationGridQuery.TryWorldToCell(
                        ref grid,
                        position,
                        out _,
                        out int nextCellIndex))
                {
                    // 锚点可以停在格子内部，但 Y 坐标始终贴合该格子的烘焙地面高度
                    position.y = NavigationGridQuery.GetCellWorldPosition(
                        ref grid,
                        nextCellIndex).y;
                    anchor.ValueRW.CurrentCellIndex = nextCellIndex;
                }

                anchor.ValueRW.Position = position;
                anchor.ValueRW.Velocity = velocity;
                // 位置和速度一起写回，避免进度系统读到不同帧的数据
                float3 flatVelocity = PlanarMath.FlattenY(velocity);
                if (math.lengthsq(flatVelocity) > 1e-5f)
                {
                    // 移动时朝向速度方向；停下后保留指令要求的最终朝向
                    anchor.ValueRW.Forward = math.normalize(flatVelocity);
                }
                else
                {
                    float targetDistance = math.length(
                        PlanarMath.FlattenY(
                            pathState.ValueRO.ResolvedTargetPosition - position));
                    if (pathState.ValueRO.Status != AniSquadMovementStatus.Failed &&
                        targetDistance <= math.max(
                            0.1f,
                            command.ValueRO.TargetStoppingDistance))
                    {
                        // 在判断到达前先切换到最终朝向，让成员有时间站到旋转后的正确槽位
                        anchor.ValueRW.Forward = math.normalizesafe(
                            command.ValueRO.DesiredForward,
                            new float3(0f, 0f, 1f));
                    }
                }
            }
        }
    }

    /// <summary>
    /// 让每名成员跟随队伍整体速度，同时修正自己与阵型槽位的偏差
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

            // 成员共享所属队伍的锚点和寻路结果，不为每个 Ani 重复保存一份路径
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
                    targetVelocity = AniSquadSteeringAlgorithms.CalculateSlotVelocity(
                        transform.ValueRO.Position,
                        slotTarget.ValueRO.Position,
                        _anchorLookup[squadEntity].Velocity,
                        config.ValueRO.MaxSpeed);
                }

                // 成员仍受自身加速度限制，不会为了追槽位而瞬移
                // 找不到队伍锚点时目标速度保持为零，让成员自然减速
                preferredVelocity.ValueRW.Value = VectorMath.MoveTowards(
                    preferredVelocity.ValueRO.Value,
                    targetVelocity,
                    config.ValueRO.MaxAcceleration * deltaTime);
            }
        }
    }

    /// <summary>
    /// 把计算后的速度应用到 Ani，并作为导航模块唯一写入位置和旋转的系统
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

            // 导航模块只在这里写 Transform；碰撞处理只能调整输入速度，不能另行改位置
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

                // 队伍移动只处理水平位移，角色原有高度保持不变
                velocity = PlanarMath.FlattenY(velocity);
                LocalTransform nextTransform = transform.ValueRO;
                nextTransform.Position += velocity * deltaTime;

                // 先从当前 Transform 计算新位置，旋转则根据本帧实际速度更新

                float speedSquared = math.lengthsq(velocity);
                if (speedSquared > 1e-5f)
                {
                    // 用最大角速度限制转向，避免低速时的速度抖动造成朝向跳变
                    quaternion targetRotation = quaternion.LookRotationSafe(
                        math.normalize(velocity),
                        math.up());
                    float3 currentForward = math.mul(
                        nextTransform.Rotation,
                        new float3(0f, 0f, 1f));

                    // 先把点积限制在合法范围，再计算夹角，避免浮点误差产生 NaN
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

                // 一次写回位置、实际速度和提交次数，供进度判断和网络同步读取
                transform.ValueRW = nextTransform;

                // 使用移动后的新位置计算槽位误差，进度系统不会读到上一帧的距离
                float3 slotOffset = slotTarget.ValueRO.Position - nextTransform.Position;
                slotOffset = PlanarMath.FlattenY(slotOffset);
                result.ValueRW.AppliedVelocity = velocity;
                result.ValueRW.DistanceToSlot = math.length(slotOffset);
                // CommitCount 只在这里递增，用于确认位置确实由唯一的提交系统写入
                result.ValueRW.CommitCount++;
            }
        }
    }

    /// <summary>
    /// 综合队伍锚点和所有成员的状态，判断移动指令是否真正完成
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

            // 本系统只读取移动结果，不修改成员 Transform
            // 先刷新所有组件查询，避免已销毁成员的旧引用影响到达判断
            foreach (var (command, anchor, pathState, fieldState, members) in
                     SystemAPI.Query<
                         RefRO<AniSquadCommand>,
                         RefRO<AniSquadAnchor>,
                         RefRW<AniSquadPathState>,
                         RefRO<NavigationFlowFieldState>,
                         DynamicBuffer<AniSquadMember>>())
            {
                AniSquadMovementStatus currentStatus = pathState.ValueRO.Status;
                if (currentStatus == AniSquadMovementStatus.Failed ||
                    currentStatus == AniSquadMovementStatus.Completed ||
                    currentStatus == AniSquadMovementStatus.Holding)
                {
                    // 完成或失败状态只能由新指令或动态目标重规划解除，不会因成员晃动自行恢复
                    continue;
                }

                if (fieldState.ValueRO.Status == NavigationPathStatus.Failed &&
                    fieldState.ValueRO.RequestVersion == pathState.ValueRO.ActiveRequestVersion)
                {
                    // 当前寻路请求失败后立即终止指令，避免继续使用旧 Flow Field
                    pathState.ValueRW.Status = AniSquadMovementStatus.Failed;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                if (fieldState.ValueRO.Status == NavigationPathStatus.Pending ||
                    fieldState.ValueRO.Status == NavigationPathStatus.Searching)
                {
                    // Flow Field 仍在计算时保持等待，不把尚未开始移动误判为失败
                    pathState.ValueRW.Status = AniSquadMovementStatus.AwaitingPath;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                // 对 Follow 和 Find 使用目标当前坐标判断锚点是否到达，而不是最初下令时的位置
                float3 targetOffset =
                    pathState.ValueRO.ResolvedTargetPosition - anchor.ValueRO.Position;
                targetOffset = PlanarMath.FlattenY(targetOffset);
                bool anchorArrived = math.length(targetOffset) <=
                                     math.max(0.1f, command.ValueRO.TargetStoppingDistance);
                bool membersArrived = AreMembersSettled(members);

                // 锚点到达不等于全队到达；所有成员还必须靠近槽位并基本停稳
                if (!anchorArrived || !membersArrived)
                {
                    // 锚点或任一成员不满足条件时，连续稳定帧数重新计数
                    pathState.ValueRW.Status = AniSquadMovementStatus.Moving;
                    pathState.ValueRW.SettledTicks = 0;
                    continue;
                }

                // 只有锚点和所有成员同时满足条件，才累计稳定帧数
                int settledTicks = pathState.ValueRO.SettledTicks + 1;
                pathState.ValueRW.SettledTicks = settledTicks;
                if (settledTicks < 5)
                {
                    // 连续多帧都稳定后才确认到达，避免速度或浮点误差让状态来回切换
                    continue;
                }

                // Follow 到达后进入等待跟随；其他一次性移动指令直接完成
                pathState.ValueRW.Status = command.ValueRO.Mode == AniSquadCommandMode.Follow
                    ? AniSquadMovementStatus.Holding
                    : AniSquadMovementStatus.Completed;
            }
        }

        private bool AreMembersSettled(DynamicBuffer<AniSquadMember> members)
        {
            if (members.IsEmpty)
            {
                // 没有成员的队伍不能算作到达，生命周期系统会清理它
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
                    // 任一成员缺少位置、目标或移动结果，就说明当前阵型状态不完整
                    return false;
                }

                float3 slotOffset =
                    _slotTargetLookup[aniEntity].Position - _transformLookup[aniEntity].Position;
                slotOffset = PlanarMath.FlattenY(slotOffset);
                if (math.length(slotOffset) > _configLookup[aniEntity].ArrivalRadius ||
                    math.lengthsq(_velocityLookup[aniEntity].Value) > 0.0225f)
                {
                    // 同时检查位置和剩余速度，避免成员只是掠过槽位就被判定为站稳
                    return false;
                }
            }

            return true;
        }
    }
}
