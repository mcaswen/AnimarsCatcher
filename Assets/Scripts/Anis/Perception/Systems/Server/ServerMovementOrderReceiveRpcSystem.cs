using System.Diagnostics;
using System.Threading.Tasks;
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

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
    }

    // [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 建立 NetworkId -> 玩家主角(leader) 的映射
        var leadersByNetworkId =
            new NativeParallelHashMap<int, Entity>(16, Allocator.Temp);

        foreach (var (owner, leaderEntity) in
                 SystemAPI.Query<RefRO<GhostOwner>>()
                          .WithAll<CharacterTag>()
                          .WithEntityAccess())
        {
            leadersByNetworkId.TryAdd(owner.ValueRO.NetworkId, leaderEntity);
        }

        foreach (var (rpc, recv, rpcEntity) in
                 SystemAPI.Query<RefRO<MovementOrderRpc>, RefRO<ReceiveRpcCommandRequest>>()
                          .WithEntityAccess())
        {
            Entity connection = recv.ValueRO.SourceConnection;

            // 找到这条连接的 NetworkId
            if (!SystemAPI.HasComponent<NetworkId>(connection))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            int networkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

            // 验证 TargetEntity 是否存在（非 Ground 情况）
            Entity targetEntity = rpc.ValueRO.TargetEntity;
            MovementTargetKind targetKind = rpc.ValueRO.TargetKind;
            float3 clickPos = rpc.ValueRO.TargetWorldPosition;

            // 如果是 Ani / Resource / Player，但 TargetEntity 映射失败，直接忽略本条命令
            if ((targetKind == MovementTargetKind.Ani ||
                 targetKind == MovementTargetKind.Resource ||
                 targetKind == MovementTargetKind.Player) &&
                (targetEntity == Entity.Null || !state.EntityManager.Exists(targetEntity)))
            {
                entityCommandBuffer.DestroyEntity(rpcEntity);
                continue;
            }

            // 这次命令对应的“玩家主角”实体
            Entity leaderEntity = Entity.Null;
            float3 leaderPos = float3.zero;
            quaternion leaderRot = quaternion.identity;

            if (leadersByNetworkId.TryGetValue(networkId, out leaderEntity) &&
                SystemAPI.HasComponent<LocalTransform>(leaderEntity))
            {
                var lt = SystemAPI.GetComponent<LocalTransform>(leaderEntity);
                leaderPos = lt.Position;
                leaderRot = lt.Rotation;
            }

            // 遍历所有归属于该 NetworkId 且当前被选中的 Ani
            foreach (var (attributes, owner, aniEntity) in
                     SystemAPI.Query<RefRO<AniAttributes>, RefRO<GhostOwner>>()
                              .WithAll<AniSelectedTag>()
                              .WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId != networkId)
                    continue;

                if (!_blackboardLookup.HasBuffer(aniEntity))
                    continue;

                DynamicBuffer<FsmVar> blackboard = _blackboardLookup[aniEntity];

                switch (targetKind)
                {
                    case MovementTargetKind.Ground:
                    {
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.K_CommandMode,
                            (int)AniMovementCommandMode.MoveTo);

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.K_MoveToPosition,
                            clickPos);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.K_TargetEntity,
                            Entity.Null);

                         float3 forward;

                        if (leaderEntity != Entity.Null)
                        {
                            float3 dir = clickPos - leaderPos;
                            dir.y = 0f;

                            if (math.lengthsq(dir) < 0.0001f)
                            {
                                // 如果玩家和点击点几乎重合，就用玩家自己的 forward
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
                            // 没找到 leader，就兜底一个世界 Z 方向
                            forward = new float3(0, 0, 1);
                        }

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.K_MoveFormationTargetPoint,
                            clickPos);

                        Blackboard.SetFloat3(ref blackboard,
                            AniMovementBlackboardKeys.K_MoveFormationForward,
                            forward);
                        
                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                        
                        UnityEngine.Debug.Log($"[ServerMovementOrderReceiveRpcSystem] Received MovementOrderRpc:" +
                        $" TargetKind = Ground, TargetWorldPosition={clickPos}, TargetEntity={targetEntity} for Ani Entity={aniEntity.Index}");
                        
                        break;
                    }

                    case MovementTargetKind.Ani:
                    {
                        var o = SystemAPI.GetComponent<GhostOwner>(targetEntity);
                        if (o.NetworkId == networkId)
                            break;

                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.K_CommandMode,
                            (int)AniMovementCommandMode.Find);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.K_TargetEntity,
                            targetEntity);
                        
                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                        
                        UnityEngine.Debug.Log($"[ServerMovementOrderReceiveRpcSystem] Received MovementOrderRpc:" +
                        $" TargetKind = Ani, TargetWorldPosition={clickPos}, TargetEntity={targetEntity} for Ani Entity={aniEntity.Index}");
                        
                        break;
                    }

                    case MovementTargetKind.Resource:
                    {
                        // 同理，Find 模式，靠近资源后由 PickTask 系统接管
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.K_CommandMode,
                            (int)AniMovementCommandMode.Find);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.K_TargetEntity,
                            targetEntity);

                        if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);

                        UnityEngine.Debug.Log($"[ServerMovementOrderReceiveRpcSystem] Received MovementOrderRpc:" +
                        $" TargetKind = Resource, TargetWorldPosition={clickPos}, TargetEntity={targetEntity} for Ani Entity={aniEntity.Index}");
                        
                        break;
                    }

                    case MovementTargetKind.Player:
                    {
                        // 点击 Player：让自己 Follow 该玩家主角（TargetEntity）
                        Blackboard.SetInt(ref blackboard,
                            AniMovementBlackboardKeys.K_CommandMode,
                            (int)AniMovementCommandMode.Follow);

                        Blackboard.SetEntity(ref blackboard,
                            AniMovementBlackboardKeys.K_PlayerEntity,
                            targetEntity);
                        
                        if (!SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                            entityCommandBuffer.AddComponent<AniInTeamTag>(aniEntity);
                        
                        UnityEngine.Debug.Log($"[ServerMovementOrderReceiveRpcSystem] Received MovementOrderRpc:" +
                        $" TargetKind = Player, TargetWorldPosition={clickPos}, TargetEntity={targetEntity} for Ani Entity={aniEntity.Index}");
                        
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
    }
}
