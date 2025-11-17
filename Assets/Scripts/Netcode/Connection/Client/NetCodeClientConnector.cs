using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

public static class NetCodeClientConnector
{
    public static void RequestConnect(string ipAddress, ushort port)
    {
        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null)
        {
            Debug.LogError("[Client] 未找到 Client World，无法发起连接请求。");
            return;
        }

        var entityManager = clientWorld.EntityManager;

        // 已经连上
        if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
        {
            Debug.Log("[Client] 已经处于连接状态，忽略新的连接请求。");
            return;
        }

        // 已有连接请求
        if (!entityManager.CreateEntityQuery(typeof(NetworkStreamRequestConnect)).IsEmpty)
        {
            Debug.Log("[Client] 已经有连接请求存在，忽略新的连接请求。");
            return;
        }

        // 正在连接中
        if (!entityManager.CreateEntityQuery(typeof(NetworkStreamConnection)).IsEmpty)
        {
            Debug.Log("[Client] 已有连接中的 NetworkStreamConnection，忽略新的连接请求。");
            return;
        }

        if (!NetworkEndpoint.TryParse(ipAddress, port, out var endpoint))
        {
            Debug.LogError($"[Client] 无法解析 IP 地址: {ipAddress}，端口: {port}");
            return;
        }

        var requestEntity = entityManager.CreateEntity();
        entityManager.AddComponentData(requestEntity, new NetworkStreamRequestConnect { Endpoint = endpoint });

        Debug.Log($"[Client] Connect Request Sent -> {endpoint.Address}:{endpoint.Port}");
    }
}
