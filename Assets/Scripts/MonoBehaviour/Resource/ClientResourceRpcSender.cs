using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.Global
{
    /// <summary>
    /// 在客户端世界创建资源变更 RPC 实体
    /// </summary>
    public static class ClientResourceRpcSender
    {
        /// <summary>
        /// 请求服务端为本地连接调整指定资源
        /// </summary>
        /// <param name="type">资源类型</param>
        /// <param name="amount">资源变化量</param>
        public static void RequestAddResource(ResourceType type, int amount)
        {
            var clientWorld = WorldManager.FindClientWorld();
            if (clientWorld == null)
            {
                Debug.LogWarning("[DebugResourceRpcSender] No client world, cannot send rpc.");
                return;
            }

            var entityManager = clientWorld.EntityManager;

            using (var query = entityManager.CreateEntityQuery(typeof(NetworkId)))
            {
                if (query.IsEmpty)
                {
                    Debug.LogWarning("[DebugResourceRpcSender] No NetworkId, client not connected.");
                    return;
                }

                var connection = query.GetSingletonEntity();

                // RPC 实体和目标连接必须创建在同一个客户端世界
                var rpcEntity = entityManager.CreateEntity();
                entityManager.AddComponentData(rpcEntity, new ResourceChangedRpc
                {
                    Type   = type,
                    Amount = amount
                });
                entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
                {
                    TargetConnection = connection
                });

                Debug.Log($"[DebugResourceRpcSender] Sent DebugAddResourceRpc: {type} +{amount}");
            }
        }
    }
}
