using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using Unity.Collections;

namespace AnimarsCatcher.Presentation.HealthBars
{
    /// <summary>
    /// 在客户端为带血条配置且尚未生成视图的 Entity 创建 HUD
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct ClientSpawnHealthBarViewSystem : ISystem
    {
        private static HealthHUDBootstrap _gameHudRoot;

        public void OnCreate(ref SystemState state)
        {
            EntityQuery healthBarQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<Health>(),
                ComponentType.ReadOnly<HealthBarViewConfig>(),
                ComponentType.ReadOnly<LocalTransform>());
            state.RequireForUpdate(healthBarQuery);
        }

        public void OnUpdate(ref SystemState state)
        {
            // 场景切换后延迟查找当前活动 HUD 根节点
            if (_gameHudRoot == null)
            {
                _gameHudRoot = Object.FindFirstObjectByType<HealthHUDBootstrap>();
                if (_gameHudRoot == null)
                {
                    // HUD 尚未初始化时保留 Entity 到后续帧处理
                    return;
                }
            }

            var hud = _gameHudRoot;
            var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var entityManager = state.EntityManager;

            foreach (var (health, entity) in
                     SystemAPI.Query<RefRO<Health>>()
                              .WithAll<HealthBarViewConfig>()
                              .WithNone<HealthBarViewSpawnedTag>()
                              .WithEntityAccess())
            {
                var viewPrefab = SystemAPI.ManagedAPI.GetComponent<HealthBarViewConfig>(entity);

                if (viewPrefab == null || viewPrefab.HealthBarPrefab == null)
                {
                    continue;
                }

                GameObject instance = Object.Instantiate(
                    viewPrefab.HealthBarPrefab,
                    hud.HealthBarRoot
                );

                HealthBarView barView = instance.GetComponent<HealthBarView>();

                if (barView != null)
                {
                    // 依据本地玩家阵营决定友方和敌方血条颜色
                    bool isFriendly = false;

                    if (SystemAPI.TryGetSingleton<LocalPlayerCamp>(out var hudCamp))
                    {
                        Camp camp = SystemAPI.GetComponent<Camp>(entity);
                        isFriendly = CampUtility.IsAlly(camp.Value, hudCamp.Value);
                    }

                    barView.InitializeHealthBar(
                        entityManager,
                        entity,
                        hud.WorldCamera,
                        hud.Canvas,
                        viewPrefab.WorldOffset,
                        isFriendly
                    );
                }

                entityCommandBuffer.AddComponent<HealthBarViewSpawnedTag>(entity);
            }
            entityCommandBuffer.Playback(state.EntityManager);
            entityCommandBuffer.Dispose();
        }
    }
}
