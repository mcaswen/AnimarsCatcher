using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using Unity.Collections;

/// <summary>
/// 提供 Host UI 经本地 Client World 请求服务器开局的入口
/// </summary>
public static class HostStartGameHelper
{
    /// <summary>
    /// 向服务器发送包含目标场景的开局 RPC
    /// </summary>
    /// <param name="sceneName">服务器要求所有客户端加载的场景名</param>
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
