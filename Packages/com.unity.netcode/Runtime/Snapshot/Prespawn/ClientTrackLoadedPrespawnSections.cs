using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Scenes;
using Unity.Burst;

namespace Unity.NetCode
{
    /// <summary>
    /// 负责跟踪 Scene Section 的卸载
    /// 并从客户端 Ghost Map 中移除其中的预生成 Ghost
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateAfter(typeof(PrespawnGhostInitializationSystem))]
    [BurstCompile]
    public partial struct ClientTrackLoadedPrespawnSections : ISystem
    {
        private EntityQuery m_UnloadedSubscenes;
        private EntityQuery m_Prespawns;

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
                .WithAll<SubSceneWithGhostCleanup>()
                .WithNone<IsSectionLoaded>();
            m_UnloadedSubscenes = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<PreSpawnedGhostIndex, SubSceneGhostComponentHash>();
            m_Prespawns = state.GetEntityQuery(builder);

            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate(m_UnloadedSubscenes);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var unloadedScenes = m_UnloadedSubscenes.ToEntityArray(Allocator.Temp);

            if(unloadedScenes.Length == 0)
                return;

            // 仅处理所有 Prefab 都已销毁的场景
            var ghostsToRemove = new NativeList<SpawnedGhost>(128, state.WorldUpdateAllocator);
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            for(int i=0;i<unloadedScenes.Length;++i)
            {
                var stateComponent = state.EntityManager.GetComponentData<SubSceneWithGhostCleanup>(unloadedScenes[i]);
                m_Prespawns.SetSharedComponentFilter(new SubSceneGhostComponentHash { Value = stateComponent.SubSceneHash });
                if (m_Prespawns.IsEmpty)
                {
                    var firstId = PrespawnHelper.PrespawnGhostIdBase + stateComponent.FirstGhostId;
                    for (int p = 0; p < stateComponent.PrespawnCount; ++p)
                    {
                        ghostsToRemove.Add(new SpawnedGhost
                        {
                            ghostId = (int) (firstId + p),
                            spawnTick = NetworkTick.Invalid
                        });
                    }

                    entityCommandBuffer.RemoveComponent<PrespawnsSceneInitialized>(unloadedScenes[i]);
                    entityCommandBuffer.RemoveComponent<SubScenePrespawnBaselineResolved>(unloadedScenes[i]);
                    entityCommandBuffer.RemoveComponent<SubSceneWithGhostCleanup>(unloadedScenes[i]);
                }
            }
            entityCommandBuffer.Playback(state.EntityManager);

            if (ghostsToRemove.Length == 0)
                return;

            // 从生成 Map 中移除 Ghost
            ref readonly var ghostMapSingleton = ref SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO;
            var removeJob = new RemovePrespawnedGhosts
            {
                ghostsToRemove = ghostsToRemove,
                spawnedGhostEntityMap = ghostMapSingleton.SpawnedGhostMapRW,
                ghostEntityMap = ghostMapSingleton.ClientGhostEntityMap
            };
            state.Dependency = removeJob.Schedule(state.Dependency);
        }
        [BurstCompile]
        struct RemovePrespawnedGhosts : IJob
        {
            public NativeList<SpawnedGhost> ghostsToRemove;
            public NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostEntityMap;
            public NativeParallelHashMap<int, Entity> ghostEntityMap;
            public void Execute()
            {
                for(int i=0;i<ghostsToRemove.Length;++i)
                {
                    spawnedGhostEntityMap.Remove(ghostsToRemove[i]);
                    ghostEntityMap.Remove(ghostsToRemove[i].ghostId);
                }
            }
        }
    }
}
