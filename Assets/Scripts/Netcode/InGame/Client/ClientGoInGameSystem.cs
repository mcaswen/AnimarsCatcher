using Unity.NetCode;
using Unity.Entities;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct ClientGoInGameSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // 非 Editor 平台下禁用自动连接
#if !UNITY_EDITOR
        return;
#else
        // Editor 下只有调试场景才自动 InGame
        if (SceneManager.GetActiveScene().name != "GameLevel")
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
        UnityEngine.Debug.Log("[Client][Editor GameLevel] Auto sent GoInGameRequest and marked InGame locally.");
    }
}
