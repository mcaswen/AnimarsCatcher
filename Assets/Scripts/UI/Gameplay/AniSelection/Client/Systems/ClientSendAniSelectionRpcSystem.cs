using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using Unity.NetCode;
using Unity.Mathematics;

/// <summary>
/// 将本地框选矩形内符合模式和所有权条件的 Ani 打包为 RPC
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientSendAniSelectionRpcSystem : ISystem
{
    private ComponentLookup<PickerAniTag> _pickerLookup;
    private ComponentLookup<BlasterAniTag> _blasterLookup;

    public void OnCreate(ref SystemState state)
    {
        _pickerLookup = state.GetComponentLookup<PickerAniTag>(true);
        _blasterLookup = state.GetComponentLookup<BlasterAniTag>(true);

        state.RequireForUpdate<AniSelectionDragState>();
        state.RequireForUpdate<NetworkStreamInGame>();
        state.RequireForUpdate<AniSelectionModeSingleton>();
    }

    public void OnUpdate(ref SystemState state)
    {
        _pickerLookup.Update(ref state);
        _blasterLookup.Update(ref state);

        var drag = SystemAPI.GetSingletonRW<AniSelectionDragState>();
        if (drag.ValueRO.IsReleased == 0) return;

        // 立即消费释放标记 保证一次拖拽最多发送一个 RPC
        drag.ValueRW.IsReleased = 0;

        // 将任意拖拽方向归一化为屏幕空间包围盒
        float2 start = drag.ValueRO.StartScreen;
        float2 end = drag.ValueRO.EndScreen;
        float2 min = math.min(start, end);
        float2 max = math.max(start, end);

        var camera = Camera.main;
        var localId = SystemAPI.GetSingleton<NetworkId>();

        if (!SystemAPI.TryGetSingleton<AniSelectionModeSingleton>(out var modeSingleton))
        {
            Debug.LogError("[ClientSendAniSelectionRpcSystem] AniSelectionModeSingleton not found!");
            return;
        }
        AniSelectionMode selectionMode = modeSingleton.Mode;

        // FixedList 避免为单次选择额外分配托管内存
        AniSelectionApplyRpc rpcData = default;
        rpcData.Append = 0;
        rpcData.GhostIds = default;

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        // 将符合类型 所有权和屏幕范围的 Ani 写入 RPC
        foreach (var (localToWorld, ghostInstance, ghostOwner, aniEntity) in SystemAPI
                .Query<RefRO<LocalToWorld>, RefRO<GhostInstance>, RefRO<GhostOwner>>()
                .WithAll<AniAttributes>()
                .WithEntityAccess())
        {
            bool isPicker  = _pickerLookup.HasComponent(aniEntity);
            bool isBlaster = _blasterLookup.HasComponent(aniEntity);

            Debug.Log($"[ClientSendAniSelectionRpcSystem] Checking Ani Entity {aniEntity} - isPicker: {isPicker}, isBlaster: {isBlaster}");

            // 当前模式只允许选择对应类型的 Ani
            switch (selectionMode)
            {
                case AniSelectionMode.Picker:
                    if (!isPicker)
                        continue;
                    break;

                case AniSelectionMode.Blaster:
                    if (!isBlaster)
                        continue;
                    break;
            }


            if (ghostOwner.ValueRO.NetworkId != localId.Value)
                continue;

            var screenPoint = camera.WorldToScreenPoint((Vector3)localToWorld.ValueRO.Position);
            if (screenPoint.z < 0) continue;

            var position = new float2(screenPoint.x, screenPoint.y);
            bool inside = position.x >= min.x && position.x <= max.x && position.y >= min.y && position.y <= max.y;

            if (!inside) continue;

            if (rpcData.GhostIds.Length < 128) // FixedList 容量限制为 128 个标识
            {
                rpcData.GhostIds.Add(ghostInstance.ValueRO.ghostId);
            }
        }

        if (rpcData.GhostIds.Length == 0)
        {
            Debug.Log("[ClientSendAniSelectionRpcSystem] No Ani in selection, skip sending RPC.");
            return;
        }

        // 创建无指定目标的 RPC 请求 由当前服务器连接接收
        var rpcEntity = entityCommandBuffer.CreateEntity();
        entityCommandBuffer.AddComponent(rpcEntity, rpcData);
        entityCommandBuffer.AddComponent<SendRpcCommandRequest>(rpcEntity);

        Debug.Log($"[ClientSendAniSelectionRpcSystem] Rpc sent with {rpcData.GhostIds.Length} Ani.");

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
