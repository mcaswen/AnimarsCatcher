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
    /// 在 Grid 后端验证选择集版本和目标，并生成 MovementOrder 与兼容 Squad 指令
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    public partial struct ServerAniCommandIngressSystem : ISystem
    {
        // 兼容 Squad 尚无独立碰撞与停止距离配置，6A.2 接管后应移除这些默认值
        private const float DefaultAgentRadius = 0.35f;
        private const float DefaultFollowStoppingDistance = 2.5f;
        private const float DefaultFindStoppingDistance = 1.0f;

        private uint _nextSequence;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _nextSequence = 1;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // 每轮建立玩家到权威选择集的临时映射，避免逐 RPC 扫描全部玩家
            var selectionsByOwner = new NativeParallelHashMap<int, Entity>(
                math.max(1, SystemAPI.QueryBuilder().WithAll<ServerAniSelectionSet>().Build()
                    .CalculateEntityCount()),
                Allocator.Temp);
            foreach (var (selection, selectionEntity) in
                     SystemAPI.Query<RefRO<ServerAniSelectionSet>>()
                              .WithEntityAccess())
            {
                selectionsByOwner.TryAdd(
                    selection.ValueRO.OwnerNetworkId,
                    selectionEntity);
            }

            // 延迟创建命令和销毁 RPC，避免在查询期间执行结构变更
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, receive, rpcEntity) in
                     SystemAPI.Query<RefRO<AniCommandRpc>, RefRO<ReceiveRpcCommandRequest>>()
                              .WithEntityAccess())
            {
                // 来源连接是确定玩家身份和选择集所有者的唯一可信入口
                Entity sourceConnection = receive.ValueRO.SourceConnection;
                if (!SystemAPI.HasComponent<NetworkId>(sourceConnection))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int ownerNetworkId = SystemAPI.GetComponent<NetworkId>(sourceConnection).Value;
                // 玩家尚未发布选择集时不能从客户端提供的版本推断成员
                if (!selectionsByOwner.TryGetValue(ownerNetworkId, out Entity selectionEntity))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                ServerAniSelectionSet selection =
                    SystemAPI.GetComponent<ServerAniSelectionSet>(selectionEntity);
                // 版本、Hash、非空成员和目标语义必须在复制成员前全部通过
                if (selection.Version != rpc.ValueRO.SelectionVersion ||
                    selection.CompletenessHash != rpc.ValueRO.SelectionHash ||
                    selection.MemberCount <= 0 ||
                    !TryResolveCommand(
                        ref state,
                        rpc.ValueRO,
                        ownerNetworkId,
                        out AniSquadCommand command))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                DynamicBuffer<ServerAniSelectionMember> selected =
                    state.EntityManager.GetBuffer<ServerAniSelectionMember>(
                        selectionEntity,
                        true);
                // 过渡期同时冻结新订单和旧 Squad 所需的成员数据
                var members = new NativeList<AniSquadCommandMember>(
                    math.max(1, selected.Length),
                    Allocator.Temp);
                var movementMembers = new NativeList<AniMovementOrderMember>(
                    math.max(1, selected.Length),
                    Allocator.Temp);
                // 任意成员已失效时拒绝整个命令，不能悄悄执行部分选择
                bool selectionBecameInvalid = false;

                for (int index = 0; index < selected.Length; index++)
                {
                    ServerAniSelectionMember selectedMember = selected[index];
                    Entity ani = selectedMember.Ani;
                    if (!state.EntityManager.Exists(ani) ||
                        !SystemAPI.HasComponent<GhostInstance>(ani) ||
                        !SystemAPI.HasComponent<GhostOwner>(ani) ||
                        !SystemAPI.HasComponent<LocalTransform>(ani) ||
                        !SystemAPI.HasComponent<AniAttributes>(ani))
                    {
                        selectionBecameInvalid = true;
                        break;
                    }

                    GhostInstance ghost = SystemAPI.GetComponent<GhostInstance>(ani);
                    GhostOwner owner = SystemAPI.GetComponent<GhostOwner>(ani);
                    // 同时核对 GhostId 和所有权，防止 Entity 槽位复用
                    if (ghost.ghostId != selectedMember.GhostId ||
                        owner.NetworkId != ownerNetworkId)
                    {
                        selectionBecameInvalid = true;
                        break;
                    }

                    if (SystemAPI.HasComponent<AniCommandLockedTag>(ani) &&
                        SystemAPI.IsComponentEnabled<AniCommandLockedTag>(ani))
                    {
                        // 被玩法锁定的 Ani 合法存在，但不参与这次移动命令
                        continue;
                    }

                    // 兼容成员冻结旧 Squad 链路需要的速度、加速度和角色
                    AniAttributes attributes = SystemAPI.GetComponent<AniAttributes>(ani);
                    members.Add(new AniSquadCommandMember
                    {
                        Ani = ani,
                        StableId = selectedMember.GhostId,
                        MaxSpeed = math.max(0f, attributes.MovementSpeed),
                        MaxAcceleration = math.max(1f, attributes.MovementSpeed * 4f),
                        AgentRadius = DefaultAgentRadius,
                        Role = SystemAPI.HasComponent<PickerAniTag>(ani)
                            ? AniSquadRole.Picker
                            : SystemAPI.HasComponent<BlasterAniTag>(ani)
                                ? AniSquadRole.Blaster
                                : AniSquadRole.Any,
                    });
                    movementMembers.Add(new AniMovementOrderMember
                    {
                        GhostId = selectedMember.GhostId,
                        Ani = ani,
                    });
                }

                // 选择失效或全部成员被锁定时都不生成空订单
                if (selectionBecameInvalid || members.IsEmpty)
                {
                    members.Dispose();
                    movementMembers.Dispose();
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 序号在成员校验完成后分配，失败 RPC 不消耗命令序号
                command.Sequence = NextSequence();
                command.DesiredForward = CalculateForward(
                    ref state,
                    members,
                    command.TargetPosition);

                Entity commandEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(commandEntity, new AniMovementOrderRequest());
                entityCommandBuffer.AddComponent(commandEntity, new AniMovementOrder
                {
                    Sequence = command.Sequence,
                    OwnerNetworkId = ownerNetworkId,
                    SelectionVersion = selection.Version,
                    SelectionHash = selection.CompletenessHash,
                    Mode = command.Mode,
                    TargetPosition = command.TargetPosition,
                    TargetEntity = command.TargetEntity,
                    TargetStoppingDistance = command.TargetStoppingDistance,
                });
                // MovementOrder Buffer 冻结命令创建时的成员，不再引用可变选择集
                DynamicBuffer<AniMovementOrderMember> orderMembers =
                    entityCommandBuffer.AddBuffer<AniMovementOrderMember>(commandEntity);
                for (int index = 0; index < movementMembers.Length; index++)
                {
                    orderMembers.Add(movementMembers[index]);
                }

                // 6A.2 接管运行时前继续写入旧 Squad 契约，保证现有 Grid 链路可回归
                entityCommandBuffer.AddComponent(commandEntity, new AniSquadCommandRequest());
                entityCommandBuffer.AddComponent(commandEntity, command);
                DynamicBuffer<AniSquadCommandMember> commandMembers =
                    entityCommandBuffer.AddBuffer<AniSquadCommandMember>(commandEntity);
                for (int index = 0; index < members.Length; index++)
                {
                    commandMembers.Add(members[index]);
                }

                // ECB 已复制列表内容，临时容器可以在 Playback 前释放
                members.Dispose();
                movementMembers.Dispose();
                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
            selectionsByOwner.Dispose();
        }

        private bool TryResolveCommand(
            ref SystemState state,
            AniCommandRpc rpc,
            int ownerNetworkId,
            out AniSquadCommand command)
        {
            command = default;
            AniSquadCommandMode mode;
            float stoppingDistance;
            switch (rpc.TargetKind)
            {
                case WorldCommandTargetKind.Ground:
                    mode = AniSquadCommandMode.MoveTo;
                    stoppingDistance = 0.7f;
                    break;
                case WorldCommandTargetKind.Player:
                    mode = AniSquadCommandMode.Follow;
                    stoppingDistance = DefaultFollowStoppingDistance;
                    break;
                case WorldCommandTargetKind.Ani:
                case WorldCommandTargetKind.Resource:
                case WorldCommandTargetKind.Base:
                    mode = AniSquadCommandMode.Find;
                    stoppingDistance = DefaultFindStoppingDistance;
                    break;
                default:
                    // 未登记的枚举值来自不可信网络输入，必须拒绝
                    return false;
            }

            // 地面命令可以直接采用 RPC 坐标，其他目标必须从服务器重新解析
            float3 targetPosition = rpc.TargetWorldPosition;
            if (rpc.TargetKind != WorldCommandTargetKind.Ground)
            {
                if (rpc.TargetEntity == Entity.Null ||
                    !state.EntityManager.Exists(rpc.TargetEntity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(rpc.TargetEntity))
                {
                    return false;
                }

                targetPosition = state.EntityManager.GetComponentData<LocalTransform>(
                    rpc.TargetEntity).Position;
            }

            // 拒绝 NaN 和无穷值，避免污染后续 Grid 数学运算
            if (!VectorMath.IsFinite(targetPosition))
            {
                return false;
            }

            // 严格阵型字段只服务于 6A.2 前的兼容 Squad 链路
            command = new AniSquadCommand
            {
                OwnerNetworkId = ownerNetworkId,
                Mode = mode,
                Formation = AniSquadFormationKind.CompactRectangle,
                TargetPosition = targetPosition,
                TargetEntity = rpc.TargetKind == WorldCommandTargetKind.Ground
                    ? Entity.Null
                    : rpc.TargetEntity,
                FormationColumnCount = 4,
                TargetStoppingDistance = stoppingDistance,
                DesiredForward = new float3(0f, 0f, 1f),
            };
            return true;
        }

        private static float3 CalculateForward(
            ref SystemState state,
            NativeList<AniSquadCommandMember> members,
            float3 targetPosition)
        {
            float3 center = float3.zero;
            int count = 0;
            for (int index = 0; index < members.Length; index++)
            {
                Entity ani = members[index].Ani;
                // 理论上成员已在入口校验，这里保留安全边界便于独立复用
                if (!state.EntityManager.Exists(ani) ||
                    !state.EntityManager.HasComponent<LocalTransform>(ani))
                {
                    continue;
                }

                center += state.EntityManager.GetComponentData<LocalTransform>(ani).Position;
                count++;
            }

            if (count > 0)
            {
                center /= count;
            }

            // 只计算 XZ 朝向，目标与中心重合时使用世界前方
            return PlanarMath.NormalizeXZOrDefault(
                targetPosition - center,
                new float3(0f, 0f, 1f));
        }

        private uint NextSequence()
        {
            uint sequence = _nextSequence++;
            if (_nextSequence == 0)
            {
                // 溢出后的零值立即跳过
                _nextSequence = 1;
            }

            // 防御异常反序列化或状态恢复把当前值设为零
            return sequence == 0 ? NextSequence() : sequence;
        }
    }
}
