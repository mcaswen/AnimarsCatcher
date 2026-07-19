using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 将场景目标投影到小地图覆盖层并维护对应图标
    /// </summary>
    public class MinimapIconLayer : MonoBehaviour
    {
        [FormerlySerializedAs("minimapCamera")]
        [SerializeField] private Camera _minimapCamera;
        [FormerlySerializedAs("mapRect")]
        [SerializeField] private RectTransform _mapRect;
        [FormerlySerializedAs("overlayRect")]
        [SerializeField] private RectTransform _overlayRect;
        [FormerlySerializedAs("iconPrefab")]
        [SerializeField] private Image _iconPrefab;

        private readonly List<(MinimapIconTarget target, Image icon)> _items = new();

        private void Awake()
        {
            RefreshTargets();
        }

        /// <summary>
        /// 重新扫描场景目标并重建图标实例
        /// </summary>
        public void RefreshTargets()
        {
            foreach (var pair in _items)
                if (pair.icon) Destroy(pair.icon.gameObject);
            _items.Clear();

            var all = FindObjectsByType<MinimapIconTarget>(FindObjectsSortMode.None);

            Debug.Log($"MinimapIconLayer found {all.Length} targets.");

            foreach (var target in all)
            {
                var img = Instantiate(_iconPrefab, _overlayRect);
                img.sprite = target.iconSprite;
                img.color = target.iconColor;
                img.raycastTarget = false;
                img.rectTransform.localScale = Vector3.one;
                img.rectTransform.localRotation = Quaternion.identity;
                _items.Add((target, img));
            }
        }

        // 在目标移动结束后更新图标可见性和局部坐标
        private void LateUpdate()
        {
            if (_minimapCamera == null || _mapRect == null || _overlayRect == null) return;

            var rect = _mapRect.rect;

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var (target, icon) = _items[i];

                // 目标或图标销毁后同步移除配对记录
                if (target == null || icon == null)
                {
                    if (icon) Destroy(icon.gameObject);
                    _items.RemoveAt(i);
                    continue;
                }

                Vector3 samplePos = target.transform.position + target.worldOffset;
                Vector3 viewPoint = _minimapCamera.WorldToViewportPoint(samplePos);

                // 只显示位于相机前方且处于视口范围内的目标
                bool isInFront = viewPoint.z > 0f;
                bool inViewport = viewPoint.x >= 0f && viewPoint.x <= 1f && viewPoint.y >= 0f && viewPoint.y <= 1f;
                bool visible = isInFront && inViewport;

                icon.enabled = visible;
                if (!visible) continue;

                // 将归一化视口坐标转换为地图 RectTransform 局部坐标
                float x = (viewPoint.x - 0.5f) * rect.width;
                float y = (viewPoint.y - 0.5f) * rect.height;
                icon.rectTransform.anchoredPosition = new Vector2(x, y);
                icon.rectTransform.localRotation = Quaternion.identity;
            }
        }
    }
}
