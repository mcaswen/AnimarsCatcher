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

    [BurstCompile]
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

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        Entity rpcEntity = entityCommandBuffer.CreateEntity();
        entityCommandBuffer.AddComponent(rpcEntity, new MovementOrderRpc
        {
            TargetKind = result.TargetKind,
            TargetWorldPosition = result.TargetWorldPosition,
            TargetEntity = result.TargetEntity
        });

        UnityEngine.Debug.Log($"[MovementOrderSendRpcSystem] Sending MovementOrderRpc: TargetKind={result.TargetKind}, TargetWorldPosition={result.TargetWorldPosition}");

        entityCommandBuffer.AddComponent(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connection
        });

        entityCommandBuffer.Playback(state.EntityManager);
    }
}
