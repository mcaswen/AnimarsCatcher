#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 负责为每个预生成 Ghost 分配唯一的 <see cref="GhostInstance.ghostId"/>
    /// 并将 Ghost 加入已生成 Ghost Map
    /// 依赖前一个初始化步骤确定需要处理的 SubScene 子集
    /// </summary>
    /// <remarks>
    /// <para>
    /// 服务器具有权威性，负责为每个场景分配唯一的 GhostId 范围
    /// 对每个包含 Prespawn Ghost 的 Section，流式传输协议会向客户端发送 Prespawn Hash、ID 范围和 Baseline Hash
    /// 客户端使用收到的 SubScene Hash 与 Baseline Hash 验证数据，并按 GhostId 范围像服务器一样为 Prespawn Ghost 分配 ID
    /// 因而无需保证场景加载顺序具有确定性
    /// 最后客户端向服务器确认已加载场景，服务器收到 Ack 后开始流式传输其中的预生成 Ghost
    /// </para>
    /// <para>### 完整的 Prespawn SubScene 同步协议</para>
    /// <para>
    /// 服务器计算 Prespawn Baseline
    /// 服务器为预生成 Ghost 分配运行时 GhostId
    /// 服务器将 `SubSceneHash`、`BaselineHash`、`FirstGhostId` 和 `PrespawnCount` 存入 `PrespawnSceneLoaded` 集合
    /// 服务器创建具有 `PrespawnSceneLoaded` Buffer 的新 Ghost，并将其序列化到客户端
    /// </para>
    /// </remarks>
    /// <seealso cref="ClientPopulatePrespawnedGhostsSystem"/>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    [BurstCompile]
    public partial struct ServerPopulatePrespawnedGhostsSystem : ISystem
    {
        EntityQuery m_UninitializedScenes;
        EntityQuery m_Prespawns;
        EntityQuery m_PrefabQuery;
        Entity m_GhostIdAllocator;

        EntityTypeHandle m_EntityTypeHandle;
        ComponentTypeHandle<PreSpawnedGhostIndex> m_PreSpawnedGhostIndexHandle;
        ComponentTypeHandle<GhostInstance> m_GhostComponentHandle;
        ComponentTypeHandle<GhostCleanup> m_GhostCleanupComponentHandle;
        BufferLookup<PrespawnGhostIdRange> m_PrespawnGhostIdRangeFromEntity;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SubSceneWithPrespawnGhosts, SubScenePrespawnBaselineResolved>()
                .WithNone<PrespawnsSceneInitialized>();
            m_UninitializedScenes = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<PreSpawnedGhostIndex, SubSceneGhostComponentHash>();
            m_Prespawns = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<PrespawnSceneLoaded, Prefab>();
            m_PrefabQuery = state.GetEntityQuery(builder);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_PreSpawnedGhostIndexHandle = state.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_GhostComponentHandle = state.GetComponentTypeHandle<GhostInstance>();
            m_GhostCleanupComponentHandle = state.GetComponentTypeHandle<GhostCleanup>();
            m_PrespawnGhostIdRangeFromEntity = state.GetBufferLookup<PrespawnGhostIdRange>();

            var atype = new NativeArray<ComponentType>(1, Allocator.Temp);
            atype[0] = ComponentType.ReadWrite<PrespawnGhostIdRange>();
            m_GhostIdAllocator = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(atype));
            state.EntityManager.SetName(m_GhostIdAllocator, (FixedString64Bytes)"PrespawnGhostIdAllocator");
            state.RequireForUpdate(m_UninitializedScenes);
            state.RequireForUpdate(m_Prespawns);
            // 要求至少存在一个 InGame 标签，服务器可以为每个客户端各有一个
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<GhostCollection>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<PrespawnSceneLoaded>(out var prespawnSceneListEntity))
            {
                var prefab = m_PrefabQuery.GetSingletonEntity();
                // TODO-release: 验证该问题是否仍会发生
                // 以下过程假定 NetworkStreamInGame 在稍后添加：
                // - 游戏启动
                // - 创建 NetworkId
                // - 用户 System 添加 NetworkStreamInGame
                // - 进入下一帧
                // - GhostCollection 运行并注册 Prefab
                // - PrespawnGhostInitializationSystem 的 OnBeforeStart 运行并创建 Prespawn 场景列表 Prefab
                // - 由于已存在 NetworkStreamInGame，该 Prefab 随即实例化
                // - GhostSendSystem 更新时发现一个尚未注册到 GhostCollection 的额外 Prefab 并抛出异常
                prespawnSceneListEntity = state.EntityManager.Instantiate(prefab);
                state.EntityManager.RemoveComponent<GhostPrefabMetaData>(prespawnSceneListEntity);
                state.EntityManager.GetBuffer<PrespawnSceneLoaded>(prespawnSceneListEntity).EnsureCapacity(128);
            }
            var subScenesWithGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            var subSceneEntities = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            // 为所有 Ghost 添加 GhostCleanup
            // 实测表明，当 Entity 数量较多时，例如超过 3000 个
            // 这种方式比在 Job 中通过 Command Buffer 逐个添加 Component 快约 5 到 6 倍
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                state.EntityManager.AddComponent<GhostCleanup>(m_Prespawns);
            }
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            // 该临时列表用于在 Ghost 已注册时仍将 Entity 强制重新写入客户端和服务器的生成 Map
            var totalPrespawns = 0;
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
                totalPrespawns += subScenesWithGhosts[i].PrespawnCount;
            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(totalPrespawns, state.WorldUpdateAllocator);
            // 为每个 SubScene 调度 Job，给场景内全部预生成 Ghost 分配 GhostId
            // 同时填充 Prespawn Ghost 数组，供发送与接收 System 写入 Ghost Map
            var subsceneCollection = state.EntityManager.GetBuffer<PrespawnSceneLoaded>(prespawnSceneListEntity);
            var entityCommandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            ref var spawnedGhostEntityMap = ref SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRW;

            m_EntityTypeHandle.Update(ref state);
            m_PreSpawnedGhostIndexHandle.Update(ref state);
            m_GhostComponentHandle.Update(ref state);
            m_GhostCleanupComponentHandle.Update(ref state);
            m_PrespawnGhostIdRangeFromEntity.Update(ref state);
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                LogAssignPrespawnGhostIds(ref netDebug, subScenesWithGhosts[i]);
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[i].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                // 为该 SubScene 分配或复用 GhostId 范围，并将 ID 分配给 Ghost
                int startId = AllocatePrespawnGhostRange(ref netDebug, ref spawnedGhostEntityMap, subScenesWithGhosts[i].SubSceneHash, subScenesWithGhosts[i].PrespawnCount);
                var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                var assignPrespawnGhostIdJob = new AssignPrespawnGhostIdJob
                {
                    entityType = m_EntityTypeHandle,
                    prespawnIndexType = m_PreSpawnedGhostIndexHandle,
                    ghostComponentType = m_GhostComponentHandle,
                    ghostStateTypeHandle = m_GhostCleanupComponentHandle,
                    startGhostId = startId,
                    spawnedGhosts = spawnedGhosts.AsParallelWriter(),
                    netDebug = netDebug,
                    GhostTypeToColletionIndex = state.EntityManager.GetComponentData<GhostCollection>(collectionEntity).GhostTypeToColletionIndex,
                    ghostType = SystemAPI.GetComponentTypeHandle<GhostType>(),
                    isServer = state.WorldUnmanaged.IsServer()
                };
                state.Dependency = assignPrespawnGhostIdJob.ScheduleParallel(m_Prespawns, state.Dependency);
                // 将 SubScene 加入集合，该集合会同步到客户端
                subsceneCollection.Add(new PrespawnSceneLoaded
                {
                    SubSceneHash = subScenesWithGhosts[i].SubSceneHash,
                    BaselineHash = subScenesWithGhosts[i].BaselinesHash,
                    FirstGhostId = startId,
                    PrespawnCount = subScenesWithGhosts[i].PrespawnCount
                });

                // 将场景标记为已初始化并添加生命周期跟踪
                var sceneSectionData = default(SceneSectionData);
#if UNITY_EDITOR
                if (state.EntityManager.HasComponent<LiveLinkPrespawnSectionReference>(subSceneEntities[i]))
                {
                    var sceneSectionRef = state.EntityManager.GetComponentData<LiveLinkPrespawnSectionReference>(subSceneEntities[i]);
                    sceneSectionData.SceneGUID = sceneSectionRef.SceneGUID;
                    sceneSectionData.SubSectionIndex = sceneSectionRef.Section;
                }
                else
#endif
                    sceneSectionData = state.EntityManager.GetComponentData<SceneSectionData>(subSceneEntities[i]);

                entityCommandBuffer.AddComponent<PrespawnsSceneInitialized>(subSceneEntities[i]);
                entityCommandBuffer.AddComponent(subSceneEntities[i], new SubSceneWithGhostCleanup
                {
                    SubSceneHash = subScenesWithGhosts[i].SubSceneHash,
                    FirstGhostId = startId,
                    PrespawnCount = subScenesWithGhosts[i].PrespawnCount,
                    SceneGUID = sceneSectionData.SceneGUID,
                    SectionIndex = sceneSectionData.SubSectionIndex
                });
            }
            m_Prespawns.ResetFilter();
            // 等待所有 GhostId 分配 Job 完成，再填充已生成 Ghost Map
            var addJob = new ServerAddPrespawn
            {
                netDebug = netDebug,
                spawnedGhosts = spawnedGhosts,
                ghostMap = spawnedGhostEntityMap.SpawnedGhostMapRW
            };
            state.Dependency = addJob.Schedule(state.Dependency);
        }

        [BurstCompile]
        struct ServerAddPrespawn : IJob
        {
            public NetDebug netDebug;
            public NativeList<SpawnedGhostMapping> spawnedGhosts;
            public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;
            public void Execute()
            {
                for (int i = 0; i < spawnedGhosts.Length; ++i)
                {
                    if (spawnedGhosts[i].ghost.ghostId == 0)
                    {
                        netDebug.LogError($"Prespawn ghost id not assigned.");
                        return;
                    }
                    var newGhost = spawnedGhosts[i];
                    if (!ghostMap.TryAdd(newGhost.ghost, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the spawned ghost entity map.");
                        // 强制重新分配映射
                        ghostMap[newGhost.ghost] = newGhost.entity;
                    }
                }
            }
        }

        /// <summary>
        /// 返回 SubScene 的起始 GhostId
        /// 同一 SubScene 再次加载时会复用原有 ID 范围
        /// </summary>
        // TODO: 后续可以通过复用其他已释放 ID 范围改进分配策略
        private int AllocatePrespawnGhostRange(ref NetDebug netDebug, ref SpawnedGhostEntityMap spawnedGhostEntityMap, ulong subSceneHash, int prespawnCount)
        {
            var allocatedRanges = m_PrespawnGhostIdRangeFromEntity[m_GhostIdAllocator];
            for (int r = 0; r < allocatedRanges.Length; ++r)
            {
                if (allocatedRanges[r].SubSceneHash == subSceneHash)
                {
                    // 此情况表示状态错误或发生 Hash 冲突
                    if (allocatedRanges[r].Reserved != 0)
                        throw new System.InvalidOperationException($"prespawn ids range already present for subscene with hash {subSceneHash}");

                    netDebug.DebugLog($"reusing prespawn ids range from {allocatedRanges[r].FirstGhostId} to {allocatedRanges[r].FirstGhostId + prespawnCount} for subscene with hash {subSceneHash}");
                    allocatedRanges[r] = new PrespawnGhostIdRange
                    {
                        SubSceneHash = subSceneHash,
                        FirstGhostId = allocatedRanges[r].FirstGhostId,
                        Count = (short)prespawnCount,
                        Reserved = 1
                    };
                    return allocatedRanges[r].FirstGhostId;
                }
            }

            var nextGhostId = 1;
            if (allocatedRanges.Length > 0)
                nextGhostId = allocatedRanges[allocatedRanges.Length - 1].FirstGhostId +
                              allocatedRanges[allocatedRanges.Length - 1].Count;

            var newRange = new PrespawnGhostIdRange
            {
                SubSceneHash = subSceneHash,
                FirstGhostId = nextGhostId,
                Count = (short)prespawnCount,
                Reserved = 1
            };
            allocatedRanges.Add(newRange);
            LogAllocatedIdRange(ref netDebug, newRange);
            // 更新服务器已分配的 Prespawn GhostId 上界
            spawnedGhostEntityMap.SetServerAllocatedPrespawnGhostId(nextGhostId + prespawnCount);
            return newRange.FirstGhostId;
        }

        [Conditional("NETCODE_DEBUG")]
        private void LogAllocatedIdRange(ref NetDebug netDebug, PrespawnGhostIdRange rangeAlloc)
        {
            netDebug.DebugLog($"Assigned id-range [{rangeAlloc.FirstGhostId}-{rangeAlloc.FirstGhostId + rangeAlloc.Count}] to scene section with hash {NetDebug.PrintHex(rangeAlloc.SubSceneHash)}");
        }

        [Conditional("NETCODE_DEBUG")]
        void LogAssignPrespawnGhostIds(ref NetDebug netDebug, in SubSceneWithPrespawnGhosts subScenesWithGhosts)
        {
            netDebug.DebugLog(FixedString.Format("Assigning prespawn ghost ids for scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subScenesWithGhosts.SubSceneHash), subScenesWithGhosts.PrespawnCount));
        }
    }
}
