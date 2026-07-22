using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 负责跟踪已初始化 Prespawn Section 的卸载
    /// 以释放已分配数据和 GhostId 范围
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(ServerPopulatePrespawnedGhostsSystem))]
    [BurstCompile]
    public partial struct ServerTrackLoadedPrespawnSections : ISystem
    {
        EntityQuery m_UnloadedSubscenes;
        EntityQuery m_Prespawns;
        EntityQuery m_AllPrespawnScenes;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<SubSceneWithGhostCleanup>()
                .WithNone<IsSectionLoaded>();
            m_UnloadedSubscenes = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<PreSpawnedGhostIndex, SubSceneGhostComponentHash>();
            m_Prespawns = state.GetEntityQuery(builder);
            m_AllPrespawnScenes = state.GetEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>());

            state.RequireForUpdate(m_UnloadedSubscenes);
            state.RequireForUpdate<GhostCollection>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var unloadedSections = m_UnloadedSubscenes.ToEntityArray(Allocator.Temp);

            if (unloadedSections.Length == 0)
                return;

            var entityCommandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            // 仅处理所有 Prefab 都已销毁的场景
            var subsceneCollection = SystemAPI.GetSingletonBuffer<PrespawnSceneLoaded>();
            var allocatedRanges = SystemAPI.GetSingletonBuffer<PrespawnGhostIdRange>();
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            var unloadedGhostRange = new NativeList<int2>(state.WorldUpdateAllocator);
            for(int i=0;i<unloadedSections.Length;++i)
            {
                var stateComponent = state.EntityManager.GetComponentData<SubSceneWithGhostCleanup>(unloadedSections[i]);
                m_Prespawns.SetSharedComponentFilter(new SubSceneGhostComponentHash { Value = stateComponent.SubSceneHash });

                // 如果仍有 Ghost 存在，则暂不从场景列表移除该场景
                // 注意：此检查只能判断 Ghost 是否已 Despawn
                // 对应 Entity 仍可能在等待 Ack，并由 GhostCleanup 跟踪
                if (!m_Prespawns.IsEmpty)
                    continue;

                // 查找场景并将其从集合移除
                int idx = 0;
                for (; idx < subsceneCollection.Length; ++idx)
                {
                    if (subsceneCollection[idx].SubSceneHash == stateComponent.SubSceneHash)
                        break;
                }

                if (idx != subsceneCollection.Length)
                {
                    subsceneCollection.RemoveAtSwapBack(idx);
                }
                else
                {
                    netDebug.LogError($"Scene with hash {stateComponent.SubSceneHash} not found in active subscene list");
                }
                // 释放 ID 范围以供后续复用
                // 为保持简单，目前只允许同一场景复用自己的 GhostId
                unloadedGhostRange.Add(new int2(stateComponent.FirstGhostId, stateComponent.PrespawnCount));
                for (int rangeIdx = 0; i < allocatedRanges.Length; ++rangeIdx)
                {
                    if (allocatedRanges[rangeIdx].Reserved != 0 &&
                        allocatedRanges[rangeIdx].SubSceneHash == stateComponent.SubSceneHash)
                    {
                        allocatedRanges[rangeIdx] = new PrespawnGhostIdRange
                        {
                            SubSceneHash = allocatedRanges[rangeIdx].SubSceneHash,
                            FirstGhostId = allocatedRanges[rangeIdx].FirstGhostId,
                            Count = allocatedRanges[rangeIdx].Count,
                            Reserved = 0
                        };
                        break;
                    }
                }
                entityCommandBuffer.RemoveComponent<PrespawnsSceneInitialized>(unloadedSections[i]);
                entityCommandBuffer.RemoveComponent<SubScenePrespawnBaselineResolved>(unloadedSections[i]);
                entityCommandBuffer.RemoveComponent<SubSceneWithGhostCleanup>(unloadedSections[i]);
            }

            if (unloadedGhostRange.Length == 0)
                return;
            // 如果存在 Prespawn，则为 Despawn 列表调度清理 Job
            // 范围释放后，即 Reserved == 0，属于该范围的 Ghost 不会再由 GhostSendSystem 加入队列
            var cleanupJob = new PrespawnSceneCleanup
            {
                unloadedGhostRange = unloadedGhostRange,
                despawns = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.ServerDestroyedPrespawns
            };
            state.Dependency = cleanupJob.Schedule(state.Dependency);

            // 如果已不存在 Prespawn 场景，则销毁 Prespawn 场景列表
            if(subsceneCollection.Length == 0 && m_AllPrespawnScenes.IsEmpty)
                entityCommandBuffer.DestroyEntity(SystemAPI.GetSingletonEntity<PrespawnSceneLoaded>());
        }

        [BurstCompile]
        struct PrespawnSceneCleanup : IJob
        {
            public NativeList<int2> unloadedGhostRange;
            public NativeList<int> despawns;
            public void Execute()
            {
                for (int i = 0; i < unloadedGhostRange.Length; ++i)
                {
                    var firstId = unloadedGhostRange[i].x;
                    for (int idx = 0; idx < unloadedGhostRange[i].y; ++idx)
                    {
                        var ghostId = PrespawnHelper.MakePrespawnGhostId(firstId + idx);
                        var found = despawns.IndexOf(ghostId);
                        if (found != -1)
                            despawns.RemoveAtSwapBack(found);
                    }
                }
            }
        }
    }
}
