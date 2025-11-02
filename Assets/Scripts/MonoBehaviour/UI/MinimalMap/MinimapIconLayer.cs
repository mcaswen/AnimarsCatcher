using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher.Mono.UI
{
    public class MinimapIconLayer : MonoBehaviour
    {
        [SerializeField] private Camera minimapCamera;
        [SerializeField] public RectTransform mapRect;
        [SerializeField] public RectTransform overlayRect;
        [SerializeField] public Image iconPrefab;

        private readonly List<(MinimapIconTarget target, Image icon)> items = new();

        void Awake()
        {
            RefreshTargets();
        }

        public void RefreshTargets()
        {
            foreach (var pair in items)
                if (pair.icon) Destroy(pair.icon.gameObject);
            items.Clear();

            var all = FindObjectsByType<MinimapIconTarget>(FindObjectsSortMode.None);

            Debug.Log($"MinimapIconLayer found {all.Length} targets.");

            foreach (var t in all)
            {
                var img = Instantiate(iconPrefab, overlayRect);
                img.sprite = t.iconSprite;
                img.color = t.iconColor;
                img.raycastTarget = false;
                img.rectTransform.localScale = Vector3.one;
                img.rectTransform.localRotation = Quaternion.identity;
                items.Add((t, img));
            }
        }

        void LateUpdate()
        {
            if (minimapCamera == null || mapRect == null || overlayRect == null) return;

            var rect = mapRect.rect;

            for (int i = items.Count - 1; i >= 0; i--)
            {
                var (target, icon) = items[i];

                // 清理残留图标
                if (target == null || icon == null)
                {
                    if (icon) Destroy(icon.gameObject);
                    items.RemoveAt(i);
                    continue;
                }

                Vector3 samplePos = target.transform.position + target.worldOffset;
                Vector3 viewPoint = minimapCamera.WorldToViewportPoint(samplePos);

                bool isInFront = viewPoint.z > 0f;
                bool inViewport = viewPoint.x >= 0f && viewPoint.x <= 1f && viewPoint.y >= 0f && viewPoint.y <= 1f;
                bool visible = isInFront && inViewport;

                icon.enabled = visible;
                if (!visible) continue;

                float x = (viewPoint.x - 0.5f) * rect.width;
                float y = (viewPoint.y - 0.5f) * rect.height;
                icon.rectTransform.anchoredPosition = new Vector2(x, y);
                icon.rectTransform.localRotation = Quaternion.identity;
            }
        }
    }
}
