using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartServerListenSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        bool shouldCreateListenRequest = false;

#if UNITY_EDITOR

        shouldCreateListenRequest = true; // 编辑器里由 PlayMode Tools 负责监听

#else

        shouldCreateListenRequest = CommandLineManager.HasArg("-server") || CommandLineManager.HasArg("-serverui");

#endif

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

        var endPoint = NetworkEndpoint.AnyIpv4.WithPort(NetPorts.Game);
        var requestListenEntity = state.EntityManager.CreateEntity();
        
        state.EntityManager.SetName(requestListenEntity, "ServerListenRequest (Custom)");
        state.EntityManager.AddComponentData(requestListenEntity, new NetworkStreamRequestListen { Endpoint = endPoint });
    }

}
