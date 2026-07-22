#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
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
    /// 客户端预期通过协议接收以下内容：</para>
    /// <para>- 用于验证的 SubScene Hash 与 Baseline Hash</para>
    /// <para>- 每个 SubScene 的 GhostId 范围</para>
    /// <para>### 完整的 Prespawn SubScene 同步协议</para>
    /// <para>
    /// 客户端最终会收到 SubScene 数据，并将其存入 `PrespawnSceneLoaded` 集合
    /// 加载新场景时，客户端还会在此前、此后或并行地序列化 Prespawn Baseline
    /// 客户端应验证：</para>
    /// <para>- Prespawn 场景存在于服务器</para>
    /// <para>- Prespawn Ghost 数量、SubScene Hash 与 Baseline Hash 和服务器一致</para>
    /// <para>客户端随后为 Prespawn 分配 GhostId
    /// 并且必须通知服务器哪些场景 Section 已完成加载和初始化
    /// </para>
    /// </remarks>
    /// <seealso cref="ServerPopulatePrespawnedGhostsSystem"/>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    [BurstCompile]
    public partial struct ClientPopulatePrespawnedGhostsSystem : ISystem
    {
        private EntityQuery m_UninitializedScenes;
        private EntityQuery m_Prespawns;

        private EntityTypeHandle m_EntityTypeHandle;
        private ComponentTypeHandle<PreSpawnedGhostIndex> m_PreSpawnedGhostIndexHandle;
        private ComponentTypeHandle<GhostInstance> m_GhostComponentHandle;
        private ComponentTypeHandle<GhostCleanup> m_GhostCleanupComponentHandle;

        enum ValidationResult
        {
            ValidationSucceed = 0,
            SubSceneNotFound,
            MetadataNotMatch
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SubSceneWithPrespawnGhosts, SubScenePrespawnBaselineResolved>()
                .WithNone<PrespawnsSceneInitialized>();
            m_UninitializedScenes = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<PreSpawnedGhostIndex, SubSceneGhostComponentHash>();
            m_Prespawns = state.GetEntityQuery(builder);

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_PreSpawnedGhostIndexHandle = state.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_GhostComponentHandle = state.GetComponentTypeHandle<GhostInstance>();
            m_GhostCleanupComponentHandle = state.GetComponentTypeHandle<GhostCleanup>();

            state.RequireForUpdate(m_UninitializedScenes);
            state.RequireForUpdate(m_Prespawns);
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<PrespawnSceneLoaded>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var subsceneCollection = SystemAPI.GetSingletonBuffer<PrespawnSceneLoaded>();
            // 列表为空时没有可处理内容，说明服务器尚未发送数据或 SubScene 必须卸载
            // 两种情况下客户端都无法分配 GhostId，因此提前退出
            if(subsceneCollection.Length == 0)
                return;

            var subScenesWithGhosts = m_UninitializedScenes.ToComponentDataArray<SubSceneWithPrespawnGhosts>(Allocator.Temp);
            // x 表示 SubScene 索引
            // y 表示集合索引
            var validScenes = new NativeList<int2>(subScenesWithGhosts.Length, Allocator.Temp);
            // 调度任何 Job 前先验证全部数据
            // 不检查服务器存在但客户端缺失的 SubScene，因为客户端设计上可以只加载服务器全部 SubScene 的一个子集
            var totalValidPrespawns = 0;
            var hasValidationError = false;
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            for (int i = 0; i < subScenesWithGhosts.Length; ++i)
            {
                var validationResult = ValidatePrespawnGhostSubSceneData(ref netDebug, subScenesWithGhosts[i].SubSceneHash,
                    subScenesWithGhosts[i].BaselinesHash, subScenesWithGhosts[i].PrespawnCount, subsceneCollection,
                    out var collectionIndex);
                if (validationResult == ValidationResult.SubSceneNotFound)
                {
                    // 这可能表示：
                    // - 客户端与服务器同时或更早加载了场景，但尚未收到更新后的场景列表
                    // - 服务器已卸载该场景，此时客户端负责通过取决于用户或游戏的上层协议将其卸载
                    // 两种情况都不是真正的错误：第一种应等待新列表，第二种应移除场景
                    // 后续最好能够区分这两种情况
                    continue;
                }
                if (validationResult == ValidationResult.MetadataNotMatch)
                {
                    // 先记录所有验证错误，之后统一请求断开连接
                    hasValidationError = true;
                    continue;
                }
                validScenes.Add(new int2(i, collectionIndex));
                totalValidPrespawns += subScenesWithGhosts[i].PrespawnCount;
            }
            if(hasValidationError)
            {
                // 断开客户端连接
                state.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(SystemAPI.GetSingletonEntity<NetworkId>());
                return;
            }
            // 为每个 SubScene 调度 Job，给场景内全部预生成 Ghost 分配 GhostId
            var subscenes = m_UninitializedScenes.ToEntityArray(Allocator.Temp);
            var entityCommandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            // 该临时列表用于在 Ghost 已注册时仍将 Entity 强制重新写入生成 Map
            var spawnedGhosts = new NativeList<SpawnedGhostMapping>(totalValidPrespawns, state.WorldUpdateAllocator);
            m_EntityTypeHandle.Update(ref state);
            m_PreSpawnedGhostIndexHandle.Update(ref state);
            m_GhostComponentHandle.Update(ref state);
            m_GhostCleanupComponentHandle.Update(ref state);
            for (int i = 0; i < validScenes.Length; ++i)
            {
                var sceneIndex = validScenes[i].x;
                var collectionIndex = validScenes[i].y;
                var sharedFilter = new SubSceneGhostComponentHash {Value = subScenesWithGhosts[sceneIndex].SubSceneHash};
                m_Prespawns.SetSharedComponentFilter(sharedFilter);
                LogAssignPrespawnGhostIds(ref netDebug, subScenesWithGhosts[sceneIndex]);
                var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                var assignPrespawnGhostIdJob = new AssignPrespawnGhostIdJob
                {
                    entityType = m_EntityTypeHandle,
                    prespawnIndexType = m_PreSpawnedGhostIndexHandle,
                    ghostComponentType = m_GhostComponentHandle,
                    ghostStateTypeHandle = m_GhostCleanupComponentHandle,
                    startGhostId = subsceneCollection[collectionIndex].FirstGhostId,
                    spawnedGhosts = spawnedGhosts.AsParallelWriter(),
                    netDebug = netDebug,
                    GhostTypeToColletionIndex = state.EntityManager.GetComponentData<GhostCollection>(collectionEntity).GhostTypeToColletionIndex,
                    ghostType = SystemAPI.GetComponentTypeHandle<GhostType>(),
                    isServer = state.WorldUnmanaged.IsServer()
                };
                state.Dependency = assignPrespawnGhostIdJob.ScheduleParallel(m_Prespawns, state.Dependency);
                // 添加状态 Component 以跟踪场景生命周期
                var sceneSectionData = default(SceneSectionData);
#if UNITY_EDITOR
                if (state.EntityManager.HasComponent<LiveLinkPrespawnSectionReference>(subscenes[i]))
                {
                    var sceneSectionRef = state.EntityManager.GetComponentData<LiveLinkPrespawnSectionReference>(subscenes[i]);
                    sceneSectionData.SceneGUID = sceneSectionRef.SceneGUID;
                    sceneSectionData.SubSectionIndex = sceneSectionRef.Section;
                }
                else
#endif
                    sceneSectionData = state.EntityManager.GetComponentData<SceneSectionData>(subscenes[sceneIndex]);
                entityCommandBuffer.AddComponent(subscenes[sceneIndex], new SubSceneWithGhostCleanup
                {
                    SubSceneHash = subScenesWithGhosts[sceneIndex].SubSceneHash,
                    FirstGhostId = subsceneCollection[collectionIndex].FirstGhostId,
                    PrespawnCount = subScenesWithGhosts[sceneIndex].PrespawnCount,
                    SceneGUID =  sceneSectionData.SceneGUID,
                    SectionIndex =  sceneSectionData.SubSectionIndex,
                });
                entityCommandBuffer.AddComponent<PrespawnsSceneInitialized>(subscenes[sceneIndex]);
            }
            m_Prespawns.ResetFilter();
            ref readonly var spawnedGhostEntityMap = ref SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO;
            var addJob = new ClientAddPrespawn
            {
                netDebug = netDebug,
                spawnedGhosts = spawnedGhosts,
                ghostMap = spawnedGhostEntityMap.SpawnedGhostMapRW,
                ghostEntityMap = spawnedGhostEntityMap.ClientGhostEntityMap
            };
            state.Dependency = addJob.Schedule(state.Dependency);
        }
        [BurstCompile]
        struct ClientAddPrespawn : IJob
        {
            public NetDebug netDebug;
            public NativeList<SpawnedGhostMapping> spawnedGhosts;
            public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;
            public NativeParallelHashMap<int, Entity> ghostEntityMap;
            public void Execute()
            {
                for (int i = 0; i < spawnedGhosts.Length; ++i)
                {
                    var newGhost = spawnedGhosts[i];
                    if (newGhost.ghost.ghostId == 0)
                    {
                        netDebug.LogError("Prespawn ghost id not assigned.");
                        return;
                    }

                    if (!ghostMap.TryAdd(newGhost.ghost, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the spawned ghost entity map.");
                        ghostMap[newGhost.ghost] = newGhost.entity;
                    }

                    if (!ghostEntityMap.TryAdd(newGhost.ghost.ghostId, newGhost.entity))
                    {
                        netDebug.LogError($"GhostID {newGhost.ghost.ghostId} already present in the ghost entity map. Overwrite");
                        ghostEntityMap[newGhost.ghost.ghostId] = newGhost.entity;
                    }
                }
            }
        }

        ValidationResult ValidatePrespawnGhostSubSceneData(ref NetDebug netDebug, ulong subSceneHash, ulong subSceneBaselineHash, int prespawnCount,
            in DynamicBuffer<PrespawnSceneLoaded> serverPrespawnHashBuffer, out int index)
        {
            // 查找匹配条目
            index = -1;
            for (int i = 0; i < serverPrespawnHashBuffer.Length; ++i)
            {
                if (serverPrespawnHashBuffer[i].SubSceneHash == subSceneHash)
                {
                    // 检查 Baseline 是否匹配
                    if (serverPrespawnHashBuffer[i].BaselineHash != subSceneBaselineHash)
                    {
                        netDebug.LogError(
                            $"Subscene {subSceneHash} baseline mismatch. Server:{serverPrespawnHashBuffer[i].BaselineHash} Client:{subSceneBaselineHash}");
                        return ValidationResult.MetadataNotMatch;
                    }

                    if (serverPrespawnHashBuffer[i].PrespawnCount != prespawnCount)
                    {
                        netDebug.LogError(
                            $"Subscene {subSceneHash} has different prespawn count. Server:{serverPrespawnHashBuffer[i].PrespawnCount} Client:{prespawnCount}");
                        return ValidationResult.MetadataNotMatch;
                    }

                    index = i;
                    return ValidationResult.ValidationSucceed;
                }
            }
            return ValidationResult.SubSceneNotFound;
        }

        [Conditional("NETCODE_DEBUG")]
        void LogAssignPrespawnGhostIds(ref NetDebug netDebug, in SubSceneWithPrespawnGhosts subScenesWithGhosts)
        {
            netDebug.DebugLog(FixedString.Format("Assigning prespawn ghost ids for scene Hash:{0} Count:{1}",
                NetDebug.PrintHex(subScenesWithGhosts.SubSceneHash), subScenesWithGhosts.PrespawnCount));
        }
    }
}
