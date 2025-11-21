using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

public class HealthBarView : MonoBehaviour
{
    [Header("UI References")]
    public RectTransform rootRectTransform;
    public Image foregroundImage;
    public Image backgroundImage;

    private EntityManager entityManager;
    private Entity targetEntity;

    private Camera worldCamera;
    private Canvas canvas;
    private Vector3 worldOffset;

    private bool isFriendly;

    public void InitializeHealthBar(
        EntityManager entityManager,
        Entity targetEntity,
        Camera worldCamera,
        Canvas canvas,
        Vector3 worldOffset,
        bool isFriendly)
    {
        this.entityManager = entityManager;
        this.targetEntity  = targetEntity;
        this.worldCamera   = worldCamera;
        this.canvas        = canvas;
        this.worldOffset   = worldOffset;
        this.isFriendly    = isFriendly;

        if (foregroundImage != null)
        {
            foregroundImage.color = isFriendly ? Color.green : Color.red;
        }

        Debug.Log($"Initialized HealthBarView for Entity {targetEntity.Index} (IsFriendly: {isFriendly})");
    }

    private void LateUpdate()
    {
        if (!entityManager.Exists(targetEntity))
        {
            Destroy(gameObject);
            return;
        }

        if (!entityManager.HasComponent<LocalTransform>(targetEntity))
        {
            return;
        }

        LocalTransform localTransform = entityManager.GetComponentData<LocalTransform>(targetEntity);
        Vector3 worldPosition = localTransform.Position + (float3)worldOffset;

        Vector3 screenPosition = worldCamera.WorldToScreenPoint(worldPosition);

        // 背面朝向相机的时候直接隐藏
        if (screenPosition.z < 0f)
        {
            if (rootRectTransform.gameObject.activeSelf)
            {
                rootRectTransform.gameObject.SetActive(false);
            }

            return;
        }

        if (!rootRectTransform.gameObject.activeSelf)
        {
            rootRectTransform.gameObject.SetActive(true);
        }

        Vector2 uiPosition;

        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)canvas.transform,
                screenPosition,
                null,
                out uiPosition
            );
        }
        else
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)canvas.transform,
                screenPosition,
                canvas.worldCamera,
                out uiPosition
            );
        }

        rootRectTransform.anchoredPosition = uiPosition;

        // 更新血量
        if (entityManager.HasComponent<Health>(targetEntity))
        {
            Health health = entityManager.GetComponentData<Health>(targetEntity);

            float healthPercent = 0f;

            if (health.max > 0)
            {
                healthPercent = math.clamp((float)health.current / (float)health.max, 0f, 1f);
            }

            if (foregroundImage != null)
            {
                foregroundImage.fillAmount = healthPercent;
            }
        }
    }
}
