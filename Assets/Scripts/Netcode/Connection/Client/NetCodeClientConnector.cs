using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

/// <summary>
/// 提供 UI 向 Client World 发起连接请求的入口
/// </summary>
public static class NetCodeClientConnector
{
    /// <summary>
    /// 校验连接状态和地址后创建 NetCode 连接请求
    /// </summary>
    /// <param name="ipAddress">服务器 IP 地址</param>
    /// <param name="port">服务器监听端口</param>
    public static void RequestConnect(string ipAddress, ushort port)
    {
        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null)
        {
            Debug.LogError("[Client] 未找到 Client World，无法发起连接请求。");
            return;
        }

        var entityManager = clientWorld.EntityManager;

        // 连接、请求和握手中的任一状态存在时都禁止重复发起请求
        if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
        {
            Debug.Log("[Client] 已经处于连接状态，忽略新的连接请求。");
            return;
        }

        if (!entityManager.CreateEntityQuery(typeof(NetworkStreamRequestConnect)).IsEmpty)
        {
            Debug.Log("[Client] 已经有连接请求存在，忽略新的连接请求。");
            return;
        }

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
