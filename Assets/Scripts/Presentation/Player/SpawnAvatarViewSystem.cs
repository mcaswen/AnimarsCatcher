namespace AnimarsCatcher.Presentation.PlayerView
{
    using AnimarsCatcher.Presentation.Anis;
    using AnimarsCatcher.Presentation.Selection;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using UnityEngine;

    /// <summary>
    /// 在客户端为带表现配置的实体创建并绑定 GameObject 视图
    /// </summary>
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

                // 表现对象只在 Client World 创建，服务器保持纯实体状态
                GameObject spawnedGameObject = Object.Instantiate(prefabReference.ViewPrefab);

                // Prefab 缺少跟随组件时动态补齐，保证实体和表现对象生命周期一致
                AvatarViewFollower follower = spawnedGameObject.GetComponent<AvatarViewFollower>()?? spawnedGameObject.AddComponent<AvatarViewFollower>();

                var proxy = spawnedGameObject.GetComponent<MovementSelectableProxy>();
                if (proxy != null)
                {
                    proxy.Entity = targetEntity;
                }

                // 显式注入所属世界，避免表现对象通过默认世界访问错误实体
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
                }

                // 所有绑定完成后再标记，防止异常中断留下半初始化视图
                entityManager.AddComponent<AvatarViewSpawnedTag>(targetEntity);
            }
        }
    }
}
