using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(AniFormationManagementSystem))]
public partial struct AniFormationJoinRequestSystem : ISystem
{
    private BufferLookup<FsmVar> _blackboardLookup;
    private ComponentLookup<GhostOwner> _ghostOwnerLookup;

    public void OnCreate(ref SystemState state)
    {
        _blackboardLookup = state.GetBufferLookup<FsmVar>(isReadOnly: false);
        _ghostOwnerLookup = state.GetComponentLookup<GhostOwner>(isReadOnly: true);

        // 有 FsmContext 才说明 Ani 行为树等都已经建好了
        state.RequireForUpdate<FsmContext>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        _blackboardLookup.Update(ref state);
        _ghostOwnerLookup.Update(ref state);

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 先建立 每个 NetworkId -> 对应玩家主角（leader） 的映射
        var leadersByNetworkId =
            new NativeParallelHashMap<int, Entity>(16, Allocator.Temp);

        foreach (var (owner, entity) in
                 SystemAPI.Query<RefRO<GhostOwner>>()
                          .WithAll<CharacterTag>()   
                          .WithEntityAccess())
        {
            int netId = owner.ValueRO.NetworkId;
            leadersByNetworkId.TryAdd(netId, entity);
        }

        // 给需要排阵的 Ani 生成 JoinRequest
        foreach (var (attributes, entity) in
                 SystemAPI.Query<RefRO<AniAttributes>>()
                          .WithEntityAccess())
        {
            if (SystemAPI.HasComponent<AniFormationMember>(entity))
                continue;

            if (SystemAPI.HasComponent<AniFormationJoinRequest>(entity))
                continue;

            if (!_blackboardLookup.HasBuffer(entity))
                continue;

            if (!_ghostOwnerLookup.HasComponent(entity))
                continue;

            var blackboard = _blackboardLookup[entity];

            var commandMode = (AniMovementCommandMode)
                Blackboard.GetInt(ref blackboard, AniMovementBlackboardKeys.K_CommandMode);

            // 只有这些模式才需要排队
            bool needsFormation =
                commandMode == AniMovementCommandMode.Follow ||
                commandMode == AniMovementCommandMode.MoveTo ||
                commandMode == AniMovementCommandMode.Find;

            if (!needsFormation)
                continue;

            // 用 Ani 自己的 GhostOwner.NetworkId 找到对应的玩家主角
            int ownerNetId = _ghostOwnerLookup[entity].NetworkId;

            if (!leadersByNetworkId.TryGetValue(ownerNetId, out Entity leader))
            {
                // 对应的玩家主角还没 spawn / 没打 CharacterTag
                continue;
            }

            entityCommandBuffer.AddComponent(entity, new AniFormationJoinRequest
            {
                leader = leader   // 始终是控制它的玩家实体
            });
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
        leadersByNetworkId.Dispose();
    }
}
