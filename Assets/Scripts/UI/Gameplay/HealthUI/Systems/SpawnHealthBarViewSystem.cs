using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Gameplay;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using AnimarsCatcher.Mono.Global;
using Unity.Collections;


/// <summary>
/// 在客户端为带血条配置且尚未生成视图的实体创建 HUD
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct SpawnHealthBarViewSystem : ISystem
{
    private static HealthHUDBootstrap s_GameHUDRoot;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(
            SystemAPI.QueryBuilder()
                .WithAll<Health, HealthBarViewPrefab, LocalTransform>()
                .Build());
    }

    public void OnUpdate(ref SystemState state)
    {
        // 场景切换后延迟查找当前活动 HUD 根节点
        if (s_GameHUDRoot == null)
        {
            s_GameHUDRoot = Object.FindFirstObjectByType<HealthHUDBootstrap>();
            if (s_GameHUDRoot == null)
            {
                // HUD 尚未初始化时保留实体到后续帧处理
                return;
            }
        }

        var hud = s_GameHUDRoot;
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var entityManager = state.EntityManager;

        foreach (var (health, entity) in
                 SystemAPI.Query<RefRO<Health>>()
                          .WithAll<HealthBarViewPrefab>()
                          .WithNone<HealthBarViewSpawnedTag>()
                          .WithEntityAccess())
        {
            var viewPrefab = SystemAPI.ManagedAPI.GetComponent<HealthBarViewPrefab>(entity);

            if (viewPrefab == null || viewPrefab.healthBarPrefab == null)
            {
                continue;
            }

            GameObject instance = Object.Instantiate(
                viewPrefab.healthBarPrefab,
                hud.healthBarRoot
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
                    hud.worldCamera,
                    hud.canvas,
                    viewPrefab.worldOffset,
                    isFriendly
                );
            }

            entityCommandBuffer.AddComponent<HealthBarViewSpawnedTag>(entity);
        }
        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
