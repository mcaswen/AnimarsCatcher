using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在成员或阵型变化时生成中心对称的基础槽位
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniSquadAnchorAdvanceSystem))]
    [UpdateBefore(typeof(AniFormationAssignmentSystem))]
    public partial struct AniFormationLayoutSystem : ISystem
    {
        private const float HorizontalSpacing = 1.4f;
        private const float LongitudinalSpacing = 1.6f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
        }

        public void OnUpdate(ref SystemState state)
        {
            foreach (var (formation, members, slots) in
                     SystemAPI.Query<
                         RefRW<AniSquadFormationState>,
                         DynamicBuffer<AniSquadMember>,
                         DynamicBuffer<AniFormationSlot>>())
            {
                // MemberVersion 是布局失效标记，版本和槽位数量都未变时直接复用缓存
                if (formation.ValueRO.LayoutVersion == formation.ValueRO.MemberVersion &&
                    slots.Length == members.Length)
                {
                    continue;
                }

                int columnCount = AniSquadMovementAlgorithms.CalculateColumnCount(
                    formation.ValueRO.Kind,
                    members.Length,
                    formation.ValueRO.ColumnCount);

                // 列数只由阵型配置和成员数量决定，不读取成员当前位置

                // 槽位从局部中心生成，Anchor 移动不会改变阵型的对称性
                slots.Clear();

                // SlotIndex 与成员稳定排序一致，重建后仍能复用确定性槽位
                for (int slotIndex = 0; slotIndex < members.Length; slotIndex++)
                {
                    slots.Add(new AniFormationSlot
                    {
                        SlotIndex = slotIndex,
                        LocalOffset = AniSquadMovementAlgorithms.CalculateSlotOffset(
                            slotIndex,
                            members.Length,
                            formation.ValueRO.Kind,
                            columnCount,
                            HorizontalSpacing,
                            LongitudinalSpacing),
                        PreferredRole = AniSquadMovementAlgorithms.CalculateSlotRole(
                            slotIndex,
                            members.Length,
                            columnCount),
                    });
                }

                formation.ValueRW.ColumnCount = columnCount;
                formation.ValueRW.LayoutVersion = formation.ValueRO.MemberVersion;

                // 布局重建后必须重新分配成员，旧 SlotIndex 不能直接视为有效
                // AssignmentVersion 清零是布局和分配之间的显式同步边界
                formation.ValueRW.AssignmentVersion = 0;
            }
        }
    }

    /// <summary>
    /// 在布局变化时使用确定性的最小总代价匹配分配槽位
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniFormationLayoutSystem))]
    [UpdateBefore(typeof(AniSlotTargetSystem))]
    public partial struct AniFormationAssignmentSystem : ISystem
    {
        private const float RoleMismatchPenalty = 100000f;
        private const float SlotChangePenalty = 0.25f;

        private ComponentLookup<LocalTransform> _transformLookup;
        private ComponentLookup<AniSquadMembership> _membershipLookup;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _transformLookup = state.GetComponentLookup<LocalTransform>(true);
            _membershipLookup = state.GetComponentLookup<AniSquadMembership>(false);
        }

        public void OnUpdate(ref SystemState state)
        {
            _transformLookup.Update(ref state);
            _membershipLookup.Update(ref state);

            foreach (var (anchor, formation, members, slots, squadEntity) in
                     SystemAPI.Query<
                         RefRO<AniSquadAnchor>,
                         RefRW<AniSquadFormationState>,
                         DynamicBuffer<AniSquadMember>,
                         DynamicBuffer<AniFormationSlot>>()
                              .WithEntityAccess())
            {
                // 已完成布局且成员数量匹配时不重新扫描，避免每 Tick 进行 O(M*S) 匹配
                if (formation.ValueRO.AssignmentVersion == formation.ValueRO.LayoutVersion ||
                    members.IsEmpty ||
                    slots.Length != members.Length)
                {
                    continue;
                }

                // Buffer 迭代变量只读，复制可写句柄后再统一写回匹配结果
                DynamicBuffer<AniSquadMember> writableMembers = members;
                quaternion anchorRotation = quaternion.LookRotationSafe(
                    math.normalizesafe(anchor.ValueRO.Forward, new float3(0f, 0f, 1f)),
                    math.up());

                int memberCount = writableMembers.Length;
                NativeArray<float> costs = new(
                    memberCount * slots.Length,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);
                NativeArray<int> assignments = new(
                    memberCount,
                    Allocator.Temp,
                    NativeArrayOptions.UninitializedMemory);

                // 代价同时考虑当前位置、职责偏好和换槽，低频全局匹配避免贪心交叉
                // 成员 Buffer 已按 StableId 排序，Hungarian 的行顺序因此不依赖 Entity.Index
                // 槽位 Buffer 按 SlotIndex 生成，等价总代价时算法稳定选择更小列索引
                for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                {
                    AniSquadMember member = writableMembers[memberIndex];
                    float3 memberPosition = _transformLookup.HasComponent(member.Ani)
                        ? _transformLookup[member.Ani].Position
                        : anchor.ValueRO.Position;
                    for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                    {
                        float3 slotPosition =
                            AniSquadMovementAlgorithms.CalculateSlotWorldPosition(
                                anchor.ValueRO.Position,
                                anchorRotation,
                                slots[slotIndex].LocalOffset);
                        // 极远距离被钳制在职责惩罚以下，确保正确职责优先于空间距离
                        // 平方距离省去开方并保留近距离成员之间的排序关系
                        float cost = math.min(
                            99999f,
                            math.distancesq(memberPosition, slotPosition));
                        if (!IsRoleCompatible(member.Role, slots[slotIndex].PreferredRole))
                        {
                            // 角色数量不足时仍允许降级匹配，但总代价会优先用完兼容槽位
                            cost += RoleMismatchPenalty;
                        }

                        if (member.SlotIndex >= 0 && member.SlotIndex != slotIndex)
                        {
                            // 小幅换槽惩罚减少视觉跳变，不覆盖职责和明显的距离收益
                            cost += SlotChangePenalty;
                        }

                        costs[memberIndex * slots.Length + slotIndex] = cost;
                    }
                }

                bool assigned = AniSquadMovementAlgorithms.TrySolveMinimumCostAssignment(
                    costs,
                    memberCount,
                    slots.Length,
                    assignments);
                if (assigned)
                {
                    // 求解完成后一次性发布，任何失败都不会留下半套新槽位
                    for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                    {
                        AniSquadMember member = writableMembers[memberIndex];
                        member.SlotIndex = assignments[memberIndex];
                        writableMembers[memberIndex] = member;
                        UpdateMembershipSlot(member.Ani, member.SlotIndex);
                    }
                }

                assignments.Dispose();
                costs.Dispose();

                if (!assigned)
                {
                    // 输入异常时保留旧版本，使下一 Tick 能再次尝试而不发布部分分配
                    continue;
                }

                // 版本写回使同一布局在后续 Tick 直接跳过匹配
                formation.ValueRW.AssignmentVersion = formation.ValueRO.LayoutVersion;
            }
        }

        private static bool IsRoleCompatible(
            AniSquadRole memberRole,
            AniSquadRole preferredRole)
        {
            return memberRole == AniSquadRole.Any ||
                   preferredRole == AniSquadRole.Any ||
                   memberRole == preferredRole;
        }

        private void UpdateMembershipSlot(Entity aniEntity, int slotIndex)
        {
            if (!_membershipLookup.HasComponent(aniEntity))
            {
                // 成员可能在清理和匹配之间失效，缺少归属组件时跳过写回
                return;
            }

            AniSquadMembership membership = _membershipLookup[aniEntity];
            membership.SlotIndex = slotIndex;

            // Membership 与 Squad Buffer 必须同步写入，Progress 通过该索引读取槽位
            // 缺少任一写回会让表现层和服务器路径状态产生不同槽位
            _membershipLookup[aniEntity] = membership;
        }
    }

    /// <summary>
    /// 按 Anchor 姿态把局部槽位转换为成员世界目标
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniFormationAssignmentSystem))]
    [UpdateBefore(typeof(AniPreferredVelocitySystem))]
    public partial struct AniSlotTargetSystem : ISystem
    {
        private ComponentLookup<AniSlotTarget> _slotTargetLookup;
        private ComponentLookup<AniMovementConfig> _movementConfigLookup;
        private EntityQuery _gridQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _slotTargetLookup = state.GetComponentLookup<AniSlotTarget>(false);
            _movementConfigLookup = state.GetComponentLookup<AniMovementConfig>(true);
            _gridQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NavigationGridReference>());
        }

        public void OnUpdate(ref SystemState state)
        {
            _slotTargetLookup.Update(ref state);
            _movementConfigLookup.Update(ref state);
            if (_gridQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity gridEntity = _gridQuery.GetSingletonEntity();
            NavigationGridReference gridReference = state.EntityManager.GetComponentData<
                NavigationGridReference>(gridEntity);
            if (!gridReference.Value.IsCreated)
            {
                return;
            }

            NativeArray<NavigationDynamicOverlayCell> overlay =
                state.EntityManager.HasBuffer<NavigationDynamicOverlayCell>(gridEntity)
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(gridEntity)
                        .AsNativeArray()
                    : default;
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            foreach (var (anchor, members, slots) in
                     SystemAPI.Query<
                         RefRO<AniSquadAnchor>,
                         DynamicBuffer<AniSquadMember>,
                         DynamicBuffer<AniFormationSlot>>())
            {
                // 槽位保持局部坐标，只有此处结合 Anchor 姿态生成成员世界目标
                quaternion rotation = quaternion.LookRotationSafe(
                    math.normalizesafe(anchor.ValueRO.Forward, new float3(0f, 0f, 1f)),
                    math.up());

                // 同一 Anchor Rotation 应用于所有成员，阵型不会因逐成员朝向产生剪切
                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    AniSquadMember member = members[memberIndex];
                    // 未完成分配的成员由下一次 Assignment 补齐，不写入临时世界目标
                    if (member.SlotIndex < 0 ||
                        member.SlotIndex >= slots.Length ||
                        !_slotTargetLookup.HasComponent(member.Ani) ||
                        !_movementConfigLookup.HasComponent(member.Ani))
                    {
                        continue;
                    }

                    float3 desiredPosition =
                        AniSquadMovementAlgorithms.CalculateSlotWorldPosition(
                            anchor.ValueRO.Position,
                            rotation,
                            slots[member.SlotIndex].LocalOffset);
                    AniMovementConfig movementConfig = _movementConfigLookup[member.Ani];
                    float3 slotPosition = desiredPosition;
                    if (NavigationGridPathAlgorithms.TryWorldToCell(
                            ref grid,
                            desiredPosition,
                            out _,
                            out int directCellIndex) &&
                        NavigationGridPathAlgorithms.CanAgentOccupyDynamic(
                            ref grid,
                            directCellIndex,
                            movementConfig.AgentRadius,
                            0.1f,
                            overlay))
                    {
                        // 合法槽位保留亚 Cell 阵型偏移，只把高度贴合烘焙地面
                        slotPosition.y = grid.Cells[directCellIndex].Height;
                    }
                    else if (NavigationGridPathAlgorithms.TryProjectToNearestCell(
                                 ref grid,
                                 desiredPosition,
                                 movementConfig.AgentRadius,
                                 0.1f,
                                 2,
                                 overlay,
                                 out int projectedCellIndex))
                    {
                        // 无效槽位才退到附近合法 Cell 中心，避免成员追逐不可达目标
                        slotPosition = NavigationGridPathAlgorithms.GetCellWorldPosition(
                            ref grid,
                            projectedCellIndex);
                    }
                    else
                    {
                        continue;
                    }

                    _slotTargetLookup[member.Ani] = new AniSlotTarget
                    {
                        // 世界目标只写位置，成员旋转由唯一 Commit System 处理
                        Position = slotPosition,
                    };
                }
            }
        }
    }
}
