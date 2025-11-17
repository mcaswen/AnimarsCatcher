using Unity.Entities;
using Unity.NetCode;
using Unity.Collections;
using UnityEngine;

public static class ClientLobbyIntroSender
{
    public static void SendIntro(World clientWorld, string playerName)
    {
        var entityManager = clientWorld.EntityManager;

        // 确认已有 NetworkId
        var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
        if (query.IsEmpty)
        {
            Debug.LogWarning("[ClientLobbyIntroSender] No NetworkId yet, cannot send intro.");
            query.Dispose();
            return;
        }

        var connectionEntity = query.GetSingletonEntity();
        query.Dispose();

        var rpcEntity = entityManager.CreateEntity();
        entityManager.AddComponentData(rpcEntity, new ClientLobbyIntroRpc
        {
            PlayerName = new FixedString64Bytes(playerName)
        });
        entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
        {
            TargetConnection = connectionEntity
        });

        Debug.Log($"[ClientLobbyIntroSender] Sent lobby intro as '{playerName}'.");
    }
}
