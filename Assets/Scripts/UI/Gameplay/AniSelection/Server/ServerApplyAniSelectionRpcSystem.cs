using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using System.Diagnostics;

/// <summary>
/// 在服务端校验 Ani 选择 RPC 的所有权并更新选中标签
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ServerApplyAniSelectionRpcSystem : ISystem
{
    private NativeParallelHashMap<int, Entity> _ghostIdToEntity;

    /// <summary>
    /// 创建持久 GhostId 索引并等待 Ani Ghost 可用
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        _ghostIdToEntity = new NativeParallelHashMap<int, Entity>(200, Allocator.Persistent);

        state.RequireForUpdate(SystemAPI.QueryBuilder()
            .WithAll<AniAttributes, GhostInstance, GhostOwner>().Build());
    }

    /// <summary>
    /// 释放持久 GhostId 索引
    /// </summary>
    public void OnDestroy(ref SystemState state)
    {
        if (_ghostIdToEntity.IsCreated) _ghostIdToEntity.Dispose();
    }

    /// <summary>
    /// 重建 GhostId 映射并消费全部选择 RPC
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        // 每帧重建映射以覆盖 Ghost 生成和销毁变化
        _ghostIdToEntity.Clear();
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (ghostInstance, entity) in
                 SystemAPI.Query<RefRO<GhostInstance>>()
                          .WithAll<AniAttributes>()
                          .WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        // 按发送连接解析玩家 NetworkId 并逐条处理选择请求
        foreach (var (rpc, requestedEntity) in SystemAPI
                     .Query<RefRO<AniSelectionApplyRpc>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            var senderConnectionEntity =
                SystemAPI.GetComponent<ReceiveRpcCommandRequest>(requestedEntity).SourceConnection;
            int playerNetworkId =
                SystemAPI.GetComponent<NetworkId>(senderConnectionEntity).Value;

            bool append = rpc.ValueRO.Append != 0;

            // 替换模式先清除发送玩家原有选择
            if (!append)
            {
                foreach (var (owner, aniEntity) in SystemAPI
                             .Query<RefRO<GhostOwner>>()
                             .WithAll<AniAttributes, AniSelectedTag>()
                             .WithEntityAccess())
                {
                    if (owner.ValueRO.NetworkId == playerNetworkId)
                    {
                        entityCommandBuffer.SetComponentEnabled<AniSelectedTag>(aniEntity, false);
                    }
                }
            }

            // 只允许发送玩家选择自己拥有的 Ani
            var ghostIds = rpc.ValueRO.GhostIds;
            for (int i = 0; i < ghostIds.Length; i++)
            {
                var ghostId = ghostIds[i];
                if (!_ghostIdToEntity.TryGetValue(ghostId, out var aniEntity))
                    continue;

                var owner = SystemAPI.GetComponent<GhostOwner>(aniEntity);
                if (owner.NetworkId == playerNetworkId)
                {
                    entityCommandBuffer.SetComponentEnabled<AniSelectedTag>(aniEntity, true);
                    UnityEngine.Debug.Log($"[ServerApplyAniSelectionRpcSystem] Selected Ani GhostId: {ghostId}.");
                }
            }

            // RPC 是一次性命令 处理完成后销毁实体
            entityCommandBuffer.DestroyEntity(requestedEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
