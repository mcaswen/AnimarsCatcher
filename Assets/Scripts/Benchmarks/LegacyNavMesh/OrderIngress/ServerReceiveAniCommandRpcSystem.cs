using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Player;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace AnimarsCatcher.Benchmarks.LegacyNavigation
{
    /// <summary>
    /// 在服务器验证移动 RPC 的连接拥有权并写入 Ani 行为黑板
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServerReceiveAniCommandRpcSystem : ISystem
    {
        private BufferLookup<FsmVar> _blackboardLookup;

        // 每帧重建 GhostId 到服务器权威 Ani 实体的映射
        private NativeParallelHashMap<int, Entity> _aniByGhostId;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LegacyNavMeshBackendEnabled>();
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

            // GhostId 会随网络实体生命周期变化，因此每帧从权威世界重建映射
            _aniByGhostId.Clear();

            foreach (var (ghostInstance, aniAttributes, entity) in
                     SystemAPI.Query<RefRO<GhostInstance>, RefRO<AniAttributes>>()
                              .WithEntityAccess())
            {
                _aniByGhostId.TryAdd(ghostInstance.ValueRO.ghostId, entity);
            }

            // 查询期间延迟结构变更，避免队伍标签和 RPC 销毁使迭代失效
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            // 玩家主角映射用于校验连接归属并计算整队移动朝向
            var leadersByNetworkId =
                new NativeParallelHashMap<int, Entity>(16, Allocator.Temp);

            foreach (var (owner, leaderEntity) in
                     SystemAPI.Query<RefRO<GhostOwner>>()
                              .WithAll<CharacterTag>()
                              .WithEntityAccess())
            {
                leadersByNetworkId.TryAdd(owner.ValueRO.NetworkId, leaderEntity);
            }

            // 每条 RPC 都以 SourceConnection 的 NetworkId 作为权限依据
            foreach (var (rpc, recv, rpcEntity) in
                     SystemAPI.Query<RefRO<AniCommandRpc>, RefRO<ReceiveRpcCommandRequest>>()
                              .WithEntityAccess())
            {
                Entity connection = recv.ValueRO.SourceConnection;

                if (!SystemAPI.HasComponent<NetworkId>(connection))
                {
                    // 来源连接已经失效的 RPC 也必须消费，不能留到下一帧重试
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                int networkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

                WorldCommandTargetKind targetKind = rpc.ValueRO.TargetKind;
                Entity             targetEntity = rpc.ValueRO.TargetEntity;
                float3             targetWorldPosition = rpc.ValueRO.TargetWorldPosition;

                // 需要实体目标的命令在目标失效时整体拒绝，避免写入悬空引用
                if ((targetKind == WorldCommandTargetKind.Ani ||
                     targetKind == WorldCommandTargetKind.Resource ||
                     targetKind == WorldCommandTargetKind.Player) &&
                    (targetEntity == Entity.Null || !state.EntityManager.Exists(targetEntity)))
                {
                    entityCommandBuffer.DestroyEntity(rpcEntity);
                    continue;
                }

                // 主角位置和朝向为地面移动提供稳定的阵型前方向
                Entity    leaderEntity = Entity.Null;
                float3    leaderPosition = float3.zero;
                quaternion leaderRotation = quaternion.identity;

                if (leadersByNetworkId.TryGetValue(networkId, out leaderEntity) &&
                    SystemAPI.HasComponent<LocalTransform>(leaderEntity))
                {
                    var lt = SystemAPI.GetComponent<LocalTransform>(leaderEntity);
                    leaderPosition = lt.Position;
                    leaderRotation = lt.Rotation;
                }

                if (targetKind == WorldCommandTargetKind.Resource)
                {
                    // 资源请求只允许绑定有效主角和尚未被分配的可拾取资源
                    if (leaderEntity != Entity.Null &&
                        SystemAPI.HasComponent<PickableResourceTag>(targetEntity) &&
                        !SystemAPI.HasComponent<ResourcePickupRequest>(targetEntity) &&
                        !SystemAPI.HasComponent<ResourceCarryAssignment>(targetEntity))
                    {
                        entityCommandBuffer.AddComponent(targetEntity, new ResourcePickupRequest
                        {
                            PlayerRobotEntity = leaderEntity,
                        });
                    }
                }

                // 逐个解析客户端快照中的 GhostId，并在服务器重新验证拥有权
                var selectedAniGhostIds = rpc.ValueRO.SelectedAniGhostIds;

                for (int i = 0; i < selectedAniGhostIds.Length; i++)
                {
                    int aniGhostId = selectedAniGhostIds[i];

                    // 选择快照允许部分 Ghost 已销毁，其余有效 Ani 仍继续执行命令
                    if (!_aniByGhostId.TryGetValue(aniGhostId, out Entity aniEntity))
                        continue;

                    // SourceConnection 只能控制 GhostOwner 与自身 NetworkId 一致的 Ani
                    if (!SystemAPI.HasComponent<GhostOwner>(aniEntity))
                        continue;

                    var aniOwner = SystemAPI.GetComponent<GhostOwner>(aniEntity);
                    if (aniOwner.NetworkId != networkId)
                        continue;

                    // 尚未完成 FSM 初始化的 Ani 暂不接受行为命令
                    if (!_blackboardLookup.HasBuffer(aniEntity))
                        continue;

                    DynamicBuffer<FsmVar> blackboard = _blackboardLookup[aniEntity];

                    switch (targetKind)
                    {
                        case WorldCommandTargetKind.Ground:
                        {
                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.MoveTo);

                            Blackboard.SetFloat3(ref blackboard,
                                AniMovementBlackboardKeys.MoveToPosition,
                                targetWorldPosition);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.TargetEntity,
                                Entity.Null);

                            // 使用主角到点击点的方向作为整队统一前向
                            float3 forward;

                            if (leaderEntity != Entity.Null)
                            {
                                float3 dir = targetWorldPosition - leaderPosition;
                                dir.y = 0f;

                                if (math.lengthsq(dir) < 0.0001f)
                                {
                                    float3 fallbackForward = math.mul(leaderRotation, new float3(0, 0, 1));
                                    fallbackForward.y = 0f;
                                    if (math.lengthsq(fallbackForward) < 0.0001f)
                                        fallbackForward = new float3(0, 0, 1);

                                    forward = math.normalize(fallbackForward);
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
                                targetWorldPosition);

                            Blackboard.SetFloat3(ref blackboard,
                                AniMovementBlackboardKeys.MoveFormationForward,
                                forward);

                            // 地面点命令交由独立编队移动处理，不再保持跟随队伍状态
                            if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                                entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                            break;
                        }

                        case WorldCommandTargetKind.Ani:
                        {
                            var targetOwner = SystemAPI.GetComponent<GhostOwner>(targetEntity);
                            // 同一玩家的 Ani 不互相追逐，避免把编队成员当作交互目标
                            if (targetOwner.NetworkId == networkId)
                                break;

                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.Find);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.TargetEntity,
                                targetEntity);

                            if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                                entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                            break;
                        }

                        case WorldCommandTargetKind.Resource:
                        {
                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.Find);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.TargetEntity,
                                targetEntity);

                            if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                                entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                            break;
                        }

                        case WorldCommandTargetKind.Base:
                        {
                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.Find);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.TargetEntity,
                                targetEntity);

                            if (SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                                entityCommandBuffer.RemoveComponent<AniInTeamTag>(aniEntity);
                            break;
                        }

                        case WorldCommandTargetKind.Player:
                        {
                            Blackboard.SetInt(ref blackboard,
                                AniMovementBlackboardKeys.CommandMode,
                                (int)AniMovementCommandMode.Follow);

                            Blackboard.SetEntity(ref blackboard,
                                AniMovementBlackboardKeys.PlayerEntity,
                                targetEntity);

                            // 跟随玩家命令恢复队伍成员身份，由跟随 FSM 接管移动
                            if (!SystemAPI.HasComponent<AniInTeamTag>(aniEntity))
                                entityCommandBuffer.AddComponent<AniInTeamTag>(aniEntity);
                            break;
                        }

                        case WorldCommandTargetKind.None:
                        default:
                            break;
                    }
                }

                // 一个 RPC 只处理一次，即使其中所有 Ani 都因校验失败被跳过
                entityCommandBuffer.DestroyEntity(rpcEntity);
            }

            entityCommandBuffer.Playback(state.EntityManager);
            leadersByNetworkId.Dispose();
        }
    }
}
