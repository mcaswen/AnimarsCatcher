using AnimarsCatcher.Core;
using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 在 Grid 后端验证 AniCommandRpc，并生成统一的 Squad 订单
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridOrderIngressSystemGroup))]
    public partial struct ServerAniOrderIngressSystem : ISystem
    {
        private const float DefaultAgentRadius = 0.35f;
        private const float DefaultFollowStoppingDistance = 2.5f;
        private const float DefaultFindStoppingDistance = 1.0f;

        private NativeParallelHashMap<int, Entity> _aniByGhostId;
        private uint _nextSequence;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();

            // GhostId 只在当前服务器 World 内有效，映射由本 System 持有并随生命周期释放
            _aniByGhostId = new NativeParallelHashMap<int, Entity>(256, Allocator.Persistent);
            _nextSequence = 1;
        }

        public void OnDestroy(ref SystemState state)
        {
            // OnCreate 可能在 World 提前销毁前完成，释放时必须容忍未创建状态
            if (_aniByGhostId.IsCreated)
            {
                _aniByGhostId.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // GhostId 会随 Ghost 生成和销毁变化，逐 Tick 重建可避免持有失效 Entity
            // 该表只服务本 Tick 的 RPC，不作为跨帧权威成员缓存
            _aniByGhostId.Clear();
            foreach (var (ghostInstance, entity) in
                     SystemAPI.Query<RefRO<GhostInstance>>()
                              .WithAll<AniAttributes>()
                              .WithEntityAccess())
            {
                // 重复 GhostId 不应覆盖先到实体，异常映射会在成员校验时被拒绝
                _aniByGhostId.TryAdd(ghostInstance.ValueRO.ghostId, entity);
            }

            // RPC 消费和订单创建都会改变 Archetype，延迟回放保持查询迭代稳定
            // Playback 前所有 NativeList 都必须完成 Dispose
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, receive, rpcEntity) in
                     SystemAPI.Query<RefRO<AniCommandRpc>, RefRO<ReceiveRpcCommandRequest>>()
                              .WithEntityAccess())
            {
                Entity sourceConnection = receive.ValueRO.SourceConnection;

                // 缺少 NetworkId 的请求没有可验证身份，必须消费掉而不能留待重试
                if (!SystemAPI.HasComponent<NetworkId>(sourceConnection))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int ownerNetworkId = SystemAPI.GetComponent<NetworkId>(sourceConnection).Value;

                // 先拒绝无效目标，避免为不可能执行的命令扫描和分配成员集合
                // Follow/Find 的实体有效性在这里检查一次，运行时再由 TargetResolve 追踪移动
                if (!TryResolveOrder(
                        ref state,
                        rpc.ValueRO,
                        ownerNetworkId,
                        out AniSquadOrder order))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                NativeParallelHashSet<Entity> selectedEntities =
                    new(math.max(1, rpc.ValueRO.SelectedAniGhostIds.Length), Allocator.Temp);
                NativeList<AniSquadOrderMember> members =
                    new(math.max(1, rpc.ValueRO.SelectedAniGhostIds.Length), Allocator.Temp);

                // HashSet 同时阻止恶意重复选择和重复成员破坏 Squad Buffer 不变量
                // NativeList 保留通过权限校验的顺序，后续再按 StableId 排序
                for (int index = 0; index < rpc.ValueRO.SelectedAniGhostIds.Length; index++)
                {
                    int ghostId = rpc.ValueRO.SelectedAniGhostIds[index];
                    if (!_aniByGhostId.TryGetValue(ghostId, out Entity aniEntity) ||
                        !selectedEntities.Add(aniEntity) ||
                        !SystemAPI.HasComponent<GhostOwner>(aniEntity))
                    {
                        continue;
                    }

                    // 服务器只接受连接自身拥有且具备移动数据的 Ani
                    // GhostOwner 是权限边界，AniAttributes 只提供能力快照
                    GhostOwner owner = SystemAPI.GetComponent<GhostOwner>(aniEntity);
                    if (owner.NetworkId != ownerNetworkId ||
                        !SystemAPI.HasComponent<LocalTransform>(aniEntity) ||
                        !SystemAPI.HasComponent<AniAttributes>(aniEntity))
                    {
                        continue;
                    }

                    AniAttributes attributes = SystemAPI.GetComponent<AniAttributes>(aniEntity);

                    // 订单快照冻结本次移动参数，后续 Squad 系统不再读取玩法组件
                    members.Add(new AniSquadOrderMember
                    {
                        Ani = aniEntity,
                        StableId = ghostId,
                        MaxSpeed = math.max(0f, attributes.MovementSpeed),
                        MaxAcceleration = math.max(1f, attributes.MovementSpeed * 4f),
                        AgentRadius = DefaultAgentRadius,
                    });
                }

                selectedEntities.Dispose();

                // 空选择可能来自过期 GhostId 或越权成员，同样视为已消费的无效 RPC
                if (members.IsEmpty)
                {
                    members.Dispose();
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 序号只分配给可执行订单，避免无效 RPC 制造回放序号空洞
                order.Sequence = NextSequence();
                order.DesiredForward = CalculateForward(ref state, members, order.TargetPosition);

                // 每个 RPC 只生成一个订单实体，成员数量不会放大路径上下文数量
                // 订单成员 Buffer 是权限校验后的最小输入，不再携带原 RPC
                Entity orderEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(orderEntity, new AniSquadOrderRequest());
                entityCommandBuffer.AddComponent(orderEntity, order);
                DynamicBuffer<AniSquadOrderMember> orderMembers =
                    entityCommandBuffer.AddBuffer<AniSquadOrderMember>(orderEntity);
                for (int index = 0; index < members.Length; index++)
                {
                    orderMembers.Add(members[index]);
                }

                members.Dispose();

                // 原始 RPC 不参与重试，订单实体是后续生命周期唯一输入
                // Destroy 延迟到 Playback，当前循环仍可安全读取 rpcEntity
                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            // 所有查询结束后一次回放，防止当前 Tick 读到半构建订单
            // 下一系统组只能观察完整的 Request、Order 和 Member Buffer
            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }

        private bool TryResolveOrder(
            ref SystemState state,
            AniCommandRpc rpc,
            int ownerNetworkId,
            out AniSquadOrder order)
        {
            order = default;
            AniSquadCommandMode mode;
            float stoppingDistance;

            // 高层目标类型在入口收敛为 Grid 后端仅需处理的三种命令语义
            // 未知枚举值直接失败，避免未来协议扩展被错误解释
            switch (rpc.TargetKind)
            {
                case WorldCommandTargetKind.Ground:
                    // 地面目标只依赖 RPC 坐标，不建立动态目标引用
                    mode = AniSquadCommandMode.MoveTo;
                    stoppingDistance = 0.7f;
                    break;
                case WorldCommandTargetKind.Player:
                    // 玩家目标持续跟随实体位置，停止距离比普通 MoveTo 更宽
                    mode = AniSquadCommandMode.Follow;
                    stoppingDistance = DefaultFollowStoppingDistance;
                    break;
                case WorldCommandTargetKind.Ani:
                case WorldCommandTargetKind.Resource:
                case WorldCommandTargetKind.Base:
                    // 非玩家实体使用 Find 语义，到达后由 Progress 结束一次性订单
                    mode = AniSquadCommandMode.Find;
                    stoppingDistance = DefaultFindStoppingDistance;
                    break;
                default:
                    return false;
            }

            float3 targetPosition = rpc.TargetWorldPosition;
            if (rpc.TargetKind != WorldCommandTargetKind.Ground)
            {
                // 动态命令必须先有可解析实体，后续 TargetResolve 才能持续追踪
                // 目标位置只作为本次初始投影，不能替代 TargetEntity 引用
                if (rpc.TargetEntity == Entity.Null ||
                    !state.EntityManager.Exists(rpc.TargetEntity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(rpc.TargetEntity))
                {
                    return false;
                }

                targetPosition = state.EntityManager.GetComponentData<LocalTransform>(
                    rpc.TargetEntity).Position;
            }

            // 非有限坐标会污染 Grid 投影和排序结果，必须在契约边界阻断
            if (!VectorMath.IsFinite(targetPosition))
            {
                return false;
            }

            // TargetEntity 保留给 Follow/Find，MoveTo 使用 Entity.Null 表示静态坐标
            order = new AniSquadOrder
            {
                OwnerNetworkId = ownerNetworkId,
                Mode = mode,
                Formation = AniSquadFormationKind.CompactRectangle,
                TargetPosition = targetPosition,
                TargetEntity = rpc.TargetEntity,
                FormationColumnCount = 4,
                TargetStoppingDistance = stoppingDistance,

                // 真正前向在成员集合验证完成后按队伍中心重新计算
                DesiredForward = new float3(0f, 0f, 1f),
            };

            // 所有默认值在这里收敛，后续系统只读取统一订单而不再判断 RPC 类型
            // DesiredForward 会在成员集合通过后由队伍中心重新计算
            return true;
        }

        private static float3 CalculateForward(
            ref SystemState state,
            NativeList<AniSquadOrderMember> members,
            float3 targetPosition)
        {
            float3 center = float3.zero;
            int count = 0;

            // 只聚合仍然存活的成员，避免同 Tick 销毁导致中心偏向世界原点
            for (int index = 0; index < members.Length; index++)
            {
                Entity aniEntity = members[index].Ani;
                if (!state.EntityManager.Exists(aniEntity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(aniEntity))
                {
                    continue;
                }

                center += state.EntityManager.GetComponentData<LocalTransform>(aniEntity).Position;
                count++;
            }

            if (count > 0)
            {
                center /= count;
            }

            float3 forward = targetPosition - center;

            // 阵型只在 XZ 平面定向，零距离时使用固定前向保证确定性
            return PlanarMath.NormalizeXZOrDefault(
                forward,
                new float3(0f, 0f, 1f));
        }

        private uint NextSequence()
        {
            uint sequence = _nextSequence++;

            // 零表示未提交订单，环绕时跳过该保留值
            if (_nextSequence == 0)
            {
                _nextSequence = 1;
            }

            // 递归只处理极少见的初始零值，不在正常序列中分配额外容器
            return sequence == 0 ? NextSequence() : sequence;
        }
    }
}
