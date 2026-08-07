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
    /// 保留有效旧槽位，并为新增成员确定性分配最近空槽位
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup))]
    [UpdateAfter(typeof(AniFormationLayoutSystem))]
    [UpdateBefore(typeof(AniSlotTargetSystem))]
    public partial struct AniFormationAssignmentSystem : ISystem
    {
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

                // Buffer 迭代变量只读，复制可写句柄后再逐成员更新槽位索引
                DynamicBuffer<AniSquadMember> writableMembers = members;

                // 位图只在本次匹配中存活，用一个字节表达槽位占用即可
                NativeArray<byte> usedSlots = new(
                    slots.Length,
                    Allocator.Temp,
                    NativeArrayOptions.ClearMemory);

                // 先保留仍属于本 Squad 且未重复占用的旧槽位，减少成员视觉跳变
                for (int memberIndex = 0; memberIndex < writableMembers.Length; memberIndex++)
                {
                    AniSquadMember member = writableMembers[memberIndex];
                    int slotIndex = member.SlotIndex;
                    bool valid = slotIndex >= 0 &&
                                 slotIndex < slots.Length &&
                                 usedSlots[slotIndex] == 0 &&
                                 _membershipLookup.HasComponent(member.Ani) &&
                                 _membershipLookup[member.Ani].Squad == squadEntity;

                    // 同一槽位重复出现时只保留排序靠前的成员，后者重新匹配
                    if (valid)
                    {
                        // 先标记保留槽位，后续新成员不能抢占稳定成员位置
                        usedSlots[slotIndex] = 1;
                    }
                    else
                    {
                        // 无效旧槽位统一标记为待分配，后续只写回当前成员
                        member.SlotIndex = -1;
                        writableMembers[memberIndex] = member;
                    }
                }

                quaternion anchorRotation = quaternion.LookRotationSafe(
                    math.normalizesafe(anchor.ValueRO.Forward, new float3(0f, 0f, 1f)),
                    math.up());

                // 新成员按当前位置择最近空槽，距离相同按 SlotIndex 保证确定性
                for (int memberIndex = 0; memberIndex < writableMembers.Length; memberIndex++)
                {
                    AniSquadMember member = writableMembers[memberIndex];
                if (member.SlotIndex >= 0)
                {
                    // 保留旧槽位的成员不参与最近距离竞争，避免同槽抢占
                    // 只有新成员进入最近槽位搜索，保持已在队成员的视觉连续性
                    UpdateMembershipSlot(member.Ani, member.SlotIndex);
                        continue;
                    }

                    float3 memberPosition = _transformLookup.HasComponent(member.Ani)
                        ? _transformLookup[member.Ani].Position
                        : anchor.ValueRO.Position;

                    // 缺失 Transform 的异常成员以 Anchor 为参考，不引入非有限距离
                    int bestSlot = FindNearestFreeSlot(
                        memberPosition,
                        anchor.ValueRO.Position,
                        anchorRotation,
                        slots,
                        usedSlots);
                    if (bestSlot < 0)
                    {
                        // 槽位不足只能等待下一次布局版本，不能制造重复槽位
                        continue;
                    }

                    member.SlotIndex = bestSlot;
                    writableMembers[memberIndex] = member;
                    usedSlots[bestSlot] = 1;
                    UpdateMembershipSlot(member.Ani, bestSlot);
                }

                usedSlots.Dispose();

                // 版本写回使同一布局在后续 Tick 直接跳过匹配
                formation.ValueRW.AssignmentVersion = formation.ValueRO.LayoutVersion;
            }
        }

        private int FindNearestFreeSlot(
            float3 memberPosition,
            float3 anchorPosition,
            quaternion anchorRotation,
            DynamicBuffer<AniFormationSlot> slots,
            NativeArray<byte> usedSlots)
        {
            int bestSlot = -1;
            float bestDistanceSquared = float.PositiveInfinity;

            // 线性扫描保证小规模 Squad 的稳定结果，时间复杂度为 O(S)
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                if (usedSlots[slotIndex] != 0)
                {
                    // 已保留或已分配的槽位不参与距离比较
                    continue;
                }

                float3 worldPosition = AniSquadMovementAlgorithms.CalculateSlotWorldPosition(
                    anchorPosition,
                    anchorRotation,
                    slots[slotIndex].LocalOffset);
                float distanceSquared = math.distancesq(memberPosition, worldPosition);

                // 比较平方距离避免开方，等距时使用索引作为稳定次关键字
                // 该比较保持同一输入在不同机器上的槽位选择一致

                // 浮点近似相等时选择更小索引，避免平台差异改变成员归属
                if (distanceSquared < bestDistanceSquared - 1e-5f ||
                    (math.abs(distanceSquared - bestDistanceSquared) <= 1e-5f &&
                     slotIndex < bestSlot))
                {
                    bestDistanceSquared = distanceSquared;
                    bestSlot = slotIndex;
                }
            }

            return bestSlot;
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

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _slotTargetLookup = state.GetComponentLookup<AniSlotTarget>(false);
        }

        public void OnUpdate(ref SystemState state)
        {
            _slotTargetLookup.Update(ref state);
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
                        !_slotTargetLookup.HasComponent(member.Ani))
                    {
                        continue;
                    }

                    _slotTargetLookup[member.Ani] = new AniSlotTarget
                    {
                        // 世界目标只写位置，成员旋转由唯一 Commit System 处理
                        Position = AniSquadMovementAlgorithms.CalculateSlotWorldPosition(
                            anchor.ValueRO.Position,
                            rotation,
                            slots[member.SlotIndex].LocalOffset),
                    };
                }
            }
        }
    }
}
