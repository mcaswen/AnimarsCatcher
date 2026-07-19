using TMPro;
using UnityEngine;
using DG.Tweening;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 为普通 UI 面板提供统一的缩放和淡入淡出过渡
    /// 动画使用独立更新时间 因此暂停游戏时仍可操作菜单
    /// </summary>
    public static class SmoothPanelView
    {
        private static CanvasGroup GetOrAddCanvasGroup(GameObject panel)
            {
                var cg = panel.GetComponent<CanvasGroup>();
                if (!cg) cg = panel.AddComponent<CanvasGroup>();
                return cg;
            }

        /// <summary>
        /// 激活面板并播放进入动画
        /// </summary>
        public static void ShowPanel(GameObject panel, float panelAnimationDuration = 0.25f)
        {
            var canvasGroup = GetOrAddCanvasGroup(panel);
            var rectTransform = panel.transform as RectTransform;

            // 完成旧动画后重设交互状态 避免快速切换产生残留 Tween
            rectTransform.DOKill(true); canvasGroup.DOKill(true);

            panel.SetActive(true);
            rectTransform.localScale = Vector3.one * 0.8f;
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            DOTween.Sequence().SetUpdate(true)
                .Append(rectTransform.DOScale(1f, panelAnimationDuration).SetEase(Ease.OutBack))
                .Join(DOTween.To(() => canvasGroup.alpha, alpha => canvasGroup.alpha = alpha, 1f, panelAnimationDuration))
                .OnComplete(() =>
                {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                });
        }

        /// <summary>
        /// 禁用交互并在退出动画完成后隐藏面板
        /// </summary>
        public static void HidePanel(GameObject panel, float panelAnimationDuration = 0.25f)
        {
            var canvasGroup = GetOrAddCanvasGroup(panel);
            var rectTransform = panel.transform as RectTransform;

            rectTransform.DOKill(true); canvasGroup.DOKill(true);

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            DOTween.Sequence().SetUpdate(true)
                .Append(rectTransform.DOScale(0.85f, panelAnimationDuration * 0.8f).SetEase(Ease.InSine))
                .Join(DOTween.To(() => canvasGroup.alpha, alpha => canvasGroup.alpha = alpha, 0f, panelAnimationDuration * 0.8f))
                .OnComplete(() => panel.SetActive(false));
        }
    }
}
