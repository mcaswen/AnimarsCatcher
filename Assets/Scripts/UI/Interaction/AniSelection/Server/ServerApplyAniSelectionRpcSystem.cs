using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;

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

        foreach (var (ghostInstance, entity) in SystemAPI.Query<RefRO<GhostInstance>>().WithAll<AniAttributes>().WithEntityAccess())
        {
            _ghostIdToEntity.TryAdd(ghostInstance.ValueRO.ghostId, entity);
        }

        // 处理所有 AniSelectionApplyRpc
        foreach (var (rpc, buffer, requestedEntity) in SystemAPI
                     .Query<RefRO<AniSelectionApplyRpc>, DynamicBuffer<SelectedAniGhostRef>>()
                     .WithAll<ReceiveRpcCommandRequest>()
                     .WithEntityAccess())
        {
            var senderNetId = SystemAPI.GetComponent<ReceiveRpcCommandRequest>(requestedEntity).SourceConnection;
            int playerNetId = SystemAPI.GetComponent<NetworkId>(senderNetId).Value;

            bool append = rpc.ValueRO.Append != 0;

            // 替换模式：先清空旧选择
            if (!append)
            {
                foreach (var (attributes, owner) in SystemAPI.Query<RefRW<AniAttributes>, RefRO<GhostOwner>>())
                {
                    if (owner.ValueRO.NetworkId == playerNetId)
                        attributes.ValueRW.IsSelected = false;
                }
            }

            // 对缓冲里每个 ghostId 进行查表 -> 归属校验 -> 置位的流程
            for (int i = 0; i < buffer.Length; i++)
            {
                var item = buffer[i];
                if (!_ghostIdToEntity.TryGetValue(item.AniGhostId, out var entity))
                    continue;

                // 归属校验
                var owner = SystemAPI.GetComponent<GhostOwner>(entity);
                if (owner.NetworkId == playerNetId)
                {
                    var attributes = SystemAPI.GetComponentRW<AniAttributes>(entity);
                    attributes.ValueRW.IsSelected = true;
                }
            }

            // 消费并销毁 RPC 实体
            entityCommandBuffer.DestroyEntity(requestedEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
