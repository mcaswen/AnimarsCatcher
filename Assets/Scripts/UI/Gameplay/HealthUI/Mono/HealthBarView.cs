using AnimarsCatcher.Gameplay.Contracts;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 将目标 ECS 实体的世界位置和生命值投影到屏幕血条
/// </summary>
public class HealthBarView : MonoBehaviour
{
    [Header("UI References")]
    public RectTransform rootRectTransform;
    public Image foregroundImage;
    public Image backgroundImage;

    private EntityManager _entityManager;
    private Entity _targetEntity;

    private Camera _worldCamera;
    private Canvas _canvas;
    private Vector3 _worldOffset;

    private bool _isFriendly;

    /// <summary>
    /// 绑定目标实体和 HUD 投影环境
    /// </summary>
    public void InitializeHealthBar(
        EntityManager entityManager,
        Entity targetEntity,
        Camera worldCamera,
        Canvas canvas,
        Vector3 worldOffset,
        bool isFriendly)
    {
        _entityManager = entityManager;
        _targetEntity  = targetEntity;
        _worldCamera   = worldCamera;
        _canvas        = canvas;
        _worldOffset   = worldOffset;
        _isFriendly    = isFriendly;

        if (foregroundImage != null)
        {
            foregroundImage.color = isFriendly ? Color.green : Color.red;
        }

        Debug.Log($"Initialized HealthBarView for Entity {targetEntity.Index} (IsFriendly: {isFriendly})");
    }

    // 在实体完成移动后更新屏幕位置和生命值填充
    private void LateUpdate()
    {
        if (!_entityManager.Exists(_targetEntity))
        {
            Destroy(gameObject);
            return;
        }

        if (!_entityManager.HasComponent<LocalTransform>(_targetEntity))
        {
            return;
        }

        LocalTransform localTransform = _entityManager.GetComponentData<LocalTransform>(_targetEntity);
        Vector3 worldPosition = localTransform.Position + (float3)_worldOffset;

        Vector3 screenPosition = _worldCamera.WorldToScreenPoint(worldPosition);

        if (!rootRectTransform.gameObject.activeSelf)
        {
            rootRectTransform.gameObject.SetActive(true);
        }

        Vector2 uiPosition;

        if (_canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_canvas.transform,
                screenPosition,
                null,
                out uiPosition
            );
        }
        else
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_canvas.transform,
                screenPosition,
                _canvas.worldCamera,
                out uiPosition
            );
        }

        rootRectTransform.anchoredPosition = uiPosition;

        // 读取最新 Health 并限制填充比例到有效范围
        if (_entityManager.HasComponent<Health>(_targetEntity))
        {
            Health health = _entityManager.GetComponentData<Health>(_targetEntity);

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
