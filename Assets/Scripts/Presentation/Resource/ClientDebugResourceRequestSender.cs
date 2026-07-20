using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Networking;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace AnimarsCatcher.Presentation.Resource
{
    /// <summary>
    /// 在客户端世界创建资源变更 RPC 实体
    /// </summary>
    public static class ClientDebugResourceRequestSender
    {
        /// <summary>
        /// 请求服务端为本地连接调整指定资源
        /// </summary>
        /// <param name="kind">资源类型</param>
        /// <param name="amount">资源变化量</param>
        public static void RequestAdjustment(ResourceItemKind kind, int amount)
        {
            var clientWorld = NetworkWorldLocator.FindClientWorld();
            if (clientWorld == null)
            {
                Debug.LogWarning("[ClientDebugResourceRequestSender] No client world, cannot send RPC");
                return;
            }

            var entityManager = clientWorld.EntityManager;

            using (var query = entityManager.CreateEntityQuery(typeof(NetworkId)))
            {
                if (query.IsEmpty)
                {
                    Debug.LogWarning("[ClientDebugResourceRequestSender] No NetworkId, client not connected");
                    return;
                }

                var connection = query.GetSingletonEntity();

                // RPC 实体和目标连接必须创建在同一个客户端世界
                var rpcEntity = entityManager.CreateEntity();
                entityManager.AddComponentData(rpcEntity, new DebugAdjustResourceRpc
                {
                    Kind = kind,
                    Amount = amount
                });
                entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connection
                });

                Debug.Log($"[ClientDebugResourceRequestSender] Sent DebugAdjustResourceRpc: {kind} {amount:+#;-#;0}");
            }
        }
    }
}
