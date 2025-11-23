using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientGameOverRpcSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (rpc, req, entity) in
                 SystemAPI.Query<RefRO<GameOverRpc>, RefRO<ReceiveRpcCommandRequest>>()
                     .WithEntityAccess())
        {
            CampType localCamp = CampType.Alpha;
            if (SystemAPI.HasSingleton<LocalPlayerCamp>())
            {
                localCamp = SystemAPI.GetSingleton<LocalPlayerCamp>().Value;
            }

            UnityEngine.Debug.LogWarning($"[ClientGameOverRpcSystem] Game Over received. Winner = {rpc.ValueRO.Winner}, LocalCamp = {localCamp}");

            bool isWin = (localCamp == rpc.ValueRO.Winner);
            GameOverUIBridge.ShowGameOver(isWin);

            // 用完删掉
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}

