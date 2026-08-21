namespace AnimarsCatcher.Presentation.EntityView
{
    using AnimarsCatcher.Presentation.Anis;
    using AnimarsCatcher.Presentation.Selection;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 在客户端为带表现配置的 Entity 创建并绑定 GameObject 视图
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class ClientSpawnEntityViewSystem : SystemBase
    {
        private EntityQuery _spawnQuery;

        protected override void OnCreate()
        {
            _spawnQuery = SystemAPI.QueryBuilder()
                .WithAll<EntityViewConfig>()
                .WithNone<EntityViewSpawnedTag>()
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
                    entityManager.GetComponentObject<EntityViewConfig>(targetEntity);

                if (prefabReference == null || prefabReference.ViewPrefab == null) continue;

                // 表现对象只在 Client World 创建，服务器保持纯 Entity 状态
                GameObject spawnedGameObject = Object.Instantiate(prefabReference.ViewPrefab);

                // Prefab 缺少跟随组件时动态补齐，保证 Entity 和表现对象生命周期一致
                EntityViewFollower follower = spawnedGameObject.GetComponent<EntityViewFollower>() ?? spawnedGameObject.AddComponent<EntityViewFollower>();

                var proxy = spawnedGameObject.GetComponent<WorldCommandTargetProxy>();
                if (proxy != null)
                {
                    proxy.Bind(targetEntity);
                }

                // 将所属 World 直接传给表现对象，避免它通过默认 World 访问错误的 Entity
                follower.Bind(targetEntity, entityManager);

                switch (prefabReference.Kind)
                {
                    case EntityViewKind.BlasterAni:
                        BlasterAniAttackView blasterView = spawnedGameObject.GetComponent<BlasterAniAttackView>() ?? spawnedGameObject.AddComponent<BlasterAniAttackView>();
                        blasterView.Bind(targetEntity, entityManager);
                        break;
                    case EntityViewKind.PickerAni:
                        PickerAniAttackView pickerView = spawnedGameObject.GetComponent<PickerAniAttackView>() ?? spawnedGameObject.AddComponent<PickerAniAttackView>();
                        pickerView.Bind(targetEntity, entityManager);
                        break;
                }

                // 所有绑定完成后再标记，防止异常中断留下半初始化视图
                entityManager.AddComponent<EntityViewSpawnedTag>(targetEntity);
            }
        }
    }
}
