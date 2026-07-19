using AnimarsCatcher.Core.Fsm;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Player;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

/// <summary>
/// 在服务器把需要编队的 Ani 关联到其 GhostOwner 对应的玩家主角
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ServerMovementOrderReceiveRpcSystem))]
[UpdateBefore(typeof(AniFormationManagementSystem))]
public partial struct AniFormationJoinRequestSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookup;
    private ComponentLookup<GhostOwner> _ghostOwnerLookup;

    /// <summary>
    /// 缓存黑板和 GhostOwner 查询，并等待行为系统初始化
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        _ghostOwnerLookup = state.GetComponentLookup<GhostOwner>(isReadOnly: true);

        // FsmContext 存在时 Ani 行为数据已经完成初始化
        state.RequireForUpdate<FsmContext>();
    }

    /// <summary>
    /// 根据移动模式和连接拥有权生成无重复的加入阵型请求
    /// </summary>
    /// <param name="state">系统运行状态</param>
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);
        _ghostOwnerLookup.Update(ref state);

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 先建立连接编号到玩家主角的映射，避免对每个 Ani 重复扫描
        var leadersByNetworkId =
            new NativeParallelHashMap<int, Entity>(16, Allocator.Temp);

        foreach (var (owner, entity) in
                 SystemAPI.Query<RefRO<GhostOwner>>()
                          .WithAll<CharacterTag>()
                          .WithEntityAccess())
        {
            int networkId = owner.ValueRO.NetworkId;
            leadersByNetworkId.TryAdd(networkId, entity);
        }

        // 仅为具有移动黑板和网络拥有权的 Ani 生成请求
        foreach (var (attributes, entity) in
                 SystemAPI.Query<RefRO<AniAttributes>>()
                          .WithEntityAccess())
        {
            if (SystemAPI.HasComponent<AniFormationJoinRequest>(entity))
                continue;

            if (!_blackboardLookup.HasBuffer(entity))
                continue;

            if (!_ghostOwnerLookup.HasComponent(entity))
                continue;

            var blackboard = _blackboardLookup[entity];

            var commandMode = (AniMovementCommandMode)
                Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.CommandMode);

            // 跟随、寻敌和移动到目标点时才需要稳定阵型槽位
            bool needsFormation =
                commandMode == AniMovementCommandMode.Follow ||
                commandMode == AniMovementCommandMode.MoveTo ||
                commandMode == AniMovementCommandMode.Find;

            if (!needsFormation)
                continue;

            // 使用 Ani 自身 GhostOwner 查找同一连接拥有的玩家主角
            int ownerNetworkId = _ghostOwnerLookup[entity].NetworkId;

            if (!leadersByNetworkId.TryGetValue(ownerNetworkId, out Entity leader))
            {
                // 玩家主角尚未生成时延后到后续帧重试
                continue;
            }

            bool hasMember = SystemAPI.HasComponent<AniFormationMember>(entity);
            if (hasMember)
            {
                var member = SystemAPI.GetComponent<AniFormationMember>(entity);
                if (member.leader == leader)
                    continue;
            }

            // 保持请求幂等，避免结构变更重复入队
            if (SystemAPI.HasComponent<AniFormationJoinRequest>(entity))
                continue;

            entityCommandBuffer.AddComponent(entity, new AniFormationJoinRequest
            {
                leader = leader
            });
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
        leadersByNetworkId.Dispose();
    }
}
