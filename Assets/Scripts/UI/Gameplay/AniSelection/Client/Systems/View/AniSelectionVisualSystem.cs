using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 将 ECS 拖拽状态显示为 UGUI 框选矩形
/// </summary>
[BurstCompile]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct AniSelectionVisualSystem : ISystem
{
    /// <summary>
    /// 等待拖拽状态和托管 UI 引用可用
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AniSelectionDragState>();
        state.RequireForUpdate<AniSelectionUIReference>();
    }

    /// <summary>
    /// 将屏幕端点转换为 Canvas 局部矩形
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        var drag = SystemAPI.GetSingleton<AniSelectionDragState>();

        foreach (var ui in SystemAPI.Query<AniSelectionUIReference>())
        {
            var rect = ui.SelectionRect;
            if (!rect) continue;

            if (drag.IsDragging == 0)
            {
                if (rect.gameObject.activeSelf) rect.gameObject.SetActive(false);
                continue;
            }

            if (!rect.gameObject.activeSelf) rect.gameObject.SetActive(true);

            var canvasRect = ui.RootCanvas.transform as RectTransform;

            Vector2 canvasScreenStartPosition, canvasScreenEndPosition;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, drag.StartScreen, null, out canvasScreenStartPosition);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, drag.EndScreen, null, out canvasScreenEndPosition);

            // 归一化拖拽方向后更新左下角位置和尺寸
            var min = Vector2.Min(canvasScreenStartPosition, canvasScreenEndPosition);
            var size = Vector2.Max(canvasScreenStartPosition, canvasScreenEndPosition) - min;

            rect.anchoredPosition = min;
            rect.sizeDelta = size;
        }
    }
}
