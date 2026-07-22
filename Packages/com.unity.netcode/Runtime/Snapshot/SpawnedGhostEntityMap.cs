using Unity.Entities;
using Unity.Collections;

namespace Unity.NetCode
{
    internal struct GhostUpdateVersion : IComponentData
    {
        public uint LastSystemVersion;
    }

    /// <summary>
    /// 用于存储所有已生成 Ghost 的 Entity 引用的 Singleton Component
    /// </summary>
    public struct SpawnedGhostEntityMap : IComponentData
    {
        /// <summary>
        /// 在 Ghost 生成或 Despawn 时由 <see cref="GhostReceiveSystem"/> 和 <see cref="GhostSendSystem"/> 更新
        /// 可根据 Ghost 的 <see cref="SpawnedGhost"/> 标识获取已生成 Ghost 的 Entity 引用
        /// </summary>
        public NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly Value;
        internal NativeParallelHashMap<SpawnedGhost, Entity> SpawnedGhostMapRW;

        // 服务器数据
        internal NativeList<int> ServerDestroyedPrespawns;
        internal NativeArray<int> m_ServerAllocatedGhostIds;
        internal NativeQueue<int> m_ServerFreeGhostIds;

        internal void SetServerAllocatedPrespawnGhostId(int prespawnCount)
        {
            m_ServerAllocatedGhostIds[1] = prespawnCount;
        }

        // 客户端数据
        internal NativeParallelHashMap<int, Entity> ClientGhostEntityMap;

        internal void AddClientNonSpawnedGhosts(NativeArray<NonSpawnedGhostMapping> ghosts, NetDebug netDebug)
        {
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghostId = ghosts[i].ghostId;
                var ent = ghosts[i].entity;
                if (!ClientGhostEntityMap.TryAdd(ghostId, ent))
                {
                    netDebug.LogError($"Ghost ID {ghostId} has already been added");
                    ClientGhostEntityMap[ghostId] = ent;
                }
            }
        }

        internal void AddClientSpawnedGhosts(NativeArray<SpawnedGhostMapping> ghosts, NetDebug netDebug)
        {
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghost = ghosts[i].ghost;
                var ent = ghosts[i].entity;
                if (!ClientGhostEntityMap.TryAdd(ghost.ghostId, ent))
                {
                    netDebug.LogError($"Ghost ID {ghost.ghostId} has already been added");
                    ClientGhostEntityMap[ghost.ghostId] = ent;
                }

                if (!SpawnedGhostMapRW.TryAdd(ghost, ent))
                {
                    netDebug.LogError($"Ghost ID {ghost.ghostId} has already been added to the spawned ghost map");
                    SpawnedGhostMapRW[ghost] = ent;
                }
            }
        }
        internal void UpdateClientSpawnedGhosts(NativeArray<SpawnedGhostMapping> ghosts, NetDebug netDebug)
        {
            for (int i = 0; i < ghosts.Length; ++i)
            {
                var ghost = ghosts[i].ghost;
                var ent = ghosts[i].entity;
                var prevEnt = ghosts[i].previousEntity;
                // 如果 Ghost 同时位于 Despawn 队列中，它不会出现在 Ghost Map 内
                // 如果某个 GhostId 先前用于插值 Ghost，后来改用于预测 Ghost
                // Ghost Map 内可能存在另一个使用该 ID 的 Ghost
                if (ClientGhostEntityMap.TryGetValue(ghost.ghostId, out var existing) && existing == prevEnt)
                {
                    ClientGhostEntityMap[ghost.ghostId] =  ent;
                }
                if (!SpawnedGhostMapRW.TryAdd(ghost, ent))
                {
                    netDebug.LogError($"Ghost ID {ghost.ghostId} has already been added to the spawned ghost map");
                    SpawnedGhostMapRW[ghost] = ent;
                }
            }
        }
    }
}
