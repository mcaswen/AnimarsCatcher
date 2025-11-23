using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(GhostCollectionSystem))]
public partial struct GhostCollectionDebugSystem : ISystem
{
    private bool _printed;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GhostCollection>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (_printed)
            return;

        _printed = true;

        var entityManager = state.EntityManager;
        Entity ghostCollectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
        DynamicBuffer<GhostCollectionPrefab> prefabs =
            SystemAPI.GetBuffer<GhostCollectionPrefab>(ghostCollectionEntity);

        Debug.Log($"[GhostDebug] Prefab count = {prefabs.Length}");

        for (int i = 0; i < prefabs.Length; i++)
        {
            var entry        = prefabs[i];
            var prefabEntity = entry.GhostPrefab; // 或 entry.Prefab，按你 NetCode 版本

            bool exists = entityManager.Exists(prefabEntity);
            string name = exists ? entityManager.GetName(prefabEntity) : "<MISSING>";

            Debug.Log($"[GhostDebug] [{i}] entity={prefabEntity}, exists={exists}, name={name}");
        }
    }
}
