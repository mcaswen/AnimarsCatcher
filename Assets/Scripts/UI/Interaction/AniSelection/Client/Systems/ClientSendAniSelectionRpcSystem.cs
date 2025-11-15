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

        // 创建 RPC 请求
        var rpcEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(rpcEntity, new AniSelectionApplyRpc { Append = 0 });
        state.EntityManager.AddComponent<SendRpcCommandRequest>(rpcEntity);

        var buffer = state.EntityManager.AddBuffer<SelectedAniGhostRef>(rpcEntity);

        // 遍历命中的 Ani，把 GhostInstance 写入缓冲
        foreach (var (localToWorld, ghostInstance) in SystemAPI.Query<RefRO<LocalToWorld>, RefRO<GhostInstance>>().WithAll<AniAttributes, GhostOwner>())
        {
            var screenPoint = camera.WorldToScreenPoint((Vector3)localToWorld.ValueRO.Position);
            if (screenPoint.z < 0) continue;

            var p = new float2(screenPoint.x, screenPoint.y);
            bool inside = p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y;

            if (inside)
            {
                buffer.Add(new SelectedAniGhostRef{ AniGhostId = ghostInstance.ValueRO.ghostId });
            }
        }
    }
}
