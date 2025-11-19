using System.Xml.Schema;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

public static class GameResourceGetter
{
    public static bool TryGetLocalPlayerResourceState(out PlayerResourceState result)
    {
        result = default;

        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null)
            return false;

        var entityManager = clientWorld.EntityManager;

        if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
        {
            var localNetId = entityManager.CreateEntityQuery(typeof(NetworkId)).GetSingleton<NetworkId>().Value;

            var query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PlayerResourceState>(),
                ComponentType.ReadOnly<PlayerResourceTag>(),
                ComponentType.ReadOnly<GhostOwner>());

            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var owners   = query.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);
            var states   = query.ToComponentDataArray<PlayerResourceState>(Unity.Collections.Allocator.Temp);

            bool found = false;
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId == localNetId)
                {
                    result = states[i];
                    found = true;
                    break;
                }
            }

            entities.Dispose();
            owners.Dispose();
            states.Dispose();
            query.Dispose();

            return found;
        }

        return false;
    }

    public static bool TryGlobalGameResourceState(out GlobalGameResourceState state)
    {
        state = default;

        var serverWorld = WorldManager.FindServerWorld();
        if (serverWorld == null)
            return false;

        var entityManager = serverWorld.EntityManager;

        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<GlobalGameResourceState>(),
            ComponentType.ReadOnly<GlobalGameResourceTag>());

        if (query.IsEmpty)
        {
            query.Dispose();
            return false;
        }

        state = query.GetSingleton<GlobalGameResourceState>();
        query.Dispose();
        return true;
    }

}