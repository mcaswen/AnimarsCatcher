using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartClientConnectSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        if (SceneManager.GetActiveScene().name != "GameLevel") return;

        if (AlreadyConnectedOrConnecting(ref state))
        {
            return;
        }
        var endPoint = NetworkEndpoint.LoopbackIpv4.WithPort(NetPorts.Game);

        var entity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(entity, new NetworkStreamRequestConnect { Endpoint = endPoint });

        UnityEngine.Debug.Log("[Client] Connect Request Sent!");
#endif

    state.Enabled = false;
    
    }

    private bool AlreadyConnectedOrConnecting(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<NetworkId>()) return true; // 已连接
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestConnect>().Build().IsEmpty) return true; // 已有请求
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamConnection>().Build().IsEmpty) return true;     // 连接中
        return false;
    }
}
