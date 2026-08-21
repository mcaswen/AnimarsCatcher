using System.Xml.Schema;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using AnimarsCatcher.Gameplay;
using AnimarsCatcher.Networking;

namespace AnimarsCatcher.Presentation.Resource
{
    /// <summary>
    /// 从 NetCode 世界读取玩家资源和全局资源快照
    /// 所有查询均为即时只读，查询失败时返回 false
    /// </summary>
    public static class ResourceStateReader
    {
        /// <summary>
        /// 查找 GhostOwner 与本地 NetworkId 匹配的玩家资源状态
        /// </summary>
        /// <param name="result">找到的本地玩家资源快照</param>
        /// <returns>找到匹配 Entity 时返回 true</returns>
        public static bool TryGetLocalPlayerResourceState(out PlayerResourceState result)
        {
            result = default;

            var clientWorld = NetworkWorldLocator.FindClientWorld();
            if (clientWorld == null)
                return false;

            var entityManager = clientWorld.EntityManager;

            if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
            {
                var localNetworkId = entityManager.CreateEntityQuery(typeof(NetworkId)).GetSingleton<NetworkId>().Value;

                var query = entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<PlayerResourceState>(),
                    ComponentType.ReadOnly<PlayerResourceTag>(),
                    ComponentType.ReadOnly<GhostOwner>());

                var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                var owners   = query.ToComponentDataArray<GhostOwner>(Unity.Collections.Allocator.Temp);
                var states   = query.ToComponentDataArray<PlayerResourceState>(Unity.Collections.Allocator.Temp);

                // 通过 GhostOwner 过滤其他连接同步到本机的玩家资源 Entity
                bool found = false;
                for (int i = 0; i < entities.Length; i++)
                {
                    if (owners[i].NetworkId == localNetworkId)
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

        /// <summary>
        /// 从服务端世界读取唯一的全局资源状态
        /// </summary>
        /// <param name="state">全局资源快照</param>
        /// <returns>服务端世界和单例均存在时返回 true</returns>
        public static bool TryGetGlobalGameResourceState(out GlobalGameResourceState state)
        {
            state = default;

            var serverWorld = NetworkWorldLocator.FindServerWorld();
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
}
