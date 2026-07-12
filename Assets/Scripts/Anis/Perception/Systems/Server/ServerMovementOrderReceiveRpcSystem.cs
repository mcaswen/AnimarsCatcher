using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerMovementOrderReceiveRpcSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookup;

    // GhostId -> Ani Entity
    private NativeParallelHashMap<int, Entity> _aniByGhostId;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        _aniByGhostId     = new NativeParallelHashMap<int, Entity>(128, Allocator.Persistent);
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_aniByGhostId.IsCreated)
            _aniByGhostId.Dispose();
    }

    [BurstCompile] 
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);

        // -------- 重建 GhostId -> Ani Entity 映射 --------
        _aniByGhostId.Clear();

        foreach (var (ghostInstance, aniAttributes, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>, RefRO<AniAttributes>>()
                          .WithEntityAccess())
        {
            _aniByGhostId.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // NetworkId -> 玩家主角(leader) 的映射（用于阵型朝向）
        var leadersByNetworkId =
            new NativeParallelHashMap<int, Entity>(16, Allocator.Temp);

        foreach (var (owner, leaderEntity) in
                 SystemAPI.Query<RefRO<GhostOwner>>()
                          .WithAll<CharacterTag>()
                          .WithEntityAccess())
        {
            leadersByNetworkId.TryAdd(owner.ValueRO.NetworkId, leaderEntity);
        }

        // -------- 消费所有 MovementOrderRpc --------
        foreach (var (rpc, recv, rpcEntity) in
                 SystemAPI.Query<RefRO<MovementOrderRpc>, RefRO<ReceiveRpcCommandRequest>>()
                          .WithEntityAccess())
        {
            Entity connection = recv.ValueRO.SourceConnection;

            if (!SystemAPI.HasComponent<NetworkId>(connection))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            int networkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

            MovementTargetKind targetKind = rpc.ValueRO.TargetKind;
            Entity             targetEntity = rpc.ValueRO.TargetEntity;
            float3             clickPos     = rpc.ValueRO.TargetWorldPosition;

            // 如果是 Ani / Resource / Player，但 TargetEntity 映射失败，直接忽略本条命令
            if ((targetKind == MovementTargetKind.Ani ||
                 targetKind == MovementTargetKind.Resource ||
                 targetKind == MovementTargetKind.Player) &&
                (targetEntity == Entity.Null || !state.EntityManager.Exists(targetEntity)))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 找这条命令对应的玩家主角（用于阵型朝向）
            Entity    leaderEntity = Entity.Null;
            float3    leaderPos    = float3.zero;
            quaternion leaderRot   = quaternion.identity;

            if (leadersByNetworkId.TryGetValue(networkId, out leaderEntity) &&
                SystemAPI.HasComponent<LocalTransform>(leaderEntity))
            {
                var lt = SystemAPI.GetComponent<LocalTransform>(leaderEntity);
                leaderPos = lt.Position;
                leaderRot = lt.Rotation;
            }

            if (targetKind == MovementTargetKind.Resource)
            {
                // 需要有主角（作为 PlayerRobotEntity），目标必须真的是 PickableResource
                if (leaderEntity != Entity.Null &&
                    SystemAPI.HasComponent<PickableResourceTag>(targetEntity) &&
                    !SystemAPI.HasComponent<ResourcePickupRequest>(targetEntity) &&
                    !SystemAPI.HasComponent<ResourceCarryAssignment>(targetEntity))
                {
                    entityCommandBuffer.AddComponent(targetEntity, new ResourcePickupRequest
                    {
                        PlayerRobotEntity = leaderEntity,
                    });

                    // UnityEngine.Debug.Log(
                    //     $"[ServerMovementOrderReceiveRpcSystem] Added ResourcePickupRequest for resource={targetEntity.Index}, " +
                    //     $"leader={leaderEntity.Index}");
                }
            }

            // -------- 关键：遍历这条命令里的 Ani 列表 --------
            var selectedAniGhostIds = rpc.ValueRO.SelectedAniGhostIds;

            for (int i = 0; i < selectedAniGhostIds.Length; i++)
            {
                int aniGhostId = selectedAniGhostIds[i];

                if (!_aniByGhostId.TryGetValue(aniGhostId, out Entity aniEntity))
                    continue;

                // 防止恶意命令：只允许控制自己 NetworkId 的 Ani
                if (!SystemAPI.HasComponent<GhostOwner>(aniEntity))
                    continue;

                var aniOwner = SystemAPI.GetComponent<GhostOwner>(aniEntity);
                if (aniOwner.NetworkId != networkId)
                    continue;

                if (!_blackboardLookup.HasBuffer(aniEntity))
                    continue;

                DynamicBuffer<FsmVar> blackboard = _blackboardLookup[aniEntity];

                switch (targetKind)
                {
                    case MovementTargetKind.Ground:
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.MoveTo);

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.MoveToPosition,
                            clickPos);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.TargetEntity,
                            Entity.Null);

                        // 阵型朝向
                        float3 forward;

                        if (leaderEntity != Entity.Null)
                        {
                            float3 dir = clickPos - leaderPos;
                            dir.y = 0f;

                            if (math.lengthsq(dir) < 0.0001f)
                            {
                                float3 f = math.mul(leaderRot, new float3(0, 0, 1));
                                f.y = 0f;
                                if (math.lengthsq(f) < 0.0001f)
                                    f = new float3(0, 0, 1);

                                forward = math.normalize(f);
                            }
                            else
                            {
                                forward = math.normalize(dir);
                            }
                        }
                        else
                        {
                            forward = new float3(0, 0, 1);
                        }

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.MoveFormationTargetPoint,
                            clickPos);

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.MoveFormationForward,
                            forward);

                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);

                        // UnityEngine.Debug.Log(
                        //     $"[ServerMovementOrderReceiveRpcSystem] Ground command -> Ani {aniEntity.Index}, " +
                        //     $"click={clickPos}, target={targetEntity}");

                        break;
                    }

                    case MovementTargetKind.Ani:
                    {
                        var o = SystemAPI.GetComponent<GhostOwner>(targetEntity);
                        if (o.NetworkId == networkId)
                            break;

                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.Find);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.TargetEntity,
                            targetEntity);

                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);

                        // UnityEngine.Debug.Log(
                        //     $"[ServerMovementOrderReceiveRpcSystem] Ani command -> Ani {aniEntity.Index}, " +
                        //     $"target Ani={targetEntity.Index}");

                        break;
                    }

                    case MovementTargetKind.Resource:
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.Find);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.TargetEntity,
                            targetEntity);

                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);

                        // UnityEngine.Debug.Log(
                        //     $"[ServerMovementOrderReceiveRpcSystem] Resource command -> Ani {aniEntity.Index}, " +
                        //     $"target Resource={targetEntity.Index}");

                        break;
                    }

                    case MovementTargetKind.Base:
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.Find);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.TargetEntity,
                            targetEntity);

                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);

                        // UnityEngine.Debug.Log(
                        //     $"[ServerMovementOrderReceiveRpcSystem] Resource command -> Ani {aniEntity.Index}, " +
                        //     $"target Resource={targetEntity.Index}");

                        break;
                    }

                    case MovementTargetKind.Player:
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.CommandMode,
                            (int)AniMovementCommandMode.Follow);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.PlayerEntity,
                            targetEntity);

                        if (!SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.AddComponent<AniInTeamTag>(aniEntity);

                        // UnityEngine.Debug.Log(
                        //     $"[ServerMovementOrderReceiveRpcSystem] Follow command -> Ani {aniEntity.Index}, " +
                        //     $"player={targetEntity.Index}");

                        break;
                    }

                    case MovementTargetKind.None:
                    default:
                        break;
                }
            }

            entityCommandBuffer.DestroyEntity(rpcEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        leadersByNetworkId.Dispose();
    }
}
