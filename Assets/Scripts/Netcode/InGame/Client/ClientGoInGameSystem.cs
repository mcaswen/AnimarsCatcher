using Unity.NetCode;
using Unity.Entities;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

/// <summary>在编辑器游戏场景中自动完成客户端 InGame 调试握手</summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct ClientGoInGameSystem : ISystem
{
    /// <summary>连接建立后发送一次 GoInGameRequest 并标记本地连接</summary>
    /// <param name="state">系统状态</param>
    public void OnUpdate(ref SystemState state)
    {
        // Player 构建必须由正式大厅流程控制 InGame 状态
#if !UNITY_EDITOR
        return;
#else
        // 编辑器只在游戏调试场景跳过大厅握手
        if (SceneManager.GetActiveScene().name != "SCN_GameLevel")
        {
            return;
        }
#endif

        if (!SystemAPI.TryGetSingletonEntity<NetworkId>(out var connection)) return; // 还没连上服务器

        if (SystemAPI.HasComponent<NetworkStreamInGame>(connection)) return; // 已 InGame

        var rpcEntity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(rpcEntity, new GoInGameRequest());
        state.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connection
        });

        state.EntityManager.AddComponent<NetworkStreamInGame>(connection);
        UnityEngine.Debug.Log("[Client][Editor SCN_GameLevel] Auto sent GoInGameRequest and marked InGame locally.");
    }
}
