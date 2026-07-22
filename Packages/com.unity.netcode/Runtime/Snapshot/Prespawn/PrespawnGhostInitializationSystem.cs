#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Scenes;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 负责准备和初始化所有 SubScene 的预生成 Ghost
    /// 初始化过程包含多个步骤：
    /// - 根据 Ghost Prefab 元数据剥离 Component，会产生大量结构变更
    /// - 启动 Baseline 序列化
    /// - 计算复合 Baseline Hash 并分配给各 SubScene
    ///
    /// 首先找出全部 Ghost Archetype Serializer 均已就绪的 SubScene 子集
    /// 随后为每个 SubScene 并行启动 Component 剥离、序列化与 Baseline 分配 Job
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [BurstCompile]
    partial struct PrespawnGhostInitializationSystem : ISystem, ISystemStartStop
    {
        EntityQuery m_PrespawnBaselines;
        EntityQuery m_UninitializedScenes;
        EntityQuery m_Prespawns;

        Entity m_SubSceneListPrefab;

        ComponentLookup<GhostPrefabMetaData> m_GhostPrefabMetaDataLookup;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        ComponentTypeHandle<GhostType> m_GhostTypeComponentHandle;

        BufferLookup<GhostComponentSerializer.State> m_GhostComponentSerializerStateHandle;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostCollectionPrefabSerializerHandle;
        BufferLookup<GhostCollectionComponentIndex> m_GhostCollectionComponentIndexHandle;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionPrefabHandle;
        BufferTypeHandle<PrespawnGhostBaseline> m_PrespawnGhostBaselineHandle;
        EntityTypeHandle m_EntityTypeHandle;
        ComponentLookup<GhostInstance> m_GhostComponentFromEntity;
        ComponentLookup<SubSceneWithPrespawnGhosts> m_SubSceneWithPrespawnGhostsFromEntity;


        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<PrespawnGhostBaseline>()
                .WithAll<SubSceneGhostComponentHash>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            m_PrespawnBaselines = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAllRW<PreSpawnedGhostIndex>()
                .WithAll<SubSceneGhostComponentHash, GhostType>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            m_Prespawns = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<SubSceneWithPrespawnGhosts, IsSectionLoaded>()
                .WithNone<SubScenePrespawnBaselineResolved>();
            m_UninitializedScenes = state.GetEntityQuery(builder);

            m_GhostPrefabMetaDataLookup = state.GetComponentLookup<GhostPrefabMetaData>(true);
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_GhostTypeComponentHandle = state.GetComponentTypeHandle<GhostType>(true);
            m_GhostComponentSerializerStateHandle = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostCollectionPrefabSerializerHandle = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionComponentIndexHandle = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_GhostCollectionPrefabHandle = state.GetBufferLookup<GhostCollectionPrefab>(true);
            m_PrespawnGhostBaselineHandle = state.GetBufferTypeHandle<PrespawnGhostBaseline>();
            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_GhostComponentFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_SubSceneWithPrespawnGhostsFromEntity = state.GetComponentLookup<SubSceneWithPrespawnGhosts>();

            state.RequireForUpdate<GhostCollection>();
            // 运行条件查询不要求场景已加载，以便及时创建 Singleton
            builder.Reset();
            builder.WithAny<SubSceneWithPrespawnGhosts, ForcePrespawnListPrefabCreate>()
                .WithNone<SubScenePrespawnBaselineResolved>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if(m_SubSceneListPrefab != Entity.Null)
                state.EntityManager.DestroyEntity(m_SubSceneListPrefab);
        }

        public void OnStartRunning(ref SystemState state)
        {
            // 延迟到此处创建，以免在不存在 Prespawn 时生成不必要的 Entity
            if (m_SubSceneListPrefab == Entity.Null)
            {
                m_SubSceneListPrefab = PrespawnHelper.CreatePrespawnSceneListGhostPrefab(state.EntityManager);
                state.RequireForUpdate(m_Prespawns);
            }
        }
        public void OnStopRunning(ref SystemState state)
        {}

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (m_UninitializedScenes.IsEmptyIgnoreFilter)
                return;
            var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
            var ghostPrefabTypes = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
            // 尚未加载任何数据，客户端和服务器都可能处于此状态
            // 服务器可能一直等待到至少一个连接进入 InGame 状态
            // 客户端则可能一直等待到收到服务器发来的待处理 Prefab
            if(ghostPrefabTypes.Length == 0)
                return;

            var processedPrefabs = new NativeParallelHashMap<GhostType, Entity>(256, state.WorldUpdateAllocator);
            var subSceneWithPrespawnGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            var subScenesSections = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            var readySections = new NativeList<int>(subScenesSections.Length, Allocator.Temp);

            // 填充用于快速查找的 Map，同时供 Component 剥离 Job 使用
            for (int i = 0; i < ghostPrefabTypes.Length; ++i)
            {
                if(ghostPrefabTypes[i].GhostPrefab != Entity.Null)
                    processedPrefabs.Add(ghostPrefabTypes[i].GhostType, ghostPrefabTypes[i].GhostPrefab);
            }

            // 找出全部 Prespawn Ghost 类型都已由 Ghost Collection 解析的场景
            // 此时对应 Serializer 均已就绪
            for (int i = 0; i < subScenesSections.Length; ++i)
            {
                // 数量较大时可考虑把该检查调度为 Job
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                var ghostTypes = m_Prespawns.ToComponentDataArray<GhostType>(Allocator.Temp);
                bool allArchetypeProcessed = true;
                for(int t=0;t<ghostTypes.Length && allArchetypeProcessed;++t)
                    allArchetypeProcessed &= processedPrefabs.ContainsKey(ghostTypes[t]);
                if(allArchetypeProcessed)
                    readySections.Add(i);
            }
            m_Prespawns.ResetFilter();

            // 如果没有场景完成 Ghost Prefab 解析或加载，则提前退出
            if (readySections.Length == 0)
                return;

            // 移除 Disabled Component
            // 直接作用于整个 Chunk 比使用 Command Buffer 更快
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                state.EntityManager.RemoveComponent<Disabled>(m_Prespawns);
            }
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            m_GhostPrefabMetaDataLookup.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);
            m_GhostTypeComponentHandle.Update(ref state);
            // 为每个 SubScene 的全部 Prefab 启动 Component 剥离 Job
            var jobs = new NativeList<JobHandle>(readySections.Length, Allocator.Temp);
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithPrespawnGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                // 剥离 Component 可能对大量 Chunk 产生结构变更
                // 因此安排在下一次 Simulation 更新开始时执行
                var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                LogStrippingPrespawn(ref netDebug, subSceneWithPrespawnGhosts[sceneIndex]);
                var stripPrespawnGhostJob = new PrespawnGhostStripComponentsJob
                {
                    metaDataFromEntity = m_GhostPrefabMetaDataLookup,
                    linkedEntityTypeHandle = m_LinkedEntityGroupHandle,
                    ghostTypeHandle = m_GhostTypeComponentHandle,
                    prefabFromType = processedPrefabs,
                    commandBuffer = ecb.AsParallelWriter(),
                    netDebug = netDebug,
                    server = (byte) (state.WorldUnmanaged.IsServer() ? 1 : 0),
                    isHost = (byte) (state.WorldUnmanaged.IsHost() ? 1 : 0),
                };
                jobs.Add(stripPrespawnGhostJob.ScheduleParallel(m_Prespawns, state.Dependency));
            }
            state.Dependency = JobHandle.CombineDependencies(jobs.AsArray());
            m_Prespawns.ResetFilter();

            // 如果不存在 Prespawn Baseline，则直接将所有场景标记为已解析
            if (m_PrespawnBaselines.IsEmptyIgnoreFilter)
            {
                for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
                {
                    var sceneIndex = readySections[readyScene];
                    var subScene = subScenesSections[sceneIndex];
                    state.EntityManager.AddComponent<SubScenePrespawnBaselineResolved>(subScene);
                }
                return;
            }

            m_GhostComponentSerializerStateHandle.Update(ref state);
            m_GhostCollectionPrefabSerializerHandle.Update(ref state);
            m_GhostCollectionComponentIndexHandle.Update(ref state);
            m_GhostCollectionPrefabHandle.Update(ref state);
            m_PrespawnGhostBaselineHandle.Update(ref state);
            m_EntityTypeHandle.Update(ref state);
            m_GhostComponentFromEntity.Update(ref state);

            // 序列化 Baseline 并添加已解析标签
            var serializerJob = new PrespawnGhostSerializer
            {
                GhostComponentCollectionFromEntity = m_GhostComponentSerializerStateHandle,
                GhostTypeCollectionFromEntity = m_GhostCollectionPrefabSerializerHandle,
                GhostComponentIndexFromEntity = m_GhostCollectionComponentIndexHandle,
                GhostCollectionFromEntity = m_GhostCollectionPrefabHandle,
                ghostTypeComponentType = m_GhostTypeComponentHandle,
                prespawnBaseline = m_PrespawnGhostBaselineHandle,
                entityType = m_EntityTypeHandle,
                childEntityLookup = state.GetEntityStorageInfoLookup(),
                linkedEntityGroupType = m_LinkedEntityGroupHandle,
                ghostFromEntity = m_GhostComponentFromEntity,
                GhostCollectionSingleton = collectionEntity
            };
            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);
            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref serializerJob.ghostChunkComponentTypes);

            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            m_SubSceneWithPrespawnGhostsFromEntity.Update(ref state);
            for (int readyScene = 0; readyScene < readySections.Length; ++readyScene)
            {
                var sceneIndex = readySections[readyScene];
                LogSerializingBaselines(ref netDebug, subSceneWithPrespawnGhosts[sceneIndex]);
                var subScene = subScenesSections[sceneIndex];
                var subSceneWithGhost = subSceneWithPrespawnGhosts[sceneIndex];
                var sharedFilter = new SubSceneGhostComponentHash {Value = subSceneWithGhost.SubSceneHash};
                m_PrespawnBaselines.SetSharedComponentFilter(sharedFilter);
                // 序列化 Baseline 并存储各自的 Baseline Hash
                var baselinesHashes = new NativeList<ulong>(subSceneWithGhost.PrespawnCount, state.WorldUpdateAllocator);
                serializerJob.baselineHashes = baselinesHashes.AsParallelWriter();
                var serializeJobHandle = serializerJob.ScheduleParallelByRef(m_PrespawnBaselines, state.Dependency);
                // 计算场景内全部 Ghost 的聚合 Baseline Hash
                var aggregateJob = new AggregateHash
                {
                    baselinesHashes = baselinesHashes,
                    subSceneWithGhostFromEntity = m_SubSceneWithPrespawnGhostsFromEntity,
                    subSceneWithGhost = subSceneWithGhost,
                    subScene = subScene
                };
                state.Dependency = aggregateJob.Schedule(serializeJobHandle);
                // 标记为已解析
                commandBuffer.AddComponent<SubScenePrespawnBaselineResolved>(subScene);
            }
            // 立即回放已解析场景的 Command
            commandBuffer.Playback(state.EntityManager);
            commandBuffer.Dispose();
        }

        [BurstCompile]
        struct AggregateHash : IJob
        {
            public NativeList<ulong> baselinesHashes;
            public ComponentLookup<SubSceneWithPrespawnGhosts> subSceneWithGhostFromEntity;
            public SubSceneWithPrespawnGhosts subSceneWithGhost;
            public Entity subScene;
            public void Execute()
            {
                // 排序以保持确定性顺序
                baselinesHashes.Sort();
                ulong baselineHash;
                unsafe
                {
                    baselineHash = Unity.Core.XXHash.Hash64((byte*)baselinesHashes.GetUnsafeReadOnlyPtr(),
                        baselinesHashes.Length * sizeof(ulong));
                }
                subSceneWithGhost.BaselinesHash = baselineHash;
                subSceneWithGhostFromEntity[subScene] = subSceneWithGhost;
            }
        }

        [Conditional("NETCODE_DEBUG")]
        private void LogStrippingPrespawn(ref NetDebug netDebug, in SubSceneWithPrespawnGhosts subSceneWithPrespawnGhosts)
        {
            netDebug.DebugLog(FixedString.Format("Initializing prespawn scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subSceneWithPrespawnGhosts.SubSceneHash),
                subSceneWithPrespawnGhosts.PrespawnCount));
        }
        [Conditional("NETCODE_DEBUG")]
        private void LogSerializingBaselines(ref NetDebug netDebug, in SubSceneWithPrespawnGhosts subSceneWithPrespawnGhosts)
        {
            netDebug.DebugLog(FixedString.Format("Serializing baselines for prespawn scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subSceneWithPrespawnGhosts.SubSceneHash),
                subSceneWithPrespawnGhosts.PrespawnCount));
        }
    }
}
