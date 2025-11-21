using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial struct AniSelectionVisualSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AniSelectionDragState>();
        state.RequireForUpdate<AniSelectionUIRef>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var drag = SystemAPI.GetSingleton<AniSelectionDragState>();

        foreach (var ui in SystemAPI.Query<AniSelectionUIRef>())
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

            var min = Vector2.Min(canvasScreenStartPosition, canvasScreenEndPosition);
            var size = Vector2.Max(canvasScreenStartPosition, canvasScreenEndPosition) - min;

            rect.anchoredPosition = min;
            rect.sizeDelta = size;
        }
    }
}
