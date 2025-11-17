using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using Unity.Collections;

public static class HostStartGameHelper
{
    // 从本地 ClientWorld 发 StartGameRpc 给 Server
    public static void SendStartGameRpc(string sceneName)
    {
        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null)
        {
            Debug.LogWarning("[HostStartGameHelper] No client world, cannot send StartGameRpc.");
            return;
        }

        var entityManager = clientWorld.EntityManager;

        var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
        if (query.IsEmpty)
        {
            Debug.LogWarning("[HostStartGameHelper] No NetworkId in client world, not connected yet.");
            query.Dispose();
            return;
        }

        var connectionEntity = query.GetSingletonEntity();
        query.Dispose();

        var rpcEntity = entityManager.CreateEntity();
        entityManager.AddComponentData(rpcEntity, new StartGameRpc
        {
            SceneName = new FixedString64Bytes(sceneName)
        });
        entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connectionEntity
        });

        Debug.Log($"[HostStartGameHelper] StartGameRpc sent for scene '{sceneName}'.");
    }
}
