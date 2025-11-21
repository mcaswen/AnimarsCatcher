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

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);

        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (rpc, recv, rpcEntity) in
                 SystemAPI.Query<RefRO<MovementOrderRpc>, RefRO<ReceiveRpcCommandRequest>>()
                          .WithEntityAccess())
        {
            Entity connection = recv.ValueRO.SourceConnection;

            // 找到这条连接的 NetworkId
            if (!SystemAPI.HasComponent<NetworkId>(connection))
            {
                ecb.DestroyEntity(rpcEntity);
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
                ecb.DestroyEntity(rpcEntity);
                continue;
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

                UnityEngine.Debug.Log($"[ServerMovementOrderReceiveRpcSystem] Received MovementOrderRpc: TargetKind= {targetKind}, TargetWorldPosition={clickPos}, TargetEntity={targetEntity} for Ani Entity={aniEntity.Index}");

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
                        break;
                    }

                    case MovementTargetKind.None:
                    default:
                        break;
                }
            }

            ecb.DestroyEntity(rpcEntity);
        }

        ecb.Playback(state.EntityManager);
    }
}
