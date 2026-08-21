using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 在成员数量或阵型宽度变化后，重新生成以队伍中心为基准的槽位
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
                // 成员版本和槽位数量都没变时，现有布局仍然有效，无需每帧重建
                if (formation.ValueRO.LayoutVersion == formation.ValueRO.MemberVersion &&
                    slots.Length == members.Length)
                {
                    continue;
                }

                int columnCount = AniSquadFormationAlgorithms.CalculateColumnCount(
                    formation.ValueRO.Kind,
                    members.Length,
                    formation.ValueRO.ColumnCount);

                // 阵型宽度只取决于当前配置和成员数，不会因成员暂时站偏而改变

                // 槽位使用相对队伍中心的坐标，锚点移动不会破坏阵型对称性
                slots.Clear();

                // 槽位索引按固定顺序生成，重建同一阵型时结果保持一致
                for (int slotIndex = 0; slotIndex < members.Length; slotIndex++)
                {
                    slots.Add(new AniFormationSlot
                    {
                        SlotIndex = slotIndex,
                        LocalOffset = AniSquadFormationAlgorithms.CalculateSlotOffset(
                            slotIndex,
                            members.Length,
                            formation.ValueRO.Kind,
                            columnCount,
                            HorizontalSpacing,
                            LongitudinalSpacing),
                        PreferredRole = AniSquadFormationAlgorithms.CalculateSlotRole(
                            slotIndex,
                            members.Length,
                            columnCount),
                    });
                }

                formation.ValueRW.ColumnCount = columnCount;
                formation.ValueRW.LayoutVersion = formation.ValueRO.MemberVersion;

                // 槽位位置重建后，成员需要重新分配，旧槽位索引不能直接沿用
                // 将分配版本清零，让下一个系统明确知道需要重新匹配
                formation.ValueRW.AssignmentVersion = 0;
            }
        }
    }

    /// <summary>
    /// 阵型变化后，为成员分配合适且移动总成本较低的槽位
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
                // 当前布局已经完成分配且人数没变时直接复用，避免每帧重新计算匹配
                if (formation.ValueRO.AssignmentVersion == formation.ValueRO.LayoutVersion ||
                    members.IsEmpty ||
                    slots.Length != members.Length)
                {
                    continue;
                }

                // 查询得到的缓冲区句柄只读，复制出可写句柄后再统一提交匹配结果
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

                // 匹配成本同时考虑路程、职责适配和换槽；全局匹配可以减少成员路线交叉
                // 成员按 StableId 排序，因此 Hungarian 算法的输入顺序不受 Entity.Index 影响
                // 槽位按索引排列，成本相同时优先选择索引更小的位置
                for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                {
                    AniSquadMember member = writableMembers[memberIndex];
                    float3 memberPosition = _transformLookup.HasComponent(member.Ani)
                        ? _transformLookup[member.Ani].Position
                        : anchor.ValueRO.Position;
                    for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                    {
                        float3 slotPosition =
                            AniSquadSteeringAlgorithms.CalculateSlotWorldPosition(
                                anchor.ValueRO.Position,
                                anchorRotation,
                                slots[slotIndex].LocalOffset);
                        // 距离成本设有上限，使职责合适的重要性始终高于单纯离得近
                        // 使用平方距离可省去开方，同时保持距离远近的排序不变
                        float cost = math.min(
                            99999f,
                            math.distancesq(memberPosition, slotPosition));
                        if (!IsRoleCompatible(member.Role, slots[slotIndex].PreferredRole))
                        {
                            // 某类角色人数不足时允许站到非首选槽位，但会优先用满职责相符的位置
                            cost += RoleMismatchPenalty;
                        }

                        if (member.SlotIndex >= 0 && member.SlotIndex != slotIndex)
                        {
                            // 给换槽增加少量成本，减少不必要的来回换位，但不阻止明显更合理的分配
                            cost += SlotChangePenalty;
                        }

                        costs[memberIndex * slots.Length + slotIndex] = cost;
                    }
                }

                bool assigned = AniSquadFormationAlgorithms.TrySolveMinimumCostAssignment(
                    costs,
                    memberCount,
                    slots.Length,
                    assignments);
                if (assigned)
                {
                    // 全部匹配成功后再一次性写回，失败时不会留下半套新分配
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
                    // 输入异常时不更新版本，下一帧可以重试，同时保留上一套完整结果
                    continue;
                }

                // 记录已分配的布局版本，后续帧可以直接复用结果
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
                // 成员可能在计算期间被移除；缺少队伍归属组件时不再写回
                return;
            }

            AniSquadMembership membership = _membershipLookup[aniEntity];
            membership.SlotIndex = slotIndex;

            // 成员归属组件和队伍缓冲区必须写入同一个槽位索引，进度判断和画面才能一致
            _membershipLookup[aniEntity] = membership;
        }
    }

    /// <summary>
    /// 根据队伍锚点的位置和朝向，为每名成员生成世界坐标槽位
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
                    ? state.EntityManager.GetBuffer<NavigationDynamicOverlayCell>(
                            gridEntity,
                            isReadOnly: true)
                        .AsNativeArray()
                    : default;
            ref NavigationGridBlob grid = ref gridReference.Value.Value;
            foreach (var (anchor, members, slots) in
                     SystemAPI.Query<
                         RefRO<AniSquadAnchor>,
                         DynamicBuffer<AniSquadMember>,
                         DynamicBuffer<AniFormationSlot>>())
            {
                // 槽位数据始终保存相对坐标，只在这里结合锚点生成世界坐标
                quaternion rotation = quaternion.LookRotationSafe(
                    math.normalizesafe(anchor.ValueRO.Forward, new float3(0f, 0f, 1f)),
                    math.up());

                // 所有成员使用同一个锚点旋转，避免阵型因个人朝向不同而变形
                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    AniSquadMember member = members[memberIndex];
                    // 尚未分配到有效槽位的成员留到下一次匹配，不写入临时目标
                    if (member.SlotIndex < 0 ||
                        member.SlotIndex >= slots.Length ||
                        !_slotTargetLookup.HasComponent(member.Ani) ||
                        !_movementConfigLookup.HasComponent(member.Ani))
                    {
                        continue;
                    }

                    float3 desiredPosition =
                        AniSquadSteeringAlgorithms.CalculateSlotWorldPosition(
                            anchor.ValueRO.Position,
                            rotation,
                            slots[member.SlotIndex].LocalOffset);
                    AniMovementConfig movementConfig = _movementConfigLookup[member.Ani];
                    float3 slotPosition = desiredPosition;
                    if (NavigationGridQuery.TryWorldToCell(
                            ref grid,
                            desiredPosition,
                            out _,
                            out int directCellIndex) &&
                        NavigationGridTraversal.CanAgentOccupyDynamic(
                            ref grid,
                            directCellIndex,
                            movementConfig.AgentRadius,
                            0.1f,
                            overlay))
                    {
                        // 槽位本身可站立时保留精确位置，只把高度贴到烘焙地面
                        slotPosition.y = grid.Cells[directCellIndex].Height;
                    }
                    else if (NavigationGridQuery.TryProjectToNearestCell(
                                 ref grid,
                                 desiredPosition,
                                 movementConfig.AgentRadius,
                                 0.1f,
                                 2,
                                 overlay,
                                 out int projectedCellIndex))
                    {
                        // 槽位被挡住时改用附近可站立格子的中心，避免成员追赶无法到达的位置
                        slotPosition = NavigationGridQuery.GetCellWorldPosition(
                            ref grid,
                            projectedCellIndex);
                    }
                    else
                    {
                        continue;
                    }

                    _slotTargetLookup[member.Ani] = new AniSlotTarget
                    {
                        // 这里只设置目标位置；成员旋转统一由移动提交系统处理
                        Position = slotPosition,
                    };
                }
            }
        }
    }
}
