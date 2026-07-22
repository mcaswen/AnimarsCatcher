using System;
using Unity.Entities;
using UnityEngine;
using Unity.Assertions;
using Unity.Collections;
using Unity.Jobs;
using Unity.NetCode.Hybrid;

namespace Unity.NetCode
{
    // 此结构体与 GhostPrefabConfig 对应，但不包含任何托管类型，因此可以在普通组件中使用
    struct GhostPrefabConfigBaking
    {
        public UnityObjectRef<GhostAuthoringComponent> Authoring;
        public GhostPrefabCreation.Config Config;
    }

    // 此类型包含 Baker 从 Authoring 组件中提取的全部信息
    [BakingType]
    struct GhostAuthoringComponentBakingData : IComponentData
    {
        public GhostPrefabConfigBaking BakingConfig;
        public GhostType GhostType;
        public NetcodeConversionTarget Target;
        public bool IsPrefab;
        public bool IsActive;
        public FixedString64Bytes GhostName;
        public ulong GhostNameHash;
    }

    // 此类型用于保存覆盖设置
    [BakingType]
    struct GhostAuthoringComponentOverridesBaking : IBufferElementData
    {
        // 为便于序列化，这里使用类型全名，因为不能依赖组件的 TypeIndex
        // 也不能使用 StableTypeHash，因为布局或字段变化同样会影响该哈希值，因此它不适合此用途
        public ulong FullTypeNameID;
        // GameObject 引用，可以是根对象或子对象
        public int GameObjectID;
        // Entity GUID 引用
        public ulong EntityGuid;
        // 覆盖该类型可用的模式，如果为 0，则从 Prefab 或 Entity 实例中移除组件
        public int PrefabType;
        // 覆盖组件要发送到哪些客户端
        public int SendTypeOptimization;
        // 选择要使用的变体，0 表示默认变体
        public ulong ComponentVariant;
    }

    [BakingVersion("cmarastoni", 2)]
    class GhostAuthoringComponentBaker : Baker<GhostAuthoringComponent>
    {
        public override void Bake(GhostAuthoringComponent ghostAuthoring)
        {
            var ghostName = ghostAuthoring.GetAndValidateGhostName(out var ghostNameHash);

            // Prefab 依赖
            bool isPrefab = !ghostAuthoring.gameObject.scene.IsValid() || ghostAuthoring.ForcePrefabConversion;
#if UNITY_EDITOR
            if (!isPrefab)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(ghostAuthoring.prefabId);
                GameObject prefab = null;
                if (!String.IsNullOrEmpty(path))
                    prefab = (GameObject) UnityEditor.AssetDatabase.LoadAssetAtPath(path, typeof(GameObject));
                // 此处使用 GetEntity 告知 Baker 还需要烘焙该 Prefab
                // 这替代了转换回调 DeclareReferencedPrefabs
                GetEntity(prefab, TransformUsageFlags.Dynamic);
            }
#endif

            // 运行时转换存在一些问题，某些情况下不能在这里使用 PrefabStage 检查或类似方式
            var target = this.GetNetcodeTarget(isPrefab);
            // 开始处理前检查 Ghost 是否有效
            if (String.IsNullOrEmpty(ghostAuthoring.prefabId))
                throw new InvalidOperationException($"The ghost {ghostName} is not a valid prefab, all ghosts must be the top-level GameObject in a prefab. Ghost instances in scenes must be instances of such prefabs and changes should be made on the prefab asset, not the prefab instance");

            if (!isPrefab && ghostAuthoring.DefaultGhostMode == GhostMode.OwnerPredicted && target != NetcodeConversionTarget.Server)
                throw new InvalidOperationException($"Cannot convert a owner predicted ghost {ghostName} as a scene instance");

            if (!isPrefab && IsClient())
                throw new InvalidOperationException($"The ghost {ghostName} cannot be created on the client, either put it in a sub-scene or spawn it on the server only");

            if (ghostAuthoring.prefabId.Length != 32)
                throw new InvalidOperationException("Invalid guid for ghost prefab type");

            // 根据设置添加需要序列化的组件
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            if (ghostAuthoring.HasOwner)
            {
                AddComponent(entity, default(GhostOwner));
                AddComponent(entity, default(GhostOwnerIsLocal));
            }
            if (ghostAuthoring.SupportAutoCommandTarget && ghostAuthoring.HasOwner)
                AddComponent(entity, new AutoCommandTarget {Enabled = true});
            if (ghostAuthoring.TrackInterpolationDelay && ghostAuthoring.HasOwner)
                AddComponent(entity, default(CommandDataInterpolationDelay));
            if (ghostAuthoring.GhostGroup)
                AddBuffer<GhostGroup>(entity);

            var allComponentOverrides = GhostAuthoringInspectionComponent.CollectAllComponentOverridesInInspectionComponents(ghostAuthoring, true);

            var overrideBuffer = AddBuffer<GhostAuthoringComponentOverridesBaking>(entity);
            foreach (var componentOverride in allComponentOverrides)
            {
                overrideBuffer.Add(new GhostAuthoringComponentOverridesBaking
                {
                    FullTypeNameID = TypeManager.CalculateFullNameHash(componentOverride.Item2.FullTypeName),
                    GameObjectID = componentOverride.Item1.GetInstanceID(),
                    EntityGuid = componentOverride.Item2.EntityIndex,
                    PrefabType = (int) componentOverride.Item2.PrefabType,
                    SendTypeOptimization = (int) componentOverride.Item2.SendTypeOptimization,
                    ComponentVariant = componentOverride.Item2.VariantHash
                });
            }

            var bakingConfig = new GhostPrefabConfigBaking
            {
                Authoring = ghostAuthoring,
                Config = ghostAuthoring.AsConfig(ghostName),
            };

            // 生成 Ghost 类型组件，以便通过匹配 Prefab Asset GUID 识别 Ghost
            var ghostType = GhostType.FromHash128String(ghostAuthoring.prefabId);
            var activeInScene = IsActive();

            AddComponent(entity, new GhostAuthoringComponentBakingData
            {
                GhostName = ghostName,
                GhostNameHash = ghostNameHash,
                BakingConfig = bakingConfig,
                GhostType = ghostType,
                Target = target,
                IsPrefab = isPrefab,
                IsActive = activeInScene
            });

            if (isPrefab)
            {
                AddComponent<GhostPrefabMetaData>(entity);
                if (target == NetcodeConversionTarget.ClientAndServer)
                    // 标记此 Prefab 需要在运行时裁剪
                    AddComponent<GhostPrefabRuntimeStrip>(entity);
            }

            if (isPrefab && (target != NetcodeConversionTarget.Server) &&
                (bakingConfig.Config.SupportedGhostModes != GhostModeMask.Interpolated))
            {
                AddComponent<PredictedGhostSpawnRequest>(entity);
                // 初始设为禁用，以获得更好的查询支持
                SetComponentEnabled<PredictedGhostSpawnRequest>(entity, false);
            }

        }
    }

    // 此类型用于标记 Ghost 子实体和附加实体
    [BakingType]
    struct GhostChildEntityBaking : IComponentData
    {
        public Entity RootEntity;
    }

    // 此类型用于标记 Ghost 根实体
    [BakingType]
    struct GhostRootEntityBaking: IComponentData { }

    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [AlwaysSynchronizeSystem]
    [BakingVersion("cmarastoni", 1)]
    partial class GhostAuthoringBakingSystem : SystemBase
    {
        EntityQuery m_NoLongerBakedRootEntities;
        EntityQueryMask m_NoLongerBakedRootEntitiesMask;
        EntityQuery m_BakedEntitiesQuery;
        EntityQuery m_GhostEntities;
        EntityQueryMask m_BakedEntityMask;

        ComponentTypeSet m_ChildRevertBakingComponents = new ComponentTypeSet(new ComponentType[]
        {
            typeof(GhostChildEntity),
            typeof(GhostChildEntityBaking)
        });

        private ComponentTypeSet m_RootRevertBakingComponents = new ComponentTypeSet(new ComponentType[]
        {
            typeof(GhostType),
            typeof(GhostTypePartition),
            typeof(GhostInstance),
            typeof(PredictedGhost),
            typeof(PreSerializedGhost),
            typeof(SnapshotData),
            typeof(SnapshotDataBuffer),
            typeof(SnapshotDynamicDataBuffer),
            typeof(GhostRootEntityBaking),
        });

        protected override void OnCreate()
        {
            // 查询之前已经烘焙的所有根实体
            m_NoLongerBakedRootEntities = GetEntityQuery(new EntityQueryDesc
            {
                None = new []
                {
                    ComponentType.ReadOnly<GhostAuthoringComponentBakingData>()
                },
                All = new []
                {
                    ComponentType.ReadOnly<GhostRootEntityBaking>()
                },
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
            });
            Assert.IsFalse(m_NoLongerBakedRootEntities.HasFilter(), "EntityQueryMask will not respect the query's active filter settings.");
            m_NoLongerBakedRootEntitiesMask = m_NoLongerBakedRootEntities.GetEntityQueryMask();

            // 查询之前已经烘焙的所有根实体
            m_GhostEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = new []
                {
                    ComponentType.ReadOnly<GhostAuthoringComponentBakingData>()
                },
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
            });


            EntityQueryDesc bakedDesc = new EntityQueryDesc()
            {
                All = new[] {ComponentType.FromTypeIndex(TypeManager.GetTypeIndex<BakedEntity>())},
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
            };

            m_BakedEntitiesQuery = GetEntityQuery(bakedDesc);
            Assert.IsFalse(m_BakedEntitiesQuery.HasFilter(), "EntityQueryMask will not respect the query's active filter settings.");
            m_BakedEntityMask = m_BakedEntitiesQuery.GetEntityQueryMask();
        }

        void RevertPreviousBakings(NativeParallelHashSet<Entity> rootsToRebake)
        {
            // 还原以前作为根实体、但现在不再是根实体的所有父实体
            EntityManager.RemoveComponent(m_NoLongerBakedRootEntities, m_RootRevertBakingComponents);

            // 如果要重新计算根实体，则还原之前添加到根实体的所有组件，确保增量烘焙结果一致
            var rootEntityQuery = SystemAPI.QueryBuilder().WithAll<GhostAuthoringComponentBakingData, GhostRootEntityBaking>()
                .WithOptions(EntityQueryOptions.IncludePrefab).Build();
            var rootEntities = rootEntityQuery.ToEntityArray(Allocator.Temp);
            foreach (var rootEntity in rootEntities)
            {
                if (rootsToRebake.Contains(rootEntity))
                    EntityManager.RemoveComponent(rootEntity, m_RootRevertBakingComponents);
            }

            // 对根实体即将重新计算或已不再作为根实体的所有子实体，
            // 还原之前添加的 GhostChildEntity，确保增量烘焙结果一致
            var childEntityQuery = SystemAPI.QueryBuilder().WithAll<GhostChildEntityBaking>()
                .WithOptions(EntityQueryOptions.IncludePrefab).Build();
            var childEntities = childEntityQuery.ToEntityArray(Allocator.Temp);
            var childBakingEntities = childEntityQuery.ToComponentDataArray<GhostChildEntityBaking>(Allocator.Temp);
            for (var i=0; i<childEntities.Length; ++i)
            {
                var childEntity = childEntities[i];
                var child = childBakingEntities[i];
                if (rootsToRebake.Contains(child.RootEntity) || m_NoLongerBakedRootEntitiesMask.MatchesIgnoreFilter(child.RootEntity))
                    EntityManager.RemoveComponent(childEntity, m_ChildRevertBakingComponents);
            }
        }

        void AddRevertBakingTags(NativeArray<Entity> entities)
        {
            if (entities.Length > 0)
            {
                EntityManager.AddComponent<GhostRootEntityBaking>(entities[0]);

                var childComponent = new GhostChildEntityBaking { RootEntity = entities[0] };
                for (int index = 1; index < entities.Length; ++index)
                {
                    EntityManager.AddComponentData<GhostChildEntityBaking>(entities[index], childComponent);
                }
            }
        }

        NativeArray<Entity> GetEntityArrayFromLinkedEntityGroup(DynamicBuffer<LinkedEntityGroup> links)
        {
            unsafe
            {
                Debug.Assert(sizeof(LinkedEntityGroup) == sizeof(Entity));
            }
            var entityDynamicBuffer = links.Reinterpret<Entity>();
            var nativeArrayAlias = entityDynamicBuffer.AsNativeArray();

            var linkedEntities = new NativeArray<Entity>(links.Length, Allocator.Temp);
            NativeArray<Entity>.Copy(nativeArrayAlias, 0, linkedEntities, 0, nativeArrayAlias.Length);
            return linkedEntities;
        }

        [WithOptions(EntityQueryOptions.IncludePrefab)]
        [WithAll(typeof(GhostAuthoringComponentBakingData))]
        partial struct AddRootsToProcessJob : IJobEntity
        {
            public EntityQueryMask BakedMask;

            [NativeDisableParallelForRestriction]
            public NativeParallelHashSet<Entity>.ParallelWriter RootsToProcessWriter;

            void Execute(Entity rootEntity, DynamicBuffer<LinkedEntityGroup> linkedEntityGroup)
            {
                foreach (var child in linkedEntityGroup)
                {
                    if (BakedMask.MatchesIgnoreFilter(child.Value))
                    {
                        RootsToProcessWriter.Add(rootEntity);
                        break;
                    }
                }
            }
        }

        protected override void OnUpdate()
        {
            var bakingSystem = World.GetExistingSystemManaged<BakingSystem>();

            int ghostCount = m_GhostEntities.CalculateEntityCount();
            NativeParallelHashSet<Entity> rootsToProcess = new NativeParallelHashSet<Entity>(ghostCount, Allocator.TempJob);
            var rootsToProcessWriter = rootsToProcess.AsParallelWriter();
            var bakedMask = m_BakedEntityMask;

            // 注意：此 Singleton Entity 总会在第一次非增量处理时被销毁
            // 因为首次导入并打开 SubScene 时，烘焙系统会清理 World 中的所有 Entity
            // 这里以延迟方式重新创建该 Entity，使所有逻辑保持预期行为
            if (!SystemAPI.TryGetSingleton<GhostComponentSerializerCollectionData>(out var serializerCollectionData))
            {
                var systemGroup = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>();
                EntityManager.CreateSingleton(systemGroup.ghostComponentSerializerCollectionDataCache);
                serializerCollectionData = systemGroup.ghostComponentSerializerCollectionDataCache;
            }

            // 此代码从所有根实体中选出自身已烘焙，或至少有一个子实体已烘焙的根实体
            // BakedEntity 组件是一种 TemporaryBakingType，会添加到本次烘焙处理中已经烘焙的每个 Entity
            // Baker 可以依赖对象的数据，但不能依赖对象的烘焙过程
            // 因此，如果子实体依赖某些数据，数据变化时子实体会重新烘焙，
            // 但由于子实体本身没有变化，根实体无法得知自己也需要重新处理
            new AddRootsToProcessJob()
            {
                BakedMask = bakedMask,
                RootsToProcessWriter = rootsToProcessWriter
            }.ScheduleParallel(new JobHandle()).Complete();

            // 还原之前添加的组件
            RevertPreviousBakings(rootsToProcess);

            var context = new BlobAssetComputationContext<int, GhostPrefabBlobMetaData>(bakingSystem.BlobAssetStore, 16, Allocator.Temp);

            var processRootsQuery = SystemAPI.QueryBuilder()
                .WithAll<GhostAuthoringComponentBakingData, LinkedEntityGroup>()
                .WithOptions(EntityQueryOptions.IncludePrefab).Build();
            var processRootsEntities = processRootsQuery.ToEntityArray(Allocator.Temp);
            var processRootsBakingData = processRootsQuery
                .ToComponentDataArray<GhostAuthoringComponentBakingData>(Allocator.Temp);

            for (var i = 0; i < processRootsEntities.Length; ++i)
            {
                var rootEntity = processRootsEntities[i];
                var ghostAuthoringBakingData = processRootsBakingData[i];

                if (!rootsToProcess.Contains(rootEntity))
                    continue;

                // 此循环效率很低，但可以执行结构变化，至少会缓存 TypeHandle
                if (!SystemAPI.GetBufferLookup<LinkedEntityGroup>().TryGetBuffer(rootEntity, out var linkedEntityGroup))
                    continue;

                ProcessRoot(linkedEntityGroup, rootEntity, serializerCollectionData, ghostAuthoringBakingData, context);
            }
            rootsToProcess.Dispose();
        }

        void ProcessRoot(DynamicBuffer<LinkedEntityGroup> linkedEntityGroup, Entity rootEntity,
            GhostComponentSerializerCollectionData serializerCollectionData,
            GhostAuthoringComponentBakingData ghostAuthoringBakingData, BlobAssetComputationContext<int, GhostPrefabBlobMetaData> context)
        {
            NativeArray<Entity> linkedEntities = GetEntityArrayFromLinkedEntityGroup(linkedEntityGroup);

            // 标记这些 Entity，以便在后续烘焙时还原
            AddRevertBakingTags(linkedEntities);

            GhostPrefabCreation.CollectAllComponents(EntityManager, linkedEntities,
                out var allComponents, out var componentCounts);

            // PrefabTypes 不属于 Ghost 元数据 Blob，但会计算并保存在此数组中，以简化后续逻辑
            // 该值取决于为此类型选择的序列化变体
            var prefabTypes = new NativeArray<GhostPrefabType>(allComponents.Length, Allocator.Temp);
            var sendMasksOverride = new NativeArray<int>(allComponents.Length, Allocator.Temp);
            var variants = new NativeArray<ulong>(allComponents.Length, Allocator.Temp);

            // 设置所有组件的 GhostType、变体、sendMask 和 sendToChild 数组
            // 后续使用这些数据标记要添加或移除的组件
            DynamicBuffer<GhostAuthoringComponentOverridesBaking> overrides = EntityManager.GetBuffer<GhostAuthoringComponentOverridesBaking>(rootEntity);
            var compIdx = 0;
            for (int k = 0; k < linkedEntities.Length; ++k)
            {
                var isChild = k != 0;
                var entityGUID = EntityManager.GetComponentData<EntityGuid>(linkedEntities[k]);
                var instanceId = entityGUID.OriginatingId;
                var numComponents = componentCounts[k];
                for (int i = 0; i < numComponents; ++i, ++compIdx)
                {
                    // 查找覆盖设置
                    GhostAuthoringComponentOverridesBaking? myOverride = default;
                    foreach (var overrideEntry in overrides)
                    {
                        ulong fullTypeNameID = TypeManager.GetFullNameHash(allComponents[compIdx].TypeIndex);
                        if (overrideEntry.FullTypeNameID == fullTypeNameID && overrideEntry.GameObjectID == instanceId)
                        {
                            myOverride = overrideEntry;
                            break;
                        }
                    }

                    // 使用通用默认值初始化，必要时再覆盖
                    prefabTypes[compIdx] = GhostPrefabType.All;
                    ulong variantHash = myOverride.HasValue ? myOverride.Value.ComponentVariant : 0;
                    bool isRoot = !isChild;
                    var variantType = serializerCollectionData.GetCurrentSerializationStrategyForComponent(allComponents[compIdx], variantHash, isRoot);
                    variants[compIdx] = variantType.Hash;
                    sendMasksOverride[compIdx] = GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;

                    // NW：调查 macOS 上的 CI 超时错误期间暂时禁用警告：[TimeoutExceptionMessage] 等待日志消息超时，超时窗口内编辑器没有输出日志
                    //if (variantType.IsTestVariant != 0)
                    //{
                    //    Debug.LogWarning($"Ghost '{ghostAuthoringBakingData.GhostName}' uses a test variant {variantType.ToFixedString()}! Ensure this is only ever used in an Editor, test context.");
                    //}

                    // 先使用通用默认值初始化，再按需覆盖
                    if (myOverride.HasValue)
                    {
                        if (myOverride.Value.ComponentVariant != 0) // 哈希值为 0 表示默认值，不属于错误
                        {
                            if (variantType.Hash != myOverride.Value.ComponentVariant)
                            {
                                Debug.LogError($"Ghost '{ghostAuthoringBakingData.GhostName}' has an override for type {allComponents[compIdx].ToFixedString()} that sets the Variant to hash '{myOverride.Value.ComponentVariant}'. However, this hash is no longer present in code-gen, likely due to a code change removing or renaming the old variant. Thus, using Variant '{variantType.DisplayName}' (with hash: '{variantType.Hash}') and ignoring your \"Component Override\". Please open this prefab and re-apply.");
                            }
                        }

                        // 仅在属性明确要求时覆盖默认值，因此应始终先检查 UseDefaultValue
                        if (myOverride.Value.PrefabType != GhostAuthoringInspectionComponent.ComponentOverride.NoOverride)
                            prefabTypes[compIdx] = (GhostPrefabType) myOverride.Value.PrefabType;
                        else
                            // 问题：如果变体特性发生变化或移除了某个变体，
                            // SubScene 和 Prefab 不会重新转换，因为它们不在 SubScene 或组件中
                            // 除非强制只在运行时裁剪，否则必须在转换时检查预期使用的变体
                            prefabTypes[compIdx] = variantType.PrefabType;
                        if (myOverride.Value.SendTypeOptimization != GhostAuthoringInspectionComponent.ComponentOverride.NoOverride)
                            sendMasksOverride[compIdx] = myOverride.Value.SendTypeOptimization;
                    }
                    else
                        prefabTypes[compIdx] = variantType.PrefabType;
                }
            }

            GhostPrefabCreation.FinalizePrefabComponents(ghostAuthoringBakingData.BakingConfig.Config, EntityManager,
                rootEntity, ghostAuthoringBakingData.GhostType, linkedEntities,
                allComponents, componentCounts, ghostAuthoringBakingData.Target, prefabTypes);

            if (ghostAuthoringBakingData.IsPrefab)
            {
                var contentHash = TypeHash.FNV1A64(ghostAuthoringBakingData.BakingConfig.Config.Importance);
                contentHash = TypeHash.CombineFNV1A64(contentHash,
                    TypeHash.FNV1A64((int) ghostAuthoringBakingData.BakingConfig.Config.SupportedGhostModes));
                contentHash = TypeHash.CombineFNV1A64(contentHash,
                    TypeHash.FNV1A64((int) ghostAuthoringBakingData.BakingConfig.Config.DefaultGhostMode));
                contentHash = TypeHash.CombineFNV1A64(contentHash,
                    TypeHash.FNV1A64((int) ghostAuthoringBakingData.BakingConfig.Config.OptimizationMode));
                contentHash = TypeHash.CombineFNV1A64(contentHash, ghostAuthoringBakingData.GhostNameHash);
                for (int i = 0; i < componentCounts[0]; ++i)
                {
                    var comp = allComponents[i];
                    var prefabType = prefabTypes[i];
                    contentHash = TypeHash.CombineFNV1A64(contentHash,
                        TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash);
                    contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int) prefabType));
                }

                compIdx = componentCounts[0];
                for (int i = 1; i < linkedEntities.Length; ++i)
                {
                    contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64(i));
                    var numComponent = componentCounts[i];
                    for (int k = 0; k < numComponent; ++k, ++compIdx)
                    {
                        var comp = allComponents[compIdx];
                        var prefabType = prefabTypes[compIdx];
                        contentHash = TypeHash.CombineFNV1A64(contentHash,
                            TypeManager.GetTypeInfo(comp.TypeIndex).StableTypeHash);
                        contentHash = TypeHash.CombineFNV1A64(contentHash, TypeHash.FNV1A64((int) prefabType));
                    }
                }

                var blobHash = new Unity.Entities.Hash128(
                    ghostAuthoringBakingData.GhostType.guid0 ^ (uint) (contentHash >> 32),
                    ghostAuthoringBakingData.GhostType.guid1 ^ (uint) (contentHash),
                    ghostAuthoringBakingData.GhostType.guid2, ghostAuthoringBakingData.GhostType.guid3);
                // instanceIds[0] 包含根 GameObject 的实例 ID
                if (context.NeedToComputeBlobAsset(blobHash))
                {
                    var blobAsset = GhostPrefabCreation.CreateBlobAsset(ghostAuthoringBakingData.BakingConfig.Config,
                        EntityManager, rootEntity, linkedEntities,
                        allComponents, componentCounts, ghostAuthoringBakingData.Target, prefabTypes,
                        sendMasksOverride, variants);
                    context.AddComputedBlobAsset(blobHash, blobAsset);
                }

                context.GetBlobAsset(blobHash, out var blob);
                EntityManager.SetComponentData(rootEntity, new GhostPrefabMetaData {Value = blob});
            }
        }
    }
}
