using System;
using Unity.Entities;

namespace AnimarsCatcher.Gameplay.Contracts
{
    /// <summary>
    /// 标识当前 World 唯一允许运行的 Ani 移动后端
    /// </summary>
    public enum AniMovementBackend : byte
    {
        ClearanceGrid,
        LegacyNavMesh
    }

    /// <summary>
    /// 保存当前 World 已解析的 Ani 移动后端
    /// </summary>
    public struct AniMovementBackendConfig : IComponentData
    {
        public AniMovementBackend Value;
    }

    /// <summary>
    /// 允许 Clearance Grid 后端运行
    /// </summary>
    public struct GridMovementBackendEnabled : IComponentData
    {
    }

    /// <summary>
    /// 允许 Legacy NavMesh 后端运行
    /// </summary>
    public struct LegacyNavMeshBackendEnabled : IComponentData
    {
    }

    /// <summary>
    /// 标记当前 World 正在执行导航性能基准
    /// </summary>
    public struct NavigationBenchmarkEnabled : IComponentData
    {
    }

    /// <summary>
    /// 以单一配置实体初始化或切换 World 的移动后端
    /// </summary>
    public static class AniMovementBackendWorldUtility
    {
        /// <summary>
        /// 配置目标 World，并确保配置实体上只存在一个后端 Tag
        /// </summary>
        /// <param name="world">需要配置的 World</param>
        /// <param name="backend">唯一允许运行的移动后端</param>
        /// <returns>保存配置和后端 Tag 的实体</returns>
        public static Entity ConfigureWorld(World world, AniMovementBackend backend)
        {
            if (world == null || !world.IsCreated)
            {
                throw new ArgumentException("目标 World 必须已经创建", nameof(world));
            }

            EntityManager entityManager = world.EntityManager;
            using EntityQuery configQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadWrite<AniMovementBackendConfig>());
            int configCount = configQuery.CalculateEntityCount();

            if (configCount > 1)
            {
                throw new InvalidOperationException(
                    $"World {world.Name} 中存在多个 AniMovementBackendConfig");
            }

            Entity configEntity = configCount == 0
                ? entityManager.CreateEntity(typeof(AniMovementBackendConfig))
                : configQuery.GetSingletonEntity();

            entityManager.SetComponentData(configEntity, new AniMovementBackendConfig
            {
                Value = backend
            });

            SetExclusiveTag<GridMovementBackendEnabled>(
                entityManager,
                configEntity,
                backend == AniMovementBackend.ClearanceGrid);
            SetExclusiveTag<LegacyNavMeshBackendEnabled>(
                entityManager,
                configEntity,
                backend == AniMovementBackend.LegacyNavMesh);

            return configEntity;
        }

        /// <summary>
        /// 验证目标 World 的配置单例与后端 Tag 是否严格一致
        /// </summary>
        /// <param name="world">需要验证的 World</param>
        /// <param name="reason">验证失败时的具体原因</param>
        /// <returns>配置和后端 Tag 是否满足互斥契约</returns>
        public static bool TryValidateWorld(World world, out string reason)
        {
            EntityManager entityManager = world.EntityManager;
            using EntityQuery configQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AniMovementBackendConfig>());
            using EntityQuery gridTagQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<GridMovementBackendEnabled>());
            using EntityQuery legacyTagQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<LegacyNavMeshBackendEnabled>());

            int configCount = configQuery.CalculateEntityCount();
            int gridTagCount = gridTagQuery.CalculateEntityCount();
            int legacyTagCount = legacyTagQuery.CalculateEntityCount();
            if (configCount != 1)
            {
                reason = $"必须且只能存在一个 AniMovementBackendConfig，当前数量为 {configCount}";
                return false;
            }

            if (gridTagCount > 0 && legacyTagCount > 0)
            {
                reason = "Grid 与 Legacy 移动后端 Tag 同时存在";
                return false;
            }

            AniMovementBackend backend = configQuery.GetSingleton<AniMovementBackendConfig>().Value;
            bool valid = backend switch
            {
                AniMovementBackend.ClearanceGrid => gridTagCount == 1 && legacyTagCount == 0,
                AniMovementBackend.LegacyNavMesh => legacyTagCount == 1 && gridTagCount == 0,
                _ => false
            };
            reason = valid
                ? string.Empty
                : $"配置后端 {backend} 与启用 Tag 不一致：Grid={gridTagCount}，Legacy={legacyTagCount}";
            return valid;
        }

        private static void SetExclusiveTag<T>(
            EntityManager entityManager,
            Entity configEntity,
            bool shouldExist)
            where T : unmanaged, IComponentData
        {
            bool exists = entityManager.HasComponent<T>(configEntity);
            if (shouldExist == exists)
            {
                return;
            }

            if (shouldExist)
            {
                entityManager.AddComponent<T>(configEntity);
            }
            else
            {
                entityManager.RemoveComponent<T>(configEntity);
            }
        }
    }
}
