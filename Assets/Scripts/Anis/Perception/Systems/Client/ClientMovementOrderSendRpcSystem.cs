using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct MovementOrderSendRpcSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MovementClickResult>();
        state.RequireForUpdate<MovementClickProcessedVersion>();
        state.RequireForUpdate<NetworkStreamInGame>();
    }

    // [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        MovementClickResult result = SystemAPI.GetSingleton<MovementClickResult>();
        RefRW<MovementClickProcessedVersion> processed =
            SystemAPI.GetSingletonRW<MovementClickProcessedVersion>();

        if (result.Version == 0 || result.Version == processed.ValueRO.Version)
            return;

        processed.ValueRW.Version = result.Version;

        if (result.TargetKind == MovementTargetKind.None)
            return;

        // 找到这条连接（客户端只有一条到服务器的连接）
        Entity connection = SystemAPI.GetSingletonEntity<NetworkStreamInGame>();
        int localNetworkId = SystemAPI.GetComponent<NetworkId>(connection).Value;

        // -------- 收集当前“选中的 Ani”快照（GhostId 列表） --------
        var selectedAniGhostIds = new FixedList128Bytes<int>();

        foreach (var (ghostInstance, owner) in
                SystemAPI.Query<RefRO<GhostInstance>, RefRO<GhostOwner>>()
                        .WithAll<AniSelectedTag>()
                        .WithNone<AniCommandLockedTag>())
        {
            // 保险起见，只拿本地玩家的 Ani
            if (owner.ValueRO.NetworkId != localNetworkId)
                continue;

            if (selectedAniGhostIds.Length >= selectedAniGhostIds.Capacity)
                break;

            selectedAniGhostIds.Add(ghostInstance.ValueRO.ghostId);
        }

        // 没有选中任何 Ani
        if (selectedAniGhostIds.Length == 0)
            return;

        // -------- 创建 RPC 实体并发给服务端 --------
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        Entity rpcEntity = entityCommandBuffer.CreateEntity();
        entityCommandBuffer.AddComponent(rpcEntity, new MovementOrderRpc
        {
            TargetKind          = result.TargetKind,
            TargetWorldPosition = result.TargetWorldPosition,
            TargetEntity        = result.TargetEntity,
            SelectedAniGhostIds = selectedAniGhostIds
        });

        UnityEngine.Debug.Log(
            $"[MovementOrderSendRpcSystem] Sending MovementOrderRpc: " +
            $"TargetKind={result.TargetKind}, TargetWorldPosition={result.TargetWorldPosition}, " +
            $"AniCount={selectedAniGhostIds.Length}");

        entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connection
        });

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
