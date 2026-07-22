using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// 在烘焙过程中添加的组件，用于表示此预生成 Ghost 已完成烘焙
    /// </summary>
    [BakingType]
    internal struct PrespawnedGhostBakedBefore: IComponentData { }

    /// <summary>
    /// 对 SubScene 中所有具有 GhostAuthoringComponent 的 GameObject 执行后处理，
    /// 向其主 Entity 添加以下组件：
    /// - PreSpawnedGhostIndex 组件：包含每个 SubScene 内唯一且保证确定性的标识符
    /// - SubSceneGhostComponentHash 共享组件：用于以确定性方式对 Ghost 实例分组
    /// </summary>
    ///
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    [UpdateAfter(typeof(GhostAuthoringBakingSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [BakingVersion("cmarastoni", 1)]
    partial class PreSpawnedGhostsBakingSystem : SystemBase
    {
        private EntityQuery m_SceneSectionEntityQuery;

        protected override void OnDestroy()
        {
            if (EntityManager.IsQueryValid(m_SceneSectionEntityQuery))
                m_SceneSectionEntityQuery.Dispose();
        }

        protected override void OnUpdate()
        {
            var hashToEntity = new NativeParallelHashMap<ulong, Entity>(128, Allocator.TempJob);

            var ghostAuthoringComponentQuery =
                SystemAPI.QueryBuilder().WithAllRW<GhostAuthoringComponentBakingData>().Build();
            var ghostAuthoringComponentEntities = ghostAuthoringComponentQuery.ToEntityArray(Allocator.Temp);
            var ghostAuthoringComponentBakingDatas =
                ghostAuthoringComponentQuery.ToComponentDataArray<GhostAuthoringComponentBakingData>(Allocator.Temp);

            // TODO：检查 GhostAuthoringComponent 是否使用插值，因为目前不支持预测模式
            for (var i = 0; i < ghostAuthoringComponentEntities.Length; i++)
            {
                var entity = ghostAuthoringComponentEntities[i];
                var ghostAuthoringBakingData = ghostAuthoringComponentBakingDatas[i];

                var isInSubscene = EntityManager.HasComponent<SceneSection>(entity);
                bool isPrefab = ghostAuthoringBakingData.IsPrefab;
                var activeInScene = ghostAuthoringBakingData.IsActive;
                if (!isPrefab && isInSubscene && activeInScene)
                {
                    var hashData = new NativeList<ulong>(Allocator.Temp);
                    // 使用 Ghost 类型识别 Ghost Archetype，这是服务器和客户端之间唯一可靠的值
                    // 烘焙会根据转换目标在 Entity 上添加或移除组件，
                    // 因此 archetype.StableHash 不适用于此处
                    hashData.Add(ghostAuthoringBakingData.GhostType.guid0);
                    hashData.Add(ghostAuthoringBakingData.GhostType.guid1);
                    hashData.Add(ghostAuthoringBakingData.GhostType.guid2);
                    hashData.Add(ghostAuthoringBakingData.GhostType.guid3);

                    // 如果 Entity 的创作方式导致不存在位置和旋转数据，需要如何处理
                    // 这里改为依赖 TransformAuthoring，以获得只取决于 GameObject 创作数据的稳定结果
                    var transformAuthoring = EntityManager.GetComponentData<TransformAuthoring>(entity);

                    unsafe
                    {
                        var positionData = (byte*)&transformAuthoring.Position;
                        var rotationData = (byte*)&transformAuthoring.Rotation;
                        hashData.Add(Unity.Core.XXHash.Hash64(positionData, 3*sizeof(float)));
                        hashData.Add(Unity.Core.XXHash.Hash64(rotationData, 4*sizeof(float)));
                    }
                    // 可以在这里加入更多组件以获得更好的哈希结果，并支持位置和旋转完全相同的情况
                    // 但必须谨慎，只能包含通常保证存在于 Entity 上，且客户端和服务器都具备的组件
                    // 当前只采用位置和旋转，是最稳妥的方案

                    // 最后添加场景 GUID，使场景哈希也以已烘焙的场景 Section 为种子
                    var sceneSection = EntityManager.GetSharedComponent<SceneSection>(entity);
                    hashData.Add(sceneSection.SceneGUID.Value[0]);
                    hashData.Add(sceneSection.SceneGUID.Value[1]);
                    hashData.Add(sceneSection.SceneGUID.Value[2]);
                    hashData.Add(sceneSection.SceneGUID.Value[3]);
                    ulong combinedComponentHash;
                    unsafe
                    {
                        combinedComponentHash = Unity.Core.XXHash.Hash64((byte*) hashData.GetUnsafeReadOnlyPtr(),
                            hashData.Length * sizeof(ulong));
                    }

                    // 复制场景对象时，新对象与原对象的位置和旋转相同
                    // 在移动到独立位置前，它们会一直产生重复哈希
                    if (!hashToEntity.ContainsKey(combinedComponentHash))
                        hashToEntity.Add(combinedComponentHash, entity);
                    else
                        Debug.LogError($"Two ghosts can't be in the same exact position and rotation {EntityManager.GetName(entity)}");
                }
            }

            if (hashToEntity.Count() > 0)
            {
                // 批量添加组件
                var values = hashToEntity.GetValueArray(Allocator.Temp);
                EntityManager.AddComponent(values, typeof(PreSpawnedGhostIndex));
                EntityManager.AddComponent(values, typeof(PrespawnGhostBaseline));
                EntityManager.AddComponent(values, typeof(PrespawnedGhostBakedBefore));

                var keys = hashToEntity.GetKeyArray(Allocator.Temp);
                keys.Sort();

                // 按组件数据哈希排序，为预生成 Entity 分配 Ghost ID
                for (int i = 0; i < keys.Length; ++i)
                {
                    EntityManager.SetComponentData(hashToEntity[keys[i]], new PreSpawnedGhostIndex {Value = i});
                    // 需要预先把 ghostType 设为 -1，才能在后续处理前将该 Ghost 正确识别为预生成 Ghost
                    EntityManager.SetComponentData(hashToEntity[keys[i]], new GhostInstance
                    {
                        ghostId = 0,
                        // GhostType -1 是预生成 Ghost 的特殊值
                        // 确定 Ghost ID 后，发送和接收系统会把它转换为正确的 Ghost ID
                        ghostType = -1,
                        spawnTick = NetworkTick.Invalid
                    });

                    // 禁用 Entity，避免在预生成 Baseline 计算完成前获取该 Ghost
                    EntityManager.AddComponent<Disabled>(hashToEntity[keys[i]]);
                }

                // 保存包含所有预生成 Ghost 的最终 SubScene 哈希
                ulong hash;
                unsafe
                {
                    hash = Unity.Core.XXHash.Hash64((byte*) keys.GetUnsafeReadOnlyPtr(),
                        keys.Length * sizeof(ulong));
                }

                for (int i = 0; i < keys.Length; ++i)
                {
                    // 跟踪作为此 Entity 父级的 SubScene
                    EntityManager.AddSharedComponent(hashToEntity[keys[i]], new SubSceneGhostComponentHash {Value = hash});
                }


                // 向场景 Entity 添加 SubSceneWithPrespawnGhosts
                // FIXME：当前限制是假定所有预生成 Entity 都属于同一个 Section
                var sectionEntity = GetSceneSectionEntity(hashToEntity[keys[0]]);
                if (sectionEntity != Entity.Null)
                {
                    EntityManager.AddComponentData(sectionEntity, new SubSceneWithPrespawnGhosts
                    {
                        SubSceneHash = hash,
                        PrespawnCount = keys.Length
                    });
                    EntityManager.AddComponent<PrespawnedGhostBakedBefore>(sectionEntity);
                }
                // 这里还可以添加更多处理，理想情况下包括序列化，一种方式是使用某种偏移重映射
            }

            hashToEntity.Dispose();
        }

        public Entity GetSceneSectionEntity(Entity entity)
        {
            var sceneSection = EntityManager.GetSharedComponent<SceneSection>(entity);
            return SerializeUtility.GetSceneSectionEntity(sceneSection.Section, EntityManager, ref m_SceneSectionEntityQuery);
        }
    }

    /// <summary>
    /// 清理 PreSpawnedGhostsBakingSystem 在之前烘焙处理中添加的全部组件
    /// </summary>
    ///
    [UpdateInGroup(typeof(PreBakingSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [BakingVersion("cmarastoni", 1)]
    partial class PreSpawnedGhostsCleanupBaking : SystemBase
    {
        private EntityQuery m_PreviouslyBakedEntities;

        public static ComponentTypeSet PreSpawnedGhostsComponents = new ComponentTypeSet(new ComponentType[]
        {
            typeof(SubSceneGhostComponentHash),
            // 禁用 Entity，避免在预生成 Baseline 计算完成前获取该 Ghost
            typeof(Disabled),
            typeof(PreSpawnedGhostIndex),
            typeof(PrespawnGhostBaseline),
            typeof(SubSceneWithPrespawnGhosts),
            typeof(PrespawnedGhostBakedBefore)
        });

        protected override void OnCreate()
        {
            base.OnCreate();

            // 查询之前已经烘焙的所有子 Entity
            m_PreviouslyBakedEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrespawnedGhostBakedBefore>()
                },
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
            });
        }

        protected void RevertPreviousBakings()
        {
            EntityManager.RemoveComponent(m_PreviouslyBakedEntities, PreSpawnedGhostsComponents);
        }

        protected override void OnUpdate()
        {
            // 从不在 hashToEntity 中的 Entity 上移除 Baker 添加的组件
            RevertPreviousBakings();
        }
    }
}
