using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 根据服务器收到的移动指令创建或复用队伍，并维护成员、阵型和寻路数据
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(NavigationDynamicOverlaySystem))]
    public partial struct AniSquadLifecycleSystem : ISystem
    {
        private EntityQuery _commandQuery;
        private EntityQuery _squadQuery;
        private uint _nextSquadId;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();

            // 指令和队伍查询在系统生命周期内复用，避免每帧重新创建
            _commandQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniSquadCommandRequest>(),
                ComponentType.ReadOnly<AniSquadCommand>(),
                ComponentType.ReadOnly<AniSquadCommandMember>());
            _squadQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<AniSquad>(),
                ComponentType.ReadWrite<AniSquadCommand>(),
                ComponentType.ReadWrite<AniSquadPathState>(),
                ComponentType.ReadWrite<AniSquadAnchor>(),
                ComponentType.ReadWrite<AniSquadFormationState>(),
                ComponentType.ReadWrite<AniSquadMember>(),
                ComponentType.ReadWrite<AniFormationSlot>(),
                ComponentType.ReadWrite<NavigationFlowFieldRequest>(),
                 ComponentType.ReadWrite<NavigationFlowFieldState>());

            // 0 表示尚未分配身份，因此有效 SquadId 从 1 开始
            _nextSquadId = 1;
        }

        public void OnUpdate(ref SystemState state)
        {
            // 先移除失效成员，再处理新指令，避免同一帧把已死亡成员重新加入旧队伍
            // Cleanup 可能销毁上下文，因此必须在指令快照创建前完成
            CleanupSquads(ref state);

            using NativeArray<Entity> commands = _commandQuery.ToEntityArray(Allocator.Temp);

            // RPC 和基准测试可能在同一帧产生指令，按序号处理可以保持统一顺序
            // 使用快照数组后，下面的处理逻辑可以销毁指令 Entity 而不影响本轮遍历
            SortCommands(ref state, commands);
            for (int index = 0; index < commands.Length; index++)
            {
                if (state.EntityManager.Exists(commands[index]))
                {
                    ConsumeCommand(ref state, commands[index]);
                }
            }
        }

        private void CleanupSquads(ref SystemState state)
        {
            // 查询结果是快照，清理过程允许销毁 Squad 而不影响后续索引
            // 快照不保留可写缓冲区，所有结构变更都通过 EntityManager 完成
            using NativeArray<Entity> squads = _squadQuery.ToEntityArray(Allocator.Temp);
            for (int squadIndex = 0; squadIndex < squads.Length; squadIndex++)
            {
                Entity squadEntity = squads[squadIndex];
                if (!state.EntityManager.Exists(squadEntity))
                {
                    continue;
                }

                DynamicBuffer<AniSquadMember> members =
                    state.EntityManager.GetBuffer<AniSquadMember>(squadEntity);
                bool changed = false;

                // 从后向前删除，避免 DynamicBuffer 收缩后漏掉紧邻的成员
                for (int memberIndex = members.Length - 1; memberIndex >= 0; memberIndex--)
                {
                    Entity aniEntity = members[memberIndex].Ani;

                    // 成员归属组件和队伍缓冲区必须互相对应；任一侧不一致都视为失效成员
                    if (!state.EntityManager.Exists(aniEntity) ||
                        !state.EntityManager.HasComponent<AniSquadMembership>(aniEntity) ||
                         state.EntityManager.GetComponentData<AniSquadMembership>(aniEntity).Squad !=
                         squadEntity)
                    {
                        members.RemoveAt(memberIndex);
                        changed = true;
                    }
                }

                if (members.IsEmpty)
                {
                    // 最后一名有效成员离开后，队伍的路径、Flow Field 和阵型数据都可以释放
                    state.EntityManager.DestroyEntity(squadEntity);
                    continue;
                }

                if (changed)
                {
                    // 人员变化会让旧槽位分配失效，但队伍身份仍可继续使用
                    AniSquad squad = state.EntityManager.GetComponentData<AniSquad>(squadEntity);
                    squad.MemberVersion++;
                    state.EntityManager.SetComponentData(squadEntity, squad);
                    UpdateAggregate(ref state, squadEntity, ref squad);
                    state.EntityManager.SetComponentData(squadEntity, squad);
                    MarkFormationMembersChanged(state.EntityManager, squadEntity, squad.MemberVersion);
                }
            }
        }

        private void ConsumeCommand(ref SystemState state, Entity commandEntity)
        {
            EntityManager entityManager = state.EntityManager;

            // 指令组件在本方法内只读一次，后续写回统一进入 Squad Entity
            AniSquadCommand command = entityManager.GetComponentData<AniSquadCommand>(commandEntity);

            // 先把指令成员复制到临时列表，后续组件补齐会引起结构变更
            // 复制也隔离了指令 Entity 销毁对源 Buffer 的影响
            DynamicBuffer<AniSquadCommandMember> commandMembers =
                entityManager.GetBuffer<AniSquadCommandMember>(commandEntity);
            using NativeList<AniSquadCommandMember> members =
                CollectValidMembers(ref state, commandMembers);

            if (members.IsEmpty)
            {
                // 指令中没有任何有效成员时直接丢弃，不创建空队伍，也不占用新的编号
                entityManager.DestroyEntity(commandEntity);
                return;
            }

            SortMembers(members);

            // 先排序再查找可复用队伍，让相同成员集合始终以同一顺序比较
            // 只有成员仍属于同一玩家、同一队伍且人数一致时，才能复用原来的寻路数据
            Entity squadEntity = FindReusableSquad(ref state, command, members);
            bool reused = squadEntity != Entity.Null;

            // 确认可复用后再保存旧槽位；新队伍没有需要保留的历史分配
            // 临时列表按当前人数分配容量，避免申请多余内存
            NativeList<AniSquadMember> previousMembers =
                new(math.max(1, members.Length), Allocator.Temp);

            if (!reused)
            {
                squadEntity = CreateSquad(ref state, command, members);
            }
            else
            {
                // 记住成员原来的槽位，即使输入顺序变化也尽量避免画面上的突然换位
                DynamicBuffer<AniSquadMember> oldMembers =
                    entityManager.GetBuffer<AniSquadMember>(squadEntity);
                for (int index = 0; index < oldMembers.Length; index++)
                {
                    previousMembers.Add(oldMembers[index]);
                }
            }

            DetachMembersFromOtherSquads(ref state, squadEntity, members);

            // 先从旧队伍脱离，再补齐目标组件，避免旧队伍更新时覆盖新的归属

            AniSquad squad = entityManager.GetComponentData<AniSquad>(squadEntity);

            // LayoutVersion 保持旧值，确保布局系统在下一更新中重新生成槽位
            for (int index = 0; index < members.Length; index++)
            {
                AniSquadCommandMember commandMember = members[index];
                // 旧槽位按 Entity 匹配，不依赖这次指令中的成员顺序
                int previousSlot = FindPreviousSlot(previousMembers, commandMember.Ani);
                EnsureMemberComponents(
                    entityManager,
                    commandMember,
                    squadEntity,
                    squad.SquadId,
                    previousSlot);
            }

            // SetOrAdd 可能改变 Entity 结构，所以必须在调用结束后重新取得可写 DynamicBuffer
            DynamicBuffer<AniSquadMember> squadMembers =
                entityManager.GetBuffer<AniSquadMember>(squadEntity);
            squadMembers.Clear();

            // 队伍缓冲区使用排序后的成员快照，固定编号只在入口处计算一次
            // 清空后完整重建，避免上一次指令中未被选中的成员残留
            for (int index = 0; index < members.Length; index++)
            {
                AniSquadCommandMember commandMember = members[index];
                squadMembers.Add(new AniSquadMember
                {
                    Ani = commandMember.Ani,
                    StableId = commandMember.StableId,
                    SlotIndex = FindPreviousSlot(previousMembers, commandMember.Ani),
                    Role = commandMember.Role,
                });
            }

            entityManager.SetComponentData(squadEntity, command);

            // 目标、指令序号和玩家归属一起更新，避免规划系统读到新旧混合的状态
            UpdateAggregate(ref state, squadEntity, ref squad);

            // 无论新建还是复用队伍，都要更新成员版本，让阵型系统检查布局
            if (!reused)
            {
                squad.MemberVersion = 1;
            }
            else if (squad.MemberVersion == 0)
            {
                squad.MemberVersion = 1;
            }

            entityManager.SetComponentData(squadEntity, squad);
            // 从成员参数取全队都能满足的保守值，用来限制锚点速度和通行空间
            // 必须先补齐成员组件，才能正确汇总这些参数
            AniSquadPathState pathState = entityManager.GetComponentData<AniSquadPathState>(squadEntity);

            // 新指令从等待寻路开始，旧请求结果不能直接把它标记为完成
            pathState.Status = AniSquadMovementStatus.AwaitingPath;
            pathState.ResolvedTargetPosition = command.TargetPosition;
            pathState.SubmittedCommandSequence = 0;
            pathState.RepathCooldownTicks = 0;
            pathState.SettledTicks = 0;

            // 保留当前和已统计的请求版本，用于识别迟到的旧结果并避免重复统计
            entityManager.SetComponentData(squadEntity, pathState);

            AniSquadFormationState formation =
                entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            int requestedColumnCount = math.min(
                math.max(1, command.FormationColumnCount),
                math.max(1, members.Length));
            if (formation.Kind != command.Formation ||
                formation.ConfiguredColumnCount != requestedColumnCount ||
                formation.ColumnCount != requestedColumnCount ||
                !reused)
            {
                // 阵型类型或最大列数变化后，所有槽位相对位置都需要重算
                formation.Kind = command.Formation;
                formation.ConfiguredColumnCount = requestedColumnCount;
                formation.ColumnCount = requestedColumnCount;
                formation.DesiredColumnCount = requestedColumnCount;
                formation.WidthStableTicks = 0;
                formation.ClearanceVersion = 0;
                formation.LayoutVersion = 0;
                formation.AssignmentVersion = 0;
            }

            // 人员或阵型配置变化都会清除旧分配；保留旧布局版本可让阵型系统在下一帧重建
            formation.MemberVersion = squad.MemberVersion;
            entityManager.SetComponentData(squadEntity, formation);

            // 指令内容已经转移到队伍 Entity，后续系统只读取队伍组件，不再需要原指令 Entity
            entityManager.DestroyEntity(commandEntity);
            previousMembers.Dispose();
        }

        private static NativeList<AniSquadCommandMember> CollectValidMembers(
            ref SystemState state,
            DynamicBuffer<AniSquadCommandMember> source)
        {
            NativeList<AniSquadCommandMember> members =
                new(math.max(1, source.Length), Allocator.Temp);

            // 指令入口已检查权限，这里仍会过滤已销毁的 Entity 和重复成员
            for (int index = 0; index < source.Length; index++)
            {
                AniSquadCommandMember member = source[index];
                // 没有 Transform 的 Entity 无法参与队伍中心和槽位计算，因此不能加入队伍
                if (member.Ani == Entity.Null ||
                    !state.EntityManager.Exists(member.Ani) ||
                    !state.EntityManager.HasComponent<LocalTransform>(member.Ani) ||
                    ContainsMember(members, member.Ani))
                {
                    continue;
                }

                members.Add(member);
            }

            return members;
        }

        private Entity FindReusableSquad(
            ref SystemState state,
            AniSquadCommand command,
            NativeList<AniSquadCommandMember> members)
        {
            Entity candidate = Entity.Null;

            // 只有所有成员原本就在同一个队伍中，才能直接更新该队伍
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                // 成员没有有效队伍归属时，不能复用旧的寻路数据
                if (!state.EntityManager.HasComponent<AniSquadMembership>(aniEntity))
                {
                    return Entity.Null;
                }

                Entity memberSquad = state.EntityManager.GetComponentData<AniSquadMembership>(aniEntity).Squad;
                if (memberSquad == Entity.Null || !state.EntityManager.Exists(memberSquad))
                {
                    return Entity.Null;
                }

                if (candidate == Entity.Null)
                {
                    candidate = memberSquad;
                }
                else if (candidate != memberSquad)
                {
                    // 新指令合并了多个旧队伍时，需要创建新的队伍上下文
                    return Entity.Null;
                }
            }

            if (candidate == Entity.Null ||
                !state.EntityManager.HasComponent<AniSquad>(candidate) ||
                state.EntityManager.GetComponentData<AniSquad>(candidate).OwnerNetworkId !=
                command.OwnerNetworkId)
            {
                return Entity.Null;
            }

            // 人数变化会改变阵型和 Flow Field 的适用范围，因此创建新的队伍上下文
            DynamicBuffer<AniSquadMember> currentMembers =
                state.EntityManager.GetBuffer<AniSquadMember>(candidate);
            // 人数相同也必须确认所有成员来自同一队伍，不能只凭数量复用
            return currentMembers.Length == members.Length ? candidate : Entity.Null;
        }

        private Entity CreateSquad(
            ref SystemState state,
            AniSquadCommand command,
            NativeList<AniSquadCommandMember> members)
        {
            EntityManager entityManager = state.EntityManager;

            // 队伍 Entity 集中保存路径、Flow Field、锚点和阵型，避免每名成员重复计算同一条路线
            Entity squadEntity = entityManager.CreateEntity(
                typeof(AniSquad),
                typeof(AniSquadCommand),
                typeof(AniSquadPathState),
                typeof(AniSquadAnchor),
                typeof(AniSquadFormationState),
                typeof(NavigationFlowFieldRequest),
                typeof(NavigationFlowFieldState));
            entityManager.AddBuffer<AniSquadMember>(squadEntity);
            entityManager.AddBuffer<AniFormationSlot>(squadEntity);
            entityManager.AddBuffer<NavigationCorridorCluster>(squadEntity);
            entityManager.AddBuffer<NavigationCorridorPortal>(squadEntity);
            entityManager.AddBuffer<NavigationHierarchicalWaypoint>(squadEntity);
            entityManager.AddBuffer<NavigationFlowFieldCell>(squadEntity);

            // 这些缓冲区随队伍 Entity 一起创建和销毁，不直接挂到单个 Ani 上

            float3 center = CalculateMemberCenter(ref state, members);

            // 锚点从全体成员的中心开始，避免因随意选择队长而在首帧跳动
            entityManager.SetComponentData(squadEntity, new AniSquad
            {
                SquadId = NextSquadId(),
                OwnerNetworkId = command.OwnerNetworkId,
                MemberVersion = 1,
                MaximumAgentRadius = 0f,
                MinimumMaxSpeed = 0f,
                MinimumMaxAcceleration = 0f,
            });
            entityManager.SetComponentData(squadEntity, command);
            entityManager.SetComponentData(squadEntity, new AniSquadPathState
            {
                Status = AniSquadMovementStatus.AwaitingPath,
                ResolvedTargetPosition = command.TargetPosition,
            });
            entityManager.SetComponentData(squadEntity, new AniSquadAnchor
            {
                Position = center,
                Forward = math.normalizesafe(command.DesiredForward, new float3(0f, 0f, 1f)),
                CurrentCellIndex = -1,
            });
            entityManager.SetComponentData(squadEntity, new AniSquadFormationState
            {
                Kind = command.Formation,
                ConfiguredColumnCount = math.min(
                    math.max(1, command.FormationColumnCount),
                    math.max(1, members.Length)),
                ColumnCount = math.min(
                    math.max(1, command.FormationColumnCount),
                    math.max(1, members.Length)),
                DesiredColumnCount = math.min(
                    math.max(1, command.FormationColumnCount),
                    math.max(1, members.Length)),
                MemberVersion = 1,
            });
            entityManager.SetComponentData(squadEntity, default(NavigationFlowFieldRequest));

            // None 表示尚未提交 Flow Field 请求，不会被误认为已有结果
            entityManager.SetComponentData(squadEntity, new NavigationFlowFieldState
            {
                Status = NavigationPathStatus.None,
                ProjectedStartCellIndex = -1,
                ProjectedEndCellIndex = -1,
            });
            return squadEntity;
        }

        private void DetachMembersFromOtherSquads(
            ref SystemState state,
            Entity destinationSquad,
            NativeList<AniSquadCommandMember> members)
        {
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;

                // 成员已经属于目标队伍时无需迁移，也不必更新旧队伍版本
                if (!state.EntityManager.HasComponent<AniSquadMembership>(aniEntity))
                {
                    continue;
                }

                Entity oldSquad = state.EntityManager.GetComponentData<AniSquadMembership>(aniEntity).Squad;

                // 一名 Ani 只能属于一个队伍，加入新队伍前先从旧队伍缓冲区移除
            if (oldSquad == Entity.Null || oldSquad == destinationSquad ||
                    !state.EntityManager.Exists(oldSquad) ||
                    !state.EntityManager.HasBuffer<AniSquadMember>(oldSquad))
            {
                // 无效的旧归属会由清理流程处理，这里无需重复修改
                continue;
                }

                DynamicBuffer<AniSquadMember> oldMembers =
                    state.EntityManager.GetBuffer<AniSquadMember>(oldSquad);
                // 迁移阶段只清理旧队伍；目标队伍缓冲区稍后会按新指令完整重建
                RemoveMember(oldMembers, aniEntity);
                if (oldMembers.IsEmpty)
                {
                    // 旧队伍没有成员后立即销毁，同时释放它的寻路数据
                    state.EntityManager.DestroyEntity(oldSquad);
                    continue;
                }

                AniSquad oldSquadData = state.EntityManager.GetComponentData<AniSquad>(oldSquad);

                // 旧队伍还有成员时只需重新排阵，不需要更换队伍身份
                oldSquadData.MemberVersion++;
                UpdateAggregate(ref state, oldSquad, ref oldSquadData);
                state.EntityManager.SetComponentData(oldSquad, oldSquadData);
                MarkFormationMembersChanged(
                    state.EntityManager,
                    oldSquad,
                    oldSquadData.MemberVersion);
            }
        }

        private static void EnsureMemberComponents(
            EntityManager entityManager,
            AniSquadCommandMember commandMember,
            Entity squadEntity,
            uint squadId,
            int slotIndex)
        {
            // 将移动参数复制到导航组件，避免移动过程中直接依赖可能随时变化的玩法属性
            var membership = new AniSquadMembership
            {
                Squad = squadEntity,
                SquadId = squadId,
                SlotIndex = slotIndex,
            };
            SetOrAdd(entityManager, commandMember.Ani, membership);
            // 最大加速度至少为 1，防止错误的零配置让成员永远无法启动或追上槽位
            SetOrAdd(entityManager, commandMember.Ani, new AniMovementConfig
            {
                MaxSpeed = commandMember.MaxSpeed,
                MaxAcceleration = math.max(1f, commandMember.MaxAcceleration),
                AgentRadius = math.max(0.01f, commandMember.AgentRadius),
                ArrivalRadius = 0.7f,
                RotationSpeedRadians = math.radians(540f),
            });
            SetOrAdd(entityManager, commandMember.Ani, new AniSlotTarget());
            SetOrAdd(entityManager, commandMember.Ani, new AniPreferredVelocity());

            // 位置目标、期望速度和移动结果分别由阵型、移动和进度流程使用，缺少任何一个都会中断移动
            SetOrAdd(entityManager, commandMember.Ani, new AniMovementResult());
        }

        private static float3 CalculateMemberCenter(
            ref SystemState state,
            NativeList<AniSquadCommandMember> members)
        {
            float3 center = float3.zero;
            int count = 0;

            // 初始锚点取所有有效成员的平均位置，已销毁的 Entity 不参与计算
            for (int index = 0; index < members.Length; index++)
            {
                Entity entity = members[index].Ani;
                if (!state.EntityManager.Exists(entity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(entity))
                {
                    continue;
                }

                center += state.EntityManager.GetComponentData<LocalTransform>(entity).Position;
                count++;
            }

            // 空列表只会来自异常输入，此时返回零坐标作为明确的默认值
            return count == 0 ? float3.zero : center / count;
        }

        private static void UpdateAggregate(
            ref SystemState state,
            Entity squadEntity,
            ref AniSquad squad)
        {
            DynamicBuffer<AniSquadMember> members =
                state.EntityManager.GetBuffer<AniSquadMember>(squadEntity);
            float maximumRadius = 0f;
            float minimumSpeed = float.PositiveInfinity;
            float minimumAcceleration = float.PositiveInfinity;

            // 速度和加速度取全队最小值，半径取最大值，确保路线对每名成员都安全可用
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                if (!state.EntityManager.HasComponent<AniMovementConfig>(aniEntity))
                {
                    // 成员配置尚未准备好时跳过，避免默认零值错误限制整支队伍
                    continue;
                }

                AniMovementConfig config = state.EntityManager.GetComponentData<AniMovementConfig>(aniEntity);
                maximumRadius = math.max(maximumRadius, config.AgentRadius);
                minimumSpeed = math.min(minimumSpeed, config.MaxSpeed);
                minimumAcceleration = math.min(minimumAcceleration, config.MaxAcceleration);
            }

            // 无穷值表示没有可汇总的成员，此时改为零，让移动系统自然保持停止
            squad.MaximumAgentRadius = maximumRadius;
            squad.MinimumMaxSpeed = float.IsInfinity(minimumSpeed) ? 0f : minimumSpeed;
            squad.MinimumMaxAcceleration =
                float.IsInfinity(minimumAcceleration) ? 0f : minimumAcceleration;
        }

        private static int FindPreviousSlot(
            NativeList<AniSquadMember> previousMembers,
            Entity aniEntity)
        {
            // 队伍人数有限，直接扫描旧槽位比额外创建映射表更省内存
            for (int index = 0; index < previousMembers.Length; index++)
            {
                if (previousMembers[index].Ani == aniEntity)
                {
                    return previousMembers[index].SlotIndex;
                }
            }

            return -1;
        }

        private static bool ContainsMember(
            NativeList<AniSquadCommandMember> members,
            Entity aniEntity)
        {
            // 指令可能重复引用同一 Entity，加入前扫描一次，确保队伍中每名成员只出现一次
            for (int index = 0; index < members.Length; index++)
            {
                if (members[index].Ani == aniEntity)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveMember(
            DynamicBuffer<AniSquadMember> members,
            Entity aniEntity)
        {
            // 找到并删除后立即返回；调用方随后会判断旧队伍是否已经为空
            for (int index = members.Length - 1; index >= 0; index--)
            {
                if (members[index].Ani == aniEntity)
                {
                    members.RemoveAt(index);
                    return;
                }
            }
        }

        private static void SortCommands(ref SystemState state, NativeArray<Entity> commands)
        {
            // 同帧指令通常很少，使用原地插入排序即可避免额外分配
            for (int index = 1; index < commands.Length; index++)
            {
                Entity value = commands[index];
                uint valueSequence = state.EntityManager.GetComponentData<AniSquadCommand>(value).Sequence;
                int insertion = index - 1;
                while (insertion >= 0 &&
                       state.EntityManager.GetComponentData<AniSquadCommand>(commands[insertion]).Sequence >
                       valueSequence)
                {
                    commands[insertion + 1] = commands[insertion];
                    insertion--;
                }

                commands[insertion + 1] = value;
            }
        }

        private static void SortMembers(NativeList<AniSquadCommandMember> members)
        {
            // 队伍人数有限，原地插入排序既不额外分配，也能得到固定顺序
            for (int index = 1; index < members.Length; index++)
            {
                AniSquadCommandMember value = members[index];
                int insertion = index - 1;
                while (insertion >= 0 &&
                       IsAfter(members[insertion], value))
                {
                    members[insertion + 1] = members[insertion];
                    insertion--;
                }

                members[insertion + 1] = value;
            }
        }

        private static bool IsAfter(
            AniSquadCommandMember left,
            AniSquadCommandMember right)
        {
            // StableId 用于在回放中保持同一顺序；异常重复时再用 Entity.Index 决定先后
            return left.StableId > right.StableId ||
                   (left.StableId == right.StableId && left.Ani.Index > right.Ani.Index);
        }

        private uint NextSquadId()
        {
            uint value = _nextSquadId++;

            // 0 保留为无效编号，计数器溢出后会跳过它
            if (_nextSquadId == 0)
            {
                _nextSquadId = 1;
            }

            return value == 0 ? NextSquadId() : value;
        }

        private static void SetOrAdd<T>(EntityManager entityManager, Entity entity, T value)
            where T : unmanaged, IComponentData
        {
            // 组件已存在时只更新值，缺少时才改变 Entity 结构，减少 Archetype 迁移
            if (entityManager.HasComponent<T>(entity))
            {
                entityManager.SetComponentData(entity, value);
            }
            else
            {
                // 组件补齐后，后续指令可以直接复用同一个 Archetype
                entityManager.AddComponentData(entity, value);
            }
        }

        private static void MarkFormationMembersChanged(
            EntityManager entityManager,
            Entity squadEntity,
            uint memberVersion)
        {
            if (!entityManager.HasComponent<AniSquadFormationState>(squadEntity))
            {
                return;
            }

            // 清空分配版本，让阵型系统在下一帧为变化后的成员重新匹配槽位
            AniSquadFormationState formation =
                entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            formation.MemberVersion = memberVersion;
            formation.AssignmentVersion = 0;

            // 保留旧布局版本，使布局系统能发现版本不一致并重建槽位
            entityManager.SetComponentData(squadEntity, formation);
        }
    }
}
