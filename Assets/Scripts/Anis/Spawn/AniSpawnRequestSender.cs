using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

public static class AniSpawnRequestSender
{
    public static void RequestSpawnAnis(int blasterAniSpawnCount, int pickerAniSpawnCount)
    {
        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null)
        {
            Debug.LogWarning("[AniSpawnRequestSender] No client world, cannot send spawn request.");
            return;
        }

        var entityManager = clientWorld.EntityManager;

        using (var query = entityManager.CreateEntityQuery(typeof(NetworkId)))
        {
            if (query.IsEmpty)
            {
                Debug.LogWarning("[AniSpawnRequestSender] No NetworkId, client not connected.");
                return;
            }

            var connection = query.GetSingletonEntity();

            var rpcEntity = entityManager.CreateEntity();
            entityManager.AddComponentData(rpcEntity, new SpawnAniRpc
            {
                BlasterAniSpawnCount = blasterAniSpawnCount,
                PickerAniSpawnCount = pickerAniSpawnCount
            }
            );
            entityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });

            Debug.Log("[AniSpawnRequestSender] SpawnBlasterAniRpc sent.");
        }
    }
}

