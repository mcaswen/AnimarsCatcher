using Unity.Burst;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

#if UNITY_EDITOR
using UnityEngine.SceneManagement;
#endif

/// <summary>
/// 在编辑器游戏场景中自动创建本机客户端连接请求
/// </summary>
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct StartClientConnectSystem : ISystem
{
    /// <summary>
    /// 仅执行一次编辑器自动连接并随后禁用系统
    /// </summary>
    /// <param name="state">系统状态</param>
    public void OnCreate(ref SystemState state)
    {
#if UNITY_EDITOR
        if (SceneManager.GetActiveScene().name != "SCN_GameLevel") return;

        if (AlreadyConnectedOrConnecting(ref state))
        {
            return;
        }
        var endPoint = NetworkEndpoint.LoopbackIpv4.WithPort(NetworkPorts.Game);

        var entity = state.EntityManager.CreateEntity();
        state.EntityManager.AddComponentData(entity, new NetworkStreamRequestConnect { Endpoint = endPoint });

        UnityEngine.Debug.Log("[Client] Connect Request Sent!");
#endif

    state.Enabled = false;
    
    }

    // 同时检查已连接、待处理请求和握手状态，防止创建重复连接实体
    private bool AlreadyConnectedOrConnecting(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<NetworkId>()) return true; // 已连接
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamRequestConnect>().Build().IsEmpty) return true; // 已有请求
        if (!SystemAPI.QueryBuilder().WithAll<NetworkStreamConnection>().Build().IsEmpty) return true;     // 连接中
        return false;
    }
}
