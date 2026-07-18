using AnimarsCatcher.Gameplay.Contracts;
using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;

/// <summary>
/// 在客户端消费服务器胜负 RPC 并转换为本地界面结果
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ClientGameOverRpcSystem : ISystem
{
    /// <summary>
    /// 等待客户端进入游戏网络流后再接收结算消息
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<NetworkStreamInGame>();
    }

    /// <summary>
    /// 比较胜利阵营与本地阵营并在消费后销毁 RPC 实体
    /// </summary>
    /// <param name="state">系统运行状态</param>
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

            // RPC 实体只允许消费一次
            ecb.DestroyEntity(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}

