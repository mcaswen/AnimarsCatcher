using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class SpawnAvatarViewSystem : SystemBase
{
    private EntityQuery _spawnQuery;

    protected override void OnCreate()
    {
        _spawnQuery = SystemAPI.QueryBuilder()
            .WithAll<AvatarViewPrefabReference>()   
            .WithNone<AvatarViewSpawnedTag>()
            .Build();

        RequireForUpdate(_spawnQuery);
    }

    protected override void OnUpdate()
    {
        EntityManager entityManager = EntityManager;

        using NativeArray<Entity> entities = _spawnQuery.ToEntityArray(Allocator.Temp);
        if (entities.Length == 0) return;

        foreach (Entity targetEntity in entities)
        {
            var prefabReference =
                entityManager.GetComponentObject<AvatarViewPrefabReference>(targetEntity);

            if (prefabReference == null || prefabReference.ViewPrefab == null) continue;

            GameObject spawnedGameObject = Object.Instantiate(prefabReference.ViewPrefab);

            AvatarViewFollower follower = spawnedGameObject.GetComponent<AvatarViewFollower>()?? spawnedGameObject.AddComponent<AvatarViewFollower>();

            var proxy = spawnedGameObject.GetComponent<MovementSelectableProxy>();
            if (proxy != null)
            {
                proxy.Entity = targetEntity;
            }

            // 注入 Entity 与 EntityManager
            follower.Bind(targetEntity, entityManager);

            switch (prefabReference.ViewType)
            {
                case AvatarViewType.BlasterAni:
                    BlasterAniAttackView blasterView = spawnedGameObject.GetComponent<BlasterAniAttackView>()?? spawnedGameObject.AddComponent<BlasterAniAttackView>();
                    blasterView.Bind(targetEntity, entityManager, false);
                    break;
                case AvatarViewType.PickerAni:
                    PickerAniAttackView pickerView = spawnedGameObject.GetComponent<PickerAniAttackView>()?? spawnedGameObject.AddComponent<PickerAniAttackView>();
                    pickerView.Bind(targetEntity, entityManager, false);
                    break;
                // 可根据需要添加更多类型
            }

            entityManager.AddComponent<AvatarViewSpawnedTag>(targetEntity);
        }
    }
}
