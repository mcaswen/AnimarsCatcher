using Unity.Entities;

namespace Unity.NetCode
{
    // 必须在 GhostAuthoringBakingSystem 之前运行，确保处理 Ghost 前 Buffer 已存在
    // GhostAuthoringBakingSystem 位于 PostBakingSystemGroup，因此将本系统放入普通烘焙组即可保证该顺序
    [UpdateInGroup(typeof(BakingSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [BakingVersion("cmarastoni", 1)]
    internal partial class GhostInputBufferBakingSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // 注意：此 Singleton Entity 总会在第一次非增量处理时被销毁
            // 因为首次导入并打开 SubScene 时，烘焙系统会清理 World 中的所有 Entity
            // 这里以延迟方式重新创建该 Entity，使所有逻辑保持预期行为
            if (!SystemAPI.TryGetSingleton<GhostComponentSerializerCollectionData>(out var serializerCollectionData))
            {
                var systemGroup = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>();
                EntityManager.CreateSingleton(systemGroup.ghostComponentSerializerCollectionDataCache);
                serializerCollectionData = systemGroup.ghostComponentSerializerCollectionDataCache;
            }
            foreach (var input in serializerCollectionData.InputComponentBufferMap)
            {
                var addBufferQuery = GetEntityQuery(
                    new EntityQueryDesc
                    {
                        All = new[]
                        {
                            input.Key
                        },
                        None = new []
                        {
                            input.Value
                        },
                        Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
                    });
                EntityManager.AddComponent(addBufferQuery, input.Value);
            }
        }
    }
}
