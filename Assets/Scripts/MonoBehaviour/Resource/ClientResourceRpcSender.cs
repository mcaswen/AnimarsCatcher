using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.Global
{
    public static class ClientResourceRpcSender
    {
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
