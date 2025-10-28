using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.LocalSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class SpawnCharacterViewSystem : SystemBase
{
    private EntityQuery _entityQuery;
    protected override void OnCreate()
    {
        _entityQuery = SystemAPI.QueryBuilder()
            .WithAll<CharacterTag, CharacterViewPrefab>() 
            .WithNone<ViewSpawnedTag>()
            .Build();

        RequireForUpdate(_entityQuery);
    }

    protected override void OnUpdate()
    {
        var entityManager = EntityManager;

        using var entities = _entityQuery.ToEntityArray(Allocator.Temp);
        if (entities.Length == 0) return;

        foreach (var characterEntity in entities)
        {
            var view = entityManager.GetComponentObject<CharacterViewPrefab>(characterEntity);
            if (view?.ViewPrefab == null) continue;

            var viewGameObject = GameObject.Instantiate(view.ViewPrefab);
            var follower = viewGameObject.GetComponent<CharacterViewFollower>() ?? viewGameObject.AddComponent<CharacterViewFollower>();
            follower.Bind(characterEntity, entityManager);

            if (!entityManager.HasComponent<CharacterAnimationComponent>(characterEntity))
                entityManager.AddComponent<CharacterAnimationComponent>(characterEntity);

            entityManager.AddComponent<ViewSpawnedTag>(characterEntity);
        }
    }
}
