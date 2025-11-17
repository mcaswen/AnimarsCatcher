using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartServerListenSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        // 在 Editor 里，且当前场景是 GameLevel，自动监听
        if (SceneManager.GetActiveScene().name != "GameLevel") return;
        
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestListen>().Build().IsEmpty)
        {
            state.Enabled = false;
            return;
        }

        var endPoint = NetworkEndpoint.AnyIpv4.WithPort(NetPorts.Game);
        var requestListenEntity = state.EntityManager.CreateEntity();

        state.EntityManager.SetName(requestListenEntity, "ServerListenRequest (Editor GameLevel Auto)");
        state.EntityManager.AddComponentData(requestListenEntity, new NetworkStreamRequestListen
        {
            Endpoint = endPoint
        });


        state.Enabled = false;
        return;
#endif

#if !UNITY_EDITOR
        bool shouldCreateListenRequest =
            CommandLineManager.HasArg("-server") ||
            CommandLineManager.HasArg("-serverui") ||
            CommandLineManager.HasArg("-dedicated");

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

        var endPointRuntime = NetworkEndpoint.AnyIpv4.WithPort(NetPorts.Game);
        var requestEntityRuntime = state.EntityManager.CreateEntity();

        state.EntityManager.SetName(requestEntityRuntime, "ServerListenRequest (Runtime)");
        state.EntityManager.AddComponentData(requestEntityRuntime, new NetworkStreamRequestListen
        {
            Endpoint = endPointRuntime
        });
#else
        // 非 GameLevel 场景，完全由 UI / HostRoomPanel 决定是否监听
        state.Enabled = false;
#endif


    }

}
