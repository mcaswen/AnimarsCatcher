using TMPro;
using UnityEngine;
using DG.Tweening;

public class FloatingMessageView : MonoBehaviour
{
    [SerializeField] public TMP_Text MessageText;
    [SerializeField] private CanvasGroup _canvasGroup;

    [Header("动画参数")]
    [Tooltip("向上移动的距离（UI 坐标）")]
    [SerializeField] private float _moveDistance = 30f;

    [Tooltip("向上移动的时长")]
    [SerializeField] private float _moveDuration = 0.6f;

    [Tooltip("开始移动前静止显示的时间")]
    [SerializeField] private float _holdDuration = 0.4f;

    [Tooltip("淡出时长")]
    [SerializeField] private float _fadeDuration = 0.6f;

    private RectTransform _rectTransform;
    private Vector2 _originalAnchoredPosition;
    private Tween _activeTween;

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


    public void ShowMessage(string message)
    {
        if (MessageText == null)
        {
            return;
        }

        // 停掉上一条动画
        if (_activeTween != null && _activeTween.IsActive())
        {
            _activeTween.Kill();
        }

        // 重置状态
        _rectTransform.anchoredPosition = _originalAnchoredPosition;
        _canvasGroup.alpha = 1f;
        MessageText.text = message;

        // 组合序列：停顿 -> 向上移动 + 淡出
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

    private void OnDestroy()
    {
        if (_activeTween != null && _activeTween.IsActive())
        {
            _activeTween.Kill();
        }
    }
}
