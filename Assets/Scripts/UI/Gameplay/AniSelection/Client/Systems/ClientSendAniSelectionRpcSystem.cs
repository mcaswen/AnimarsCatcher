using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Unity.NetCode;
using Unity.Mathematics;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientSendAniSelectionRpcSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AniSelectionDragState>();
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<AniAttributes, LocalToWorld, GhostInstance>().Build());
        state.RequireForUpdate<NetworkId>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var drag = SystemAPI.GetSingletonRW<AniSelectionDragState>();
        if (drag.ValueRO.IsReleased == 0) return;

        // 消费 IsReleased
        drag.ValueRW.IsReleased = 0;

        // 计算本地筛选范围
        float2 start = drag.ValueRO.StartScreen;
        float2 end = drag.ValueRO.EndScreen;
        float2 min = math.min(start, end);
        float2 max = math.max(start, end);

        var camera = Camera.main;
        var localId = SystemAPI.GetSingleton<NetworkId>();

        // 先在栈里把数据收集好
        AniSelectionApplyRpc rpcData = default;
        rpcData.Append = 0;
        rpcData.GhostIds = default;

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 遍历命中的 Ani，把 GhostInstance 写入缓冲
        foreach (var (localToWorld, ghostInstance, ghostOwner) in SystemAPI.Query<RefRO<LocalToWorld>, RefRO<GhostInstance>, RefRO<GhostOwner>>().WithAll<AniAttributes>())
        {
            if (ghostOwner.ValueRO.NetworkId != localId.Value)
                continue;

            var screenPoint = camera.WorldToScreenPoint((Vector3)localToWorld.ValueRO.Position);
            if (screenPoint.z < 0) continue;

            var position = new float2(screenPoint.x, screenPoint.y);
            bool inside = position.x >= min.x && position.x <= max.x && position.y >= min.y && position.y <= max.y;

            if (!inside) continue;

            if (rpcData.GhostIds.Length < 128) // 最大128元素
            {
                rpcData.GhostIds.Add(ghostInstance.ValueRO.ghostId);
            }

            if (rpcData.GhostIds.Length == 0) // 没有选中任何 Ani，跳过发送 RPC
            {
                Debug.Log("[ClientSendAniSelectionRpcSystem] No Ani in selection, skip sending RPC.");
                return;
            }

            // 创建 RPC 实体并附加数据
            var rpcEntity = entityCommandBuffer.CreateEntity();
            entityCommandBuffer.AddComponent(rpcEntity, rpcData);
            entityCommandBuffer.AddComponent<SendRpcCommandRequest>(rpcEntity);

            Debug.Log($"[ClientSendAniSelectionRpcSystem] Rpc sent with {rpcData.GhostIds.Length} Ani.");
        }
        entityCommandBuffer.Playback(state.EntityManager);
    }
}
