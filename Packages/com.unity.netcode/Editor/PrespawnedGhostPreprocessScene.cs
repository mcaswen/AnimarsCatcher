using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// 在 Editor 中处理已打开编辑且包含 Pre-spawned Ghost 的 SubScene
    /// 这是对转换工作流限制的规避方案，该限制会导致 SubScene 打开编辑时无法向 Scene Section Entity 添加自定义组件
    /// 为解决此问题，这里在运行时添加 SubSceneWithPrespawnGhosts，并向 Scene Section Entity 添加 LiveLinkPrespawnSectionReference，以补齐所引用 Section 的信息
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct PrespawnedGhostPreprocessScene : ISystem
    {
        struct PrespawnSceneExtracted : IComponentData
        {
        }
        //SceneSystem.SectionLoadedFromEntity m_SectionLoadedFromEntity;
        private EntityQuery prespawnToPreprocess;
        private EntityQuery sectionsToProcess;
        private SharedComponentTypeHandle<SubSceneGhostComponentHash> prespawnHashTypeHandle;
        private SharedComponentTypeHandle<SceneSection> sceneSectionTypeHandle;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PreSpawnedGhostIndex, SceneTag>()
                .WithAllRW<SubSceneGhostComponentHash>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
            prespawnToPreprocess = state.GetEntityQuery(builder);

            builder.Reset();
            builder.WithAll<DisableSceneResolveAndLoad, SceneEntityReference>()
                .WithNone<SceneSectionData, PrespawnSceneExtracted, SubSceneWithPrespawnGhosts>();
            sectionsToProcess = state.GetEntityQuery(builder);

            prespawnHashTypeHandle = state.GetSharedComponentTypeHandle<SubSceneGhostComponentHash>();
            sceneSectionTypeHandle = state.GetSharedComponentTypeHandle<SceneSection>();
            state.RequireForUpdate(prespawnToPreprocess);
            state.RequireForUpdate(sectionsToProcess);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            prespawnHashTypeHandle.Update(ref state);
            sceneSectionTypeHandle.Update(ref state);
            prespawnToPreprocess.ResetFilter();
            var sceneEntities = sectionsToProcess.ToEntityArray(Allocator.Temp);
            foreach (var sectionEntity in sceneEntities)
            {
                if(!state.EntityManager.HasComponent<IsSectionLoaded>(sectionEntity))
                    continue;

                prespawnToPreprocess.SetSharedComponentFilter(new SceneTag{SceneEntity = sectionEntity});
                state.EntityManager.AddComponent<PrespawnSceneExtracted>(sectionEntity);
                var count = prespawnToPreprocess.CalculateEntityCount();
                if (count == 0)
                    continue;

                using var chunks = prespawnToPreprocess.ToArchetypeChunkArray(Allocator.Temp);
                var prespawnGhostHash = chunks[0].GetSharedComponent(prespawnHashTypeHandle);
                var sceneSection = chunks[0].GetSharedComponent(sceneSectionTypeHandle);
                state.EntityManager.AddComponentData(sectionEntity, new SubSceneWithPrespawnGhosts
                {
                    SubSceneHash = prespawnGhostHash.Value,
                    BaselinesHash = 0,
                    PrespawnCount = count
                });
                // 添加该组件以取得 Section 索引与 Scene GUID，重新 Spawn Pre-spawned Ghost 时需要这些信息才能正确恢复 SceneSection 组件
                // FIXME 确认仅使用 SceneTag 是否足以保证卸载 Scene 时删除重新 Spawn 的 Pre-spawned Ghost，若可行即可移除该组件并简化相关逻辑
                state.EntityManager.AddComponentData(sectionEntity, new LiveLinkPrespawnSectionReference
                {
                    SceneGUID = sceneSection.SceneGUID,
                    Section = sceneSection.Section
                });
            }
        }
    }
}
