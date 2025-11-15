using Unity.Entities;
using Unity.Burst;
using UnityEngine;
using Unity.NetCode;

[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class SelectionUIAttachSystem : SystemBase
{
    protected override void OnUpdate()
    {
        // 已注入则跳过
        var query = SystemAPI.QueryBuilder()
            .WithAll<SelectionUIAttachedTag>()
            .Build();
        if (!query.IsEmpty) return;

        var bootStrap = Object.FindFirstObjectByType<AniSelectionUIBootstrap>(FindObjectsInactive.Exclude);
        if (bootStrap == null || !bootStrap.isActiveAndEnabled) return;

        var entityManager = EntityManager;
        
        // 准备单例
        Entity dragStateEntity;
        if (!SystemAPI.TryGetSingletonEntity<AniSelectionDragState>(out dragStateEntity))
            dragStateEntity = entityManager.CreateEntity(typeof(AniSelectionDragState));

        entityManager.AddComponentObject(dragStateEntity, new AniSelectionUIRef
        {
            WorldCamera = bootStrap.worldCamera,
            RootCanvas = bootStrap.rootCanvas,
            SelectionRect = bootStrap.selectionRect
        });
        
        entityManager.AddComponent<SelectionUIAttachedTag>(dragStateEntity);

        // 不作为射线目标，避免挡住 UI
        if (bootStrap.selectionRect)
        {
            var image = bootStrap.selectionRect.GetComponent<UnityEngine.UI.Image>();
            if (image) image.raycastTarget = false;
        }
    }
}