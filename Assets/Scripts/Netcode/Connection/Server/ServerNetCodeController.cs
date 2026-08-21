namespace AnimarsCatcher.Networking
{
    using Unity.Entities;
    using Unity.NetCode;
    using Unity.Networking.Transport;
    using UnityEngine;

    /// <summary>
    /// 提供 UI 向 Server World 发起监听请求的入口
    /// </summary>
    public static class ServerNetCodeController
    {
        /// <summary>
        /// 在 Server World 中创建唯一的端口监听请求
        /// </summary>
        /// <param name="port">服务器监听端口</param>
        public static void StartListen(ushort port)
        {
            var serverWorld = NetworkWorldLocator.FindServerWorld();
            if (serverWorld == null)
            {
                Debug.LogError("[Server] 未找到 Server World，无法开始监听。");
                return;
            }

            var entityManager = serverWorld.EntityManager;

            // 监听请求必须唯一，重复 Entity 会让 NetCode 重复绑定同一端口
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
}
