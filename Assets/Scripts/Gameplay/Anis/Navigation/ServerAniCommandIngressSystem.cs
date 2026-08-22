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

            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, receive, rpcEntity) in
                     SystemAPI.Query<RefRO<AniCommandRpc>, RefRO<ReceiveRpcCommandRequest>>()
                              .WithEntityAccess())
            {
                Entity sourceConnection = receive.ValueRO.SourceConnection;
                if (!SystemAPI.HasComponent<NetworkId>(sourceConnection))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int ownerNetworkId = SystemAPI.GetComponent<NetworkId>(sourceConnection).Value;
                if (!selectionsByOwner.TryGetValue(ownerNetworkId, out Entity selectionEntity))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                ServerAniSelectionSet selection =
                    SystemAPI.GetComponent<ServerAniSelectionSet>(selectionEntity);
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
                var members = new NativeList<AniSquadCommandMember>(
                    math.max(1, selected.Length),
                    Allocator.Temp);
                var movementMembers = new NativeList<AniMovementOrderMember>(
                    math.max(1, selected.Length),
                    Allocator.Temp);
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
                    if (ghost.ghostId != selectedMember.GhostId ||
                        owner.NetworkId != ownerNetworkId)
                    {
                        selectionBecameInvalid = true;
                        break;
                    }

                    if (SystemAPI.HasComponent<AniCommandLockedTag>(ani) &&
                        SystemAPI.IsComponentEnabled<AniCommandLockedTag>(ani))
                    {
                        continue;
                    }

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

                if (selectionBecameInvalid || members.IsEmpty)
                {
                    members.Dispose();
                    movementMembers.Dispose();
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

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
                    return false;
            }

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

            if (!VectorMath.IsFinite(targetPosition))
            {
                return false;
            }

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

            return PlanarMath.NormalizeXZOrDefault(
                targetPosition - center,
                new float3(0f, 0f, 1f));
        }

        private uint NextSequence()
        {
            uint sequence = _nextSequence++;
            if (_nextSequence == 0)
            {
                _nextSequence = 1;
            }

            return sequence == 0 ? NextSequence() : sequence;
        }
    }
}
