using TMPro;
using UnityEngine;
using DG.Tweening;

/// <summary>
/// 显示单条上浮并淡出的即时反馈消息
/// 新消息会终止并替换尚未完成的动画
/// </summary>
public class FloatingMessageView : MonoBehaviour
{
    [SerializeField] public TMP_Text MessageText;
    [SerializeField] private CanvasGroup _canvasGroup;

    [Header("动画参数")]
    [Tooltip("RectTransform 锚点坐标中的上移距离")]
    [SerializeField] private float _moveDistance = 30f;

    [Tooltip("上移动画时长，单位秒")]
    [SerializeField] private float _moveDuration = 0.6f;

    [Tooltip("上移和淡出前的停留时长，单位秒")]
    [SerializeField] private float _holdDuration = 0.4f;

    [Tooltip("淡出动画时长，单位秒")]
    [SerializeField] private float _fadeDuration = 0.6f;

    private RectTransform _rectTransform;
    private Vector2 _originalAnchoredPosition;
    private Tween _activeTween;

    // 缓存必要组件并记录每次动画复位使用的初始位置
    private void Awake()
    {
        _rectTransform = GetComponent<RectTransform>();

        if (MessageText == null)
        {
            MessageText = GetComponentInChildren<TMP_Text>();
        }

        if (_canvasGroup == null)
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        _originalAnchoredPosition = _rectTransform.anchoredPosition;
        _canvasGroup.alpha = 0f;

    }


    /// <summary>
    /// 使用指定文本重新开始消息动画
    /// </summary>
    /// <param name="message">需要显示的反馈内容</param>
    public void ShowMessage(string message)
    {
        if (MessageText == null)
        {
            return;
        }

        // 终止旧序列以避免多个 Tween 同时修改位置和透明度
        if (_activeTween != null && _activeTween.IsActive())
        {
            _activeTween.Kill();
        }

        // 每次播放前恢复一致的初始视觉状态
        _rectTransform.anchoredPosition = _originalAnchoredPosition;
        _canvasGroup.alpha = 1f;
        MessageText.text = message;

        // 停留结束后同时执行上移和淡出
        var sequence = DOTween.Sequence();

        if (_holdDuration > 0f)
        {
            sequence.AppendInterval(_holdDuration);
        }

        sequence.Append(_rectTransform.DOAnchorPosY(
            _originalAnchoredPosition.y + _moveDistance,
            _moveDuration
        ));

        sequence.Join(_canvasGroup.DOFade(0f, _fadeDuration));

        sequence.OnComplete(() =>
        {
            _canvasGroup.alpha = 0f;
            _rectTransform.anchoredPosition = _originalAnchoredPosition;
        });

        _activeTween = sequence;
    }

    // 对象销毁时终止 Tween 防止完成回调访问失效组件
    private void OnDestroy()
    {
        if (_activeTween != null && _activeTween.IsActive())
        {
            _activeTween.Kill();
        }
    }
}
