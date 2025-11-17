using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

public static class NetCodeServerController
{
    // 在 Server World 中发起监听请求（创建 NetworkStreamRequestListen）
    // 如果已经在监听则忽略
    public static void StartListen(ushort port)
    {
        var serverWorld = WorldManager.FindServerWorld();
        if (serverWorld == null)
        {
            Debug.LogError("[Server] 未找到 Server World，无法开始监听。");
            return;
        }

        var entityManager = serverWorld.EntityManager;

        // 已经有监听请求了
        if (!entityManager.CreateEntityQuery(typeof(NetworkStreamRequestListen)).IsEmpty)
        {
            Debug.Log("[Server] 已经存在 NetworkStreamRequestListen，跳过。");
            return;
        }

        var endpoint = NetworkEndpoint.AnyIpv4.WithPort(port);

        var requestEntity = entityManager.CreateEntity();
        entityManager.AddComponentData(requestEntity, new NetworkStreamRequestListen { Endpoint = endpoint });
        entityManager.SetName(requestEntity, "ServerListenRequest (From UI)");

        Debug.Log($"[Server] Start listening on {endpoint.Address}:{endpoint.Port}");
    }
}
