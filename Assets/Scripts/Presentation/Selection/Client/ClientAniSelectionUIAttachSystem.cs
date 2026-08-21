using Unity.Entities;
using Unity.Burst;
using UnityEngine;
using Unity.NetCode;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 将场景中的框选 UI 对象绑定到客户端 ECS 单例
    /// 托管引用只在 Presentation 阶段建立一次
    /// </summary>
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class ClientAniSelectionUIAttachSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // Attached 标签保证场景 UI 只绑定一次
            var query = SystemAPI.QueryBuilder()
                .WithAll<AniSelectionUIAttachedTag>()
                .Build();
            if (!query.IsEmpty) return;

            var bootstrap = Object.FindFirstObjectByType<AniSelectionUIBootstrap>(FindObjectsInactive.Exclude);
            if (bootstrap == null || !bootstrap.isActiveAndEnabled) return;

            var entityManager = EntityManager;

            // 缺少拖拽状态单例时由当前 UI 绑定系统创建
            Entity dragStateEntity;
            if (!SystemAPI.TryGetSingletonEntity<AniSelectionDragState>(out dragStateEntity))
                dragStateEntity = entityManager.CreateEntity(typeof(AniSelectionDragState));

            entityManager.AddComponentObject(dragStateEntity, new AniSelectionUIReference
            {
                WorldCamera = bootstrap.WorldCamera,
                RootCanvas = bootstrap.RootCanvas,
                SelectionRect = bootstrap.SelectionRect
            });

            entityManager.AddComponent<AniSelectionUIAttachedTag>(dragStateEntity);

            // 框选矩形只负责显示，不参与 UI 射线检测
            if (bootstrap.SelectionRect)
            {
                var image = bootstrap.SelectionRect.GetComponent<UnityEngine.UI.Image>();
                if (image) image.raycastTarget = false;
            }
        }
    }
}
