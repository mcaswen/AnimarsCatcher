using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using AnimarsCatcher.Mono.Global;
using Unity.Collections;


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
        // 懒加载 HUD Root
        if (s_GameHUDRoot == null)
        {
            s_GameHUDRoot = Object.FindFirstObjectByType<HealthHUDBootstrap>();
            if (s_GameHUDRoot == null)
            {
                // HUD 还没初始化好，先别生成血条
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

            Debug.Log($"[SpawnHealthBarViewSystem] Getting Component， HealthBarView: {(barView != null ? "Found" : "Not Found")} for Entity {entity.Index})");

            if (barView != null)
            {
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
