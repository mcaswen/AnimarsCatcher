using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将统一指令转换为服务器 Squad，并维护成员和路径上下文生命周期
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

            // 指令和 Squad 查询长期复用，避免每个 Tick 重建查询描述
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

            // 零保留给未初始化的身份，实际 SquadId 从一开始递增
            _nextSquadId = 1;
        }

        public void OnUpdate(ref SystemState state)
        {
            // 先清理失效成员，再按序消费新指令，保证同 Tick 不会把已死亡成员重新挂回旧 Squad
            // Cleanup 可能销毁上下文，因此必须在指令快照创建前完成
            CleanupSquads(ref state);

            using NativeArray<Entity> commands = _commandQuery.ToEntityArray(Allocator.Temp);

            // RPC 和 Benchmark 都可能同 Tick 到达，序号排序维持跨输入源的确定性
            // 快照数组允许下面的消费逻辑销毁指令实体而不改变遍历边界
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
            // 该快照不持有可写 Buffer，结构变更只通过 EntityManager 发生
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

                // 倒序移除避免 DynamicBuffer 紧缩后跳过下一个成员
                for (int memberIndex = members.Length - 1; memberIndex >= 0; memberIndex--)
                {
                    Entity aniEntity = members[memberIndex].Ani;

                    // 归属组件和 Buffer 必须双向指向同一 Squad，任一侧失效都要移除
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
                    // 最后一个有效成员离开后，路径、Field 和阵型上下文没有保留价值
                    state.EntityManager.DestroyEntity(squadEntity);
                    continue;
                }

                if (changed)
                {
                    // 成员版本失效所有旧槽位分配，但保留可复用的 Squad 身份
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

            // 指令组件在本方法内只读一次，后续写回统一进入 Squad 实体
            AniSquadCommand command = entityManager.GetComponentData<AniSquadCommand>(commandEntity);

            // 先把指令成员复制到临时列表，后续组件补齐会引起结构变更
            // 复制也隔离了指令实体销毁对源 Buffer 的影响
            DynamicBuffer<AniSquadCommandMember> commandMembers =
                entityManager.GetBuffer<AniSquadCommandMember>(commandEntity);
            using NativeList<AniSquadCommandMember> members =
                CollectValidMembers(ref state, commandMembers);

            if (members.IsEmpty)
            {
                // 指令全部指向失效实体时直接消费，避免生成空 Squad
                // 空指令不应推进 SquadId 或路径请求版本
                entityManager.DestroyEntity(commandEntity);
                return;
            }

            SortMembers(members);

            // 排序后再寻找可复用 Squad，保证同一成员集合的比较顺序一致
            // 只有成员仍属于同一拥有者且数量一致时才能复用原路径上下文
            Entity squadEntity = FindReusableSquad(ref state, command, members);
            bool reused = squadEntity != Entity.Null;

            // 复用判断完成后保存旧成员槽位，创建新 Squad 则不需要历史映射
            // NativeList 的容量按当前成员数设置，避免大规模临时过度分配
            NativeList<AniSquadMember> previousMembers =
                new(math.max(1, members.Length), Allocator.Temp);

            if (!reused)
            {
                squadEntity = CreateSquad(ref state, command, members);
            }
            else
            {
                // 保留旧槽位索引，成员顺序变化时仍尽量维持画面稳定
                DynamicBuffer<AniSquadMember> oldMembers =
                    entityManager.GetBuffer<AniSquadMember>(squadEntity);
                for (int index = 0; index < oldMembers.Length; index++)
                {
                    previousMembers.Add(oldMembers[index]);
                }
            }

            DetachMembersFromOtherSquads(ref state, squadEntity, members);

            // Detach 之后再补齐目标组件，旧 Squad 的版本更新不会覆盖新归属

            AniSquad squad = entityManager.GetComponentData<AniSquad>(squadEntity);

            // 先补齐成员组件，再重新获取 Squad Buffer，避免结构变更使句柄失效
            for (int index = 0; index < members.Length; index++)
            {
                AniSquadCommandMember commandMember = members[index];
                // 旧槽位只按 Entity 匹配，不依赖本次指令传入顺序
                int previousSlot = FindPreviousSlot(previousMembers, commandMember.Ani);
                EnsureMemberComponents(
                    entityManager,
                    commandMember,
                    squadEntity,
                    squad.SquadId,
                    previousSlot);
            }

            // 成员补组件会产生结构变更，完成后再获取可写 Buffer 句柄
            // 这是 DynamicBuffer 生命周期约束，不能把句柄跨过 SetOrAdd 调用保存
            DynamicBuffer<AniSquadMember> squadMembers =
                entityManager.GetBuffer<AniSquadMember>(squadEntity);
            squadMembers.Clear();

            // Buffer 采用排序后的成员快照，稳定键只在入口生成一次
            // Clear 后完整重建 Buffer，避免旧指令残留未选中的成员
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

            // 指令目标、序号和拥有者在同一写回点替换，避免规划系统读到混合状态
            UpdateAggregate(ref state, squadEntity, ref squad);

            // 新 Squad 和复用 Squad 都需要非零 MemberVersion，供阵型系统触发布局
            if (!reused)
            {
                squad.MemberVersion = 1;
            }
            else if (squad.MemberVersion == 0)
            {
                squad.MemberVersion = 1;
            }

            entityManager.SetComponentData(squadEntity, squad);
            // Aggregate 使用成员配置的保守边界，供 Anchor 统一限制速度
            // 该聚合必须发生在成员组件补齐之后
            AniSquadPathState pathState = entityManager.GetComponentData<AniSquadPathState>(squadEntity);

            // 新指令从 AwaitingPath 开始，旧请求结果不能直接标记为当前指令完成
            pathState.Status = AniSquadMovementStatus.AwaitingPath;
            pathState.ResolvedTargetPosition = command.TargetPosition;
            pathState.SubmittedCommandSequence = 0;
            pathState.RepathCooldownTicks = 0;
            pathState.SettledTicks = 0;

            // ActiveRequestVersion 保留用于识别旧 Field 结果，不能随指令直接清零
            // CountedRequestVersion 同样保留，避免重复统计已完成请求
            entityManager.SetComponentData(squadEntity, pathState);

            AniSquadFormationState formation =
                entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            int requestedColumnCount = math.min(
                math.max(1, command.FormationColumnCount),
                math.max(1, members.Length));
            if (formation.Kind != command.Formation ||
                formation.ColumnCount != requestedColumnCount ||
                !reused)
            {
                // 阵型类型或列数变化会使所有局部偏移失效
                formation.Kind = command.Formation;
                formation.ColumnCount = requestedColumnCount;
                formation.DesiredColumnCount = requestedColumnCount;
                formation.WidthStableTicks = 0;
                formation.ClearanceVersion = 0;
                formation.LayoutVersion = 0;
                formation.AssignmentVersion = 0;
            }

            // 成员变化总是触发布局，指令只改变阵型配置时也会清空旧分配
            // LayoutVersion 保持旧值，确保布局系统在下一更新中重新生成槽位
            formation.MemberVersion = squad.MemberVersion;
            entityManager.SetComponentData(squadEntity, formation);

            // 指令实体的所有数据已转移到 Squad，之后由生命周期系统统一维护
            // 之后的规划系统只查询 Squad 组件，不再依赖指令实体是否存在
            entityManager.DestroyEntity(commandEntity);
            previousMembers.Dispose();
        }

        private static NativeList<AniSquadCommandMember> CollectValidMembers(
            ref SystemState state,
            DynamicBuffer<AniSquadCommandMember> source)
        {
            NativeList<AniSquadCommandMember> members =
                new(math.max(1, source.Length), Allocator.Temp);

            // 入口已经做过权限校验，这里仍需防御死亡实体和重复引用
            for (int index = 0; index < source.Length; index++)
            {
                AniSquadCommandMember member = source[index];
                // 缺少 Transform 的实体无法计算中心和槽位，不能进入 Squad
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

            // 只有所有成员都指向同一个旧 Squad 才能更新原上下文
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                // 缺少归属或归属已销毁时无法复用旧路径上下文
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
                    // 一个新指令横跨多个旧 Squad 时必须重新聚合
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

            // 成员数量变化意味着旧阵型和 Field 语义已改变，应创建新的 Squad
            DynamicBuffer<AniSquadMember> currentMembers =
                state.EntityManager.GetBuffer<AniSquadMember>(candidate);
            // 数量相等仍需依赖前面的同 Squad 检查，避免部分成员误复用
            return currentMembers.Length == members.Length ? candidate : Entity.Null;
        }

        private Entity CreateSquad(
            ref SystemState state,
            AniSquadCommand command,
            NativeList<AniSquadCommandMember> members)
        {
            EntityManager entityManager = state.EntityManager;

            // Squad 聚合实体同时持有路径、Field、Anchor 和阵型 Buffer，避免按成员重复建图
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

            // Buffer 属于 Squad 生命周期，成员数量变化不会复制这些路径容器
            // 这些 Buffer 的所有权始终跟随 Squad，不直接挂在 Ani 上

            float3 center = CalculateMemberCenter(ref state, members);

            // Anchor 初始位置取成员中心，首帧不会因队长选择产生瞬移
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
                ColumnCount = math.min(
                    math.max(1, command.FormationColumnCount),
                    math.max(1, members.Length)),
                DesiredColumnCount = math.min(
                    math.max(1, command.FormationColumnCount),
                    math.max(1, members.Length)),
                MemberVersion = 1,
            });
            entityManager.SetComponentData(squadEntity, default(NavigationFlowFieldRequest));

            // None 状态表示尚未提交请求，避免误读未初始化的 Field 结果
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

                // 已经属于目标 Squad 的成员无需迁移，避免无意义版本递增
                if (!state.EntityManager.HasComponent<AniSquadMembership>(aniEntity))
                {
                    continue;
                }

                Entity oldSquad = state.EntityManager.GetComponentData<AniSquadMembership>(aniEntity).Squad;

                // 一个 Ani 只能属于一个 Squad，迁移前从旧 Buffer 中移除
            if (oldSquad == Entity.Null || oldSquad == destinationSquad ||
                    !state.EntityManager.Exists(oldSquad) ||
                    !state.EntityManager.HasBuffer<AniSquadMember>(oldSquad))
            {
                // 无效旧归属已由 Cleanup 负责处理，当前迁移无需重复操作
                continue;
                }

                DynamicBuffer<AniSquadMember> oldMembers =
                    state.EntityManager.GetBuffer<AniSquadMember>(oldSquad);
                // 迁移只修改旧 Buffer，目标 Buffer 会在 ConsumeCommand 的结构变更后统一重建
                RemoveMember(oldMembers, aniEntity);
                if (oldMembers.IsEmpty)
                {
                    // 迁移掏空旧 Squad 时立即释放其路径上下文
                    state.EntityManager.DestroyEntity(oldSquad);
                    continue;
                }

                AniSquad oldSquadData = state.EntityManager.GetComponentData<AniSquad>(oldSquad);

                // 旧 Squad 剩余成员需要重新布局，但不应更换 Squad 身份
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
            // 成员组件由指令快照统一写入，避免移动系统依赖玩法属性的瞬时修改
            var membership = new AniSquadMembership
            {
                Squad = squadEntity,
                SquadId = squadId,
                SlotIndex = slotIndex,
            };
            SetOrAdd(entityManager, commandMember.Ani, membership);
            // MaxAcceleration 至少为一，防止零配置让成员永远无法追上槽位
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

            // 这些组件由 Commit、Progress 和阵型系统共同维护，缺一都会阻断成员链路
            SetOrAdd(entityManager, commandMember.Ani, new AniMovementResult());
        }

        private static float3 CalculateMemberCenter(
            ref SystemState state,
            NativeList<AniSquadCommandMember> members)
        {
            float3 center = float3.zero;
            int count = 0;

            // 初始 Anchor 使用有效成员平均位置，死亡实体不参与几何中心
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

            // 空列表只可能来自异常指令，零中心让失败路径保持确定性
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

            // 聚合速度取成员下界、半径取成员上界，保证整个 Squad 都满足约束
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                if (!state.EntityManager.HasComponent<AniMovementConfig>(aniEntity))
                {
                    // 组件尚未补齐时不把默认零值混入 Squad 能力聚合
                    continue;
                }

                AniMovementConfig config = state.EntityManager.GetComponentData<AniMovementConfig>(aniEntity);
                maximumRadius = math.max(maximumRadius, config.AgentRadius);
                minimumSpeed = math.min(minimumSpeed, config.MaxSpeed);
                minimumAcceleration = math.min(minimumAcceleration, config.MaxAcceleration);
            }

            // Infinity 只表示没有可用成员，转换为零让后续速度系统自然停住
            squad.MaximumAgentRadius = maximumRadius;
            squad.MinimumMaxSpeed = float.IsInfinity(minimumSpeed) ? 0f : minimumSpeed;
            squad.MinimumMaxAcceleration =
                float.IsInfinity(minimumAcceleration) ? 0f : minimumAcceleration;
        }

        private static int FindPreviousSlot(
            NativeList<AniSquadMember> previousMembers,
            Entity aniEntity)
        {
            // 成员规模较少，线性查找旧槽位比建立临时映射更节省分配
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
            // 入口列表可能包含重复 Entity，显式扫描维持唯一成员不变量
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
            // 删除后立即返回，调用方会在外层处理旧 Squad 是否被掏空
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
            // 指令数量通常很小，插入排序可保持 NativeArray 原地且无额外分配
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
            // 成员规模较小且需要稳定结果，插入排序避免 NativeList 额外分配
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
            // StableId 是跨回放主键；相等时用 Entity.Index 消除异常输入的非确定性
            return left.StableId > right.StableId ||
                   (left.StableId == right.StableId && left.Ani.Index > right.Ani.Index);
        }

        private uint NextSquadId()
        {
            uint value = _nextSquadId++;

            // 零保留为无效引用，计数器环绕时跳过零
            if (_nextSquadId == 0)
            {
                _nextSquadId = 1;
            }

            return value == 0 ? NextSquadId() : value;
        }

        private static void SetOrAdd<T>(EntityManager entityManager, Entity entity, T value)
            where T : unmanaged, IComponentData
        {
            // 组件存在时只写值；不存在时才结构变更，减少 Archetype 迁移
            if (entityManager.HasComponent<T>(entity))
            {
                entityManager.SetComponentData(entity, value);
            }
            else
            {
                // 结构变更只发生一次，后续指令复用同一 Archetype
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

            // 清空 AssignmentVersion 使成员变化在下一帧重新匹配槽位
            AniSquadFormationState formation =
                entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            formation.MemberVersion = memberVersion;
            formation.AssignmentVersion = 0;

            // LayoutVersion 保持旧值，下一次布局系统会看到版本不一致并重建槽位
            entityManager.SetComponentData(squadEntity, formation);
        }
    }
}
