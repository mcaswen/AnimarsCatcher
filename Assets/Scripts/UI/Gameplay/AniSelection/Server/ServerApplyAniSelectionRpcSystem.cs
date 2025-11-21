using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using System.Diagnostics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerApplySelectionRpcSystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _ghostIdToEntity;

    public void OnCreate(ref SystemState state)
    {
        _ghostIdToEntity = new NativeParallelHashMap<int, Entity>(200, Allocator.Persistent);

        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<AniAttributes, GhostInstance, GhostOwner>().Build());
    }

    public void OnDestroy(ref SystemState state)
    {
        if (_ghostIdToEntity.IsCreated) _ghostIdToEntity.Dispose();
    }

    public void OnUpdate(ref SystemState state)
    {
        // 建表：ghostId 与 entity 的映射
        _ghostIdToEntity.Clear();
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (ghostInstance, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>>()
                          .WithAll<AniAttributes>()
                          .WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        // 处理所有 AniSelectionApplyRpc
        foreach (var (rpc, requestedEntity) in SystemAPI
                     .Query<RefRO<AniSelectionApplyRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            var senderNetId = SystemAPI.GetComponent<ReceiveRpcCommandRequest>(requestedEntity).SourceConnection;
            int playerNetId = SystemAPI.GetComponent<NetworkId>(senderNetId).Value;

            bool append = rpc.ValueRO.Append != 0;

            // 替换模式：先清空旧选择
            if (!append)
            {
                foreach (var (owner, aniEntity) in SystemAPI
                             .Query<RefRO<GhostOwner>>()
                             .WithAll<AniAttributes, AniSelectedTag>()
                             .WithEntityAccess())
                {
                    if (owner.ValueRO.NetworkId == playerNetId)
                    {
                        entityCommandBuffer.SetComponentEnabled<AniSelectedTag>(aniEntity, false);
                    }
                }
            }

            /// 应用本次选择
            var ghostIds = rpc.ValueRO.GhostIds;
            for (int i = 0; i < ghostIds.Length; i++)
            {
                var ghostId = ghostIds[i];
                if (!_ghostIdToEntity.TryGetValue(ghostId, out var aniEntity))
                    continue;

                var owner = SystemAPI.GetComponent<GhostOwner>(aniEntity);
                if (owner.NetworkId == playerNetId)
                {
                    entityCommandBuffer.SetComponentEnabled<AniSelectedTag>(aniEntity, true);
                    UnityEngine.Debug.Log($"[ServerApplyAniSelectionRpcSystem] Selected Ani GhostId: {ghostId}.");
                }
            }

            // 消费并销毁 RPC 实体
            entityCommandBuffer.DestroyEntity(requestedEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
