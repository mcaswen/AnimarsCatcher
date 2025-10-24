using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartClientConnectSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        if (AlreadyConnectedOrConnecting(ref state))
        {
            return;
        }

        var endPoint = NetworkEndpoint.LoopbackIpv4.WithPort(NetPorts.Game);
        var entity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(entity, new NetworkStreamRequestConnect { Endpoint = endPoint });
    }

    private bool AlreadyConnectedOrConnecting(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<NetworkId>()) return true; // 已连接
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestConnect>().Build().IsEmpty) return true; // 已有请求
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamConnection>().Build().IsEmpty) return true;     // 连接中
        return false;
    }
}
