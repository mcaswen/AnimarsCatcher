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
    /// 在 Grid 后端验证选择集版本和目标，并生成可拆分为 Cohort 的 MovementOrder
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(AniGridCommandIngressSystemGroup))]
    public partial struct ServerAniCommandIngressSystem : ISystem
    {
        private const float DefaultAgentRadius = 0.35f;
        private const float DefaultFollowStoppingDistance = 2.5f;
        private const float DefaultFindStoppingDistance = 1.0f;

        private uint _nextSequence;
        private uint _serverTick;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridMovementBackendEnabled>();
            _nextSequence = 1;
            _serverTick = 0;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _serverTick = NextNonZero(_serverTick);
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
                        out AniMovementOrder command))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                DynamicBuffer<ServerAniSelectionMember> selected =
                    state.EntityManager.GetBuffer<ServerAniSelectionMember>(
                        selectionEntity,
                        true);
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

                    AniAttributes attributes = SystemAPI.GetComponent<AniAttributes>(ani);
                    float maxSpeed = math.max(0f, attributes.MovementSpeed);
                    movementMembers.Add(new AniMovementOrderMember
                    {
                        GhostId = selectedMember.GhostId,
                        Ani = ani,
                        MaxSpeed = maxSpeed,
                        MaxAcceleration = math.max(1f, maxSpeed * 4f),
                        AgentRadius = DefaultAgentRadius,
                        AgentProfile = CalculateAgentProfile(DefaultAgentRadius),
                    });
                }

                // 选择失效或全部成员被锁定时都不生成空订单
                if (selectionBecameInvalid || movementMembers.IsEmpty)
                {
                    movementMembers.Dispose();
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 序号在成员校验完成后分配，失败 RPC 不消耗命令序号
                command.Sequence = NextSequence();
                command.CreatedTick = _serverTick;
                command.CancellationVersion = command.Sequence;

                Entity commandEntity = entityCommandBuffer.CreateEntity();
                entityCommandBuffer.AddComponent(commandEntity, new AniMovementOrderRequest());
                command.SelectionVersion = selection.Version;
                command.SelectionHash = selection.CompletenessHash;
                entityCommandBuffer.AddComponent(commandEntity, command);
                // MovementOrder Buffer 冻结命令创建时的成员，不再引用可变选择集
                DynamicBuffer<AniMovementOrderMember> orderMembers =
                    entityCommandBuffer.AddBuffer<AniMovementOrderMember>(commandEntity);
                for (int index = 0; index < movementMembers.Length; index++)
                {
                    orderMembers.Add(movementMembers[index]);
                }

                // ECB 已复制列表内容，临时容器可以在 Playback 前释放
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
            out AniMovementOrder command)
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

            command = new AniMovementOrder
            {
                OwnerNetworkId = ownerNetworkId,
                Mode = mode,
                TargetPosition = targetPosition,
                TargetEntity = rpc.TargetKind == WorldCommandTargetKind.Ground
                    ? Entity.Null
                    : rpc.TargetEntity,
                TargetStoppingDistance = stoppingDistance,
                GoalCellCapacityScale = 1f,
                GoalInfluenceRadius = 4f,
            };
            return true;
        }

        private static uint CalculateAgentProfile(float agentRadius)
        {
            uint profile = math.hash(new uint2(math.asuint(agentRadius), 1u));
            return profile == 0 ? 1u : profile;
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

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1u : value;
        }
    }
}
