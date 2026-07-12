using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

/// <summary>
/// 在客户端首次就绪时输出 Ghost 预制体集合以辅助网络配置排查
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(GhostCollectionSystem))]
public partial struct GhostCollectionDebugSystem : ISystem
{
    private bool _printed;

    /// <summary>
    /// 等待 NetCode 完成 Ghost 集合初始化
    /// </summary>
    /// <param name="state">系统运行状态</param>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GhostCollection>();
    }

    /// <summary>
    /// 仅输出一次 Ghost 预制体实体及其可用状态
    /// </summary>
    /// <param name="state">系统运行状态</param>
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
            var prefabEntity = entry.GhostPrefab; // 读取 Ghost 集合登记的预制体实体

            bool exists = entityManager.Exists(prefabEntity);
            string name = exists ? entityManager.GetName(prefabEntity) : "<MISSING>";

            Debug.Log($"[GhostDebug] [{i}] entity={prefabEntity}, exists={exists}, name={name}");
        }
    }
}
