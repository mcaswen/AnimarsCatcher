using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 将统一订单转换为服务器 Squad，并维护成员和路径上下文生命周期
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridRuntimeSystemGroup), OrderFirst = true)]
    public partial struct AniSquadLifecycleSystem : ISystem
    {
        private EntityQuery _orderQuery;
        private EntityQuery _squadQuery;
        private uint _nextSquadId;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();

            // 订单和 Squad 查询长期复用，避免每个 Tick 重建查询描述
            _orderQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AniSquadOrderRequest>(),
                ComponentType.ReadOnly<AniSquadOrder>(),
                ComponentType.ReadOnly<AniSquadOrderMember>());
            _squadQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<AniSquad>(),
                ComponentType.ReadWrite<AniSquadOrder>(),
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
            // 先清理失效成员，再按序消费新订单，保证同 Tick 不会把已死亡成员重新挂回旧 Squad
            // Cleanup 可能销毁上下文，因此必须在订单快照创建前完成
            CleanupSquads(ref state);

            using NativeArray<Entity> orders = _orderQuery.ToEntityArray(Allocator.Temp);

            // RPC 和 Benchmark 都可能同 Tick 到达，序号排序维持跨输入源的确定性
            // 快照数组允许下面的消费逻辑销毁订单实体而不改变遍历边界
            SortOrders(ref state, orders);
            for (int index = 0; index < orders.Length; index++)
            {
                if (state.EntityManager.Exists(orders[index]))
                {
                    ApplyOrder(ref state, orders[index]);
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

        private void ApplyOrder(ref SystemState state, Entity orderEntity)
        {
            EntityManager entityManager = state.EntityManager;

            // 订单组件在本方法内只读一次，后续写回统一进入 Squad 实体
            AniSquadOrder order = entityManager.GetComponentData<AniSquadOrder>(orderEntity);

            // 先把订单成员复制到临时列表，后续组件补齐会引起结构变更
            // 复制也隔离了订单实体销毁对源 Buffer 的影响
            DynamicBuffer<AniSquadOrderMember> orderMembers =
                entityManager.GetBuffer<AniSquadOrderMember>(orderEntity);
            using NativeList<AniSquadOrderMember> members =
                CollectValidMembers(ref state, orderMembers);

            if (members.IsEmpty)
            {
                // 订单全部指向失效实体时直接消费，避免生成空 Squad
                // 空订单不应推进 SquadId 或路径请求版本
                entityManager.DestroyEntity(orderEntity);
                return;
            }

            SortMembers(members);

            // 排序后再寻找可复用 Squad，保证同一成员集合的比较顺序一致
            // 只有成员仍属于同一拥有者且数量一致时才能复用原路径上下文
            Entity squadEntity = FindReusableSquad(ref state, order, members);
            bool reused = squadEntity != Entity.Null;

            // 复用判断完成后保存旧成员槽位，创建新 Squad 则不需要历史映射
            // NativeList 的容量按当前成员数设置，避免大规模临时过度分配
            NativeList<AniSquadMember> previousMembers =
                new(math.max(1, members.Length), Allocator.Temp);

            if (!reused)
            {
                squadEntity = CreateSquad(ref state, order, members);
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
                AniSquadOrderMember orderMember = members[index];
                // 旧槽位只按 Entity 匹配，不依赖本次订单传入顺序
                int previousSlot = FindPreviousSlot(previousMembers, orderMember.Ani);
                EnsureMemberComponents(
                    entityManager,
                    orderMember,
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
            // Clear 后完整重建 Buffer，避免旧订单残留未选中的成员
            for (int index = 0; index < members.Length; index++)
            {
                AniSquadOrderMember orderMember = members[index];
                squadMembers.Add(new AniSquadMember
                {
                    Ani = orderMember.Ani,
                    StableId = orderMember.StableId,
                    SlotIndex = FindPreviousSlot(previousMembers, orderMember.Ani),
                });
            }

            entityManager.SetComponentData(squadEntity, order);

            // 订单目标、序号和拥有者在同一写回点替换，避免规划系统读到混合状态
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

            // 新订单从 AwaitingPath 开始，旧请求结果不能直接标记为当前订单完成
            pathState.Status = AniSquadMovementStatus.AwaitingPath;
            pathState.ResolvedTargetPosition = order.TargetPosition;
            pathState.SubmittedOrderSequence = 0;
            pathState.RepathCooldownTicks = 0;
            pathState.SettledTicks = 0;

            // ActiveRequestVersion 保留用于识别旧 Field 结果，不能随订单直接清零
            // CountedRequestVersion 同样保留，避免重复统计已完成请求
            entityManager.SetComponentData(squadEntity, pathState);

            AniSquadFormationState formation =
                entityManager.GetComponentData<AniSquadFormationState>(squadEntity);
            int requestedColumnCount = math.min(
                math.max(1, order.FormationColumnCount),
                math.max(1, members.Length));
            if (formation.Kind != order.Formation ||
                formation.ColumnCount != requestedColumnCount ||
                !reused)
            {
                // 阵型类型或列数变化会使所有局部偏移失效
                formation.Kind = order.Formation;
                formation.ColumnCount = requestedColumnCount;
                formation.LayoutVersion = 0;
                formation.AssignmentVersion = 0;
            }

            // 成员变化总是触发布局，订单只改变阵型配置时也会清空旧分配
            // LayoutVersion 保持旧值，确保布局系统在下一更新中重新生成槽位
            formation.MemberVersion = squad.MemberVersion;
            entityManager.SetComponentData(squadEntity, formation);

            // 订单实体的所有数据已转移到 Squad，之后由生命周期系统统一维护
            // 之后的规划系统只查询 Squad 组件，不再依赖订单实体是否存在
            entityManager.DestroyEntity(orderEntity);
            previousMembers.Dispose();
        }

        private static NativeList<AniSquadOrderMember> CollectValidMembers(
            ref SystemState state,
            DynamicBuffer<AniSquadOrderMember> source)
        {
            NativeList<AniSquadOrderMember> members =
                new(math.max(1, source.Length), Allocator.Temp);

            // 入口已经做过权限校验，这里仍需防御死亡实体和重复引用
            for (int index = 0; index < source.Length; index++)
            {
                AniSquadOrderMember member = source[index];
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
            AniSquadOrder order,
            NativeList<AniSquadOrderMember> members)
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
                    // 一个新订单横跨多个旧 Squad 时必须重新聚合
                    return Entity.Null;
                }
            }

            if (candidate == Entity.Null ||
                !state.EntityManager.HasComponent<AniSquad>(candidate) ||
                state.EntityManager.GetComponentData<AniSquad>(candidate).OwnerNetworkId !=
                order.OwnerNetworkId)
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
            AniSquadOrder order,
            NativeList<AniSquadOrderMember> members)
        {
            EntityManager entityManager = state.EntityManager;

            // Squad 聚合实体同时持有路径、Field、Anchor 和阵型 Buffer，避免按成员重复建图
            Entity squadEntity = entityManager.CreateEntity(
                typeof(AniSquad),
                typeof(AniSquadOrder),
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
                OwnerNetworkId = order.OwnerNetworkId,
                MemberVersion = 1,
                MaximumAgentRadius = 0f,
                MinimumMaxSpeed = 0f,
                MinimumMaxAcceleration = 0f,
            });
            entityManager.SetComponentData(squadEntity, order);
            entityManager.SetComponentData(squadEntity, new AniSquadPathState
            {
                Status = AniSquadMovementStatus.AwaitingPath,
                ResolvedTargetPosition = order.TargetPosition,
            });
            entityManager.SetComponentData(squadEntity, new AniSquadAnchor
            {
                Position = center,
                Forward = math.normalizesafe(order.DesiredForward, new float3(0f, 0f, 1f)),
                CurrentCellIndex = -1,
            });
            entityManager.SetComponentData(squadEntity, new AniSquadFormationState
            {
                Kind = order.Formation,
                ColumnCount = math.min(
                    math.max(1, order.FormationColumnCount),
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
            NativeList<AniSquadOrderMember> members)
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
                // 迁移只修改旧 Buffer，目标 Buffer 会在 ApplyOrder 的结构变更后统一重建
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
            AniSquadOrderMember orderMember,
            Entity squadEntity,
            uint squadId,
            int slotIndex)
        {
            // 成员组件由订单快照统一写入，避免移动系统依赖玩法属性的瞬时修改
            var membership = new AniSquadMembership
            {
                Squad = squadEntity,
                SquadId = squadId,
                SlotIndex = slotIndex,
            };
            SetOrAdd(entityManager, orderMember.Ani, membership);
            // MaxAcceleration 至少为一，防止零配置让成员永远无法追上槽位
            SetOrAdd(entityManager, orderMember.Ani, new AniMovementConfig
            {
                MaxSpeed = orderMember.MaxSpeed,
                MaxAcceleration = math.max(1f, orderMember.MaxAcceleration),
                AgentRadius = math.max(0.01f, orderMember.AgentRadius),
                ArrivalRadius = 0.7f,
                RotationSpeedRadians = math.radians(540f),
            });
            SetOrAdd(entityManager, orderMember.Ani, new AniSlotTarget());
            SetOrAdd(entityManager, orderMember.Ani, new AniPreferredVelocity());

            // 这些组件由 Commit、Progress 和阵型系统共同维护，缺一都会阻断成员链路
            SetOrAdd(entityManager, orderMember.Ani, new AniMovementResult());
        }

        private static float3 CalculateMemberCenter(
            ref SystemState state,
            NativeList<AniSquadOrderMember> members)
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

            // 空列表只可能来自异常订单，零中心让失败路径保持确定性
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
            NativeList<AniSquadOrderMember> members,
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

        private static void SortOrders(ref SystemState state, NativeArray<Entity> orders)
        {
            // 订单数量通常很小，插入排序可保持 NativeArray 原地且无额外分配
            for (int index = 1; index < orders.Length; index++)
            {
                Entity value = orders[index];
                uint valueSequence = state.EntityManager.GetComponentData<AniSquadOrder>(value).Sequence;
                int insertion = index - 1;
                while (insertion >= 0 &&
                       state.EntityManager.GetComponentData<AniSquadOrder>(orders[insertion]).Sequence >
                       valueSequence)
                {
                    orders[insertion + 1] = orders[insertion];
                    insertion--;
                }

                orders[insertion + 1] = value;
            }
        }

        private static void SortMembers(NativeList<AniSquadOrderMember> members)
        {
            // 成员规模较小且需要稳定结果，插入排序避免 NativeList 额外分配
            for (int index = 1; index < members.Length; index++)
            {
                AniSquadOrderMember value = members[index];
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
            AniSquadOrderMember left,
            AniSquadOrderMember right)
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
                // 结构变更只发生一次，后续订单复用同一 Archetype
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
