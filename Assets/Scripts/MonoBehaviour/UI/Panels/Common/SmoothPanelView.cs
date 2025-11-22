using TMPro;
using UnityEngine;
using DG.Tweening;

public static class SmoothPanelView
{
    private static CanvasGroup GetOrAddCanvasGroup(GameObject panel)
        {
            var cg = panel.GetComponent<CanvasGroup>();
            if (!cg) cg = panel.AddComponent<CanvasGroup>();
            return cg;
        }

    public static void ShowPanel(GameObject panel, float panelAnimDuration = 0.25f)
    {
        var canvasGroup = GetOrAddCanvasGroup(panel);
        var rectTransform = panel.transform as RectTransform;

        rectTransform.DOKill(true); canvasGroup.DOKill(true);

        panel.SetActive(true);
        rectTransform.localScale = Vector3.one * 0.8f;
        canvasGroup.alpha = 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        DOTween.Sequence().SetUpdate(true)
            .Append(rectTransform.DOScale(1f, panelAnimDuration).SetEase(Ease.OutBack))
            .Join(DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 1f, panelAnimDuration))
            .OnComplete(() =>
            {
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
            });
    }

    public static void HidePanel(GameObject panel, float panelAnimDuration = 0.25f)
    {
        var canvasGroup = GetOrAddCanvasGroup(panel);
        var rectTransform = panel.transform as RectTransform;

        rectTransform.DOKill(true); canvasGroup.DOKill(true);

        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        DOTween.Sequence().SetUpdate(true)
            .Append(rectTransform.DOScale(0.85f, panelAnimDuration * 0.8f).SetEase(Ease.InSine))
            .Join(DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 0f, panelAnimDuration * 0.8f))
            .OnComplete(() => panel.SetActive(false));
    }
}