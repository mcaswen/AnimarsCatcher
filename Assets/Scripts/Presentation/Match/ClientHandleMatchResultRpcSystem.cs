using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;

namespace AnimarsCatcher.Presentation.Match
{
    /// <summary>
    /// 在客户端处理服务器胜负 RPC，并转换为本地界面结果
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ClientHandleMatchResultRpcSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, req, entity) in
                     SystemAPI.Query<RefRO<MatchResultRpc>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                CampType localCamp = CampType.Alpha;
                if (SystemAPI.HasSingleton<LocalPlayerCamp>())
                {
                    localCamp = SystemAPI.GetSingleton<LocalPlayerCamp>().Value;
                }

                UnityEngine.Debug.LogWarning($"[ClientHandleMatchResultRpcSystem] Game Over received. Winner = {rpc.ValueRO.Winner}, LocalCamp = {localCamp}");

                bool isWin = (localCamp == rpc.ValueRO.Winner);
                MatchResultUIBridge.ShowMatchResult(isWin);

                // 每个 RPC Entity 只处理一次
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
