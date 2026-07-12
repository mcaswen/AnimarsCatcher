using Unity.Entities;
using Unity.Burst;
using UnityEngine;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class AniSelectionUIAttachSystem : SystemBase
{
    protected override void OnUpdate()
    {
        // 已注入则跳过
        var query = SystemAPI.QueryBuilder()
            .WithAll<SelectionUIAttachedTag>()
            .Build();
        if (!query.IsEmpty) return;

        var bootstrap = Object.FindFirstObjectByType<AniSelectionUIBootstrap>(FindObjectsInactive.Exclude);
        if (bootstrap == null || !bootstrap.isActiveAndEnabled) return;

        var entityManager = EntityManager;
        
        // 准备单例
        Entity dragStateEntity;
        if (!SystemAPI.TryGetSingletonEntity<AniSelectionDragState>(out dragStateEntity))
            dragStateEntity = entityManager.CreateEntity(typeof(AniSelectionDragState));

        entityManager.AddComponentObject(dragStateEntity, new AniSelectionUIRef
        {
            WorldCamera = bootstrap.worldCamera,
            RootCanvas = bootstrap.rootCanvas,
            SelectionRect = bootstrap.selectionRect
        });
        
        entityManager.AddComponent<SelectionUIAttachedTag>(dragStateEntity);

        // 不作为射线目标，避免挡住 UI
        if (bootstrap.selectionRect)
        {
            var image = bootstrap.selectionRect.GetComponent<UnityEngine.UI.Image>();
            if (image) image.raycastTarget = false;
        }
    }
}
