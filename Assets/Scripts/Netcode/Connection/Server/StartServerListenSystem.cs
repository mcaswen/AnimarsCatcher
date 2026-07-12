using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

/// <summary>根据编辑器场景或运行时启动参数创建服务器监听请求</summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartServerListenSystem : ISystem
{
    /// <summary>仅执行一次自动监听决策并避免重复监听实体</summary>
    /// <param name="state">系统状态</param>
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        // 编辑器游戏场景自动监听，便于 Host 与 Client 本机联调
        if (SceneManager.GetActiveScene().name != "SCN_GameLevel") return;
        
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestListen>().Build().IsEmpty)
        {
            state.Enabled = false;
            return;
        }

        var endPoint = NetworkEndpoint.AnyIpv4.WithPort(NetworkPorts.Game);
        var requestListenEntity = state.EntityManager.CreateEntity();

        state.EntityManager.SetName(requestListenEntity, "ServerListenRequest (Editor SCN_GameLevel Auto)");
        state.EntityManager.AddComponentData(requestListenEntity, new NetworkStreamRequestListen
        {
            Endpoint = endPoint
        });


        state.Enabled = false;
        return;
#endif

#if !UNITY_EDITOR
        // Player 构建只有显式服务器角色才允许绑定监听端口
        bool shouldCreateListenRequest =
            CommandLineManager.HasArgument("-server") ||
            CommandLineManager.HasArgument("-serverui") ||
            CommandLineManager.HasArgument("-dedicated");

        if (!shouldCreateListenRequest)
        {
            state.Enabled = false;
            return;
        }

        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestListen>().Build().IsEmpty)
        {
            state.Enabled = false;
            return;
        }

        // AnyIpv4 允许局域网和远端客户端访问服务器端口
        var endPointRuntime = NetworkEndpoint.AnyIpv4.WithPort(NetworkPorts.Game);
        var requestEntityRuntime = state.EntityManager.CreateEntity();

        state.EntityManager.SetName(requestEntityRuntime, "ServerListenRequest (Runtime)");
        state.EntityManager.AddComponentData(requestEntityRuntime, new NetworkStreamRequestListen
        {
            Endpoint = endPointRuntime
        });
#else
        // 其他编辑器场景由 HostRoomPanel 显式决定监听时机
        state.Enabled = false;
#endif


    }

}
