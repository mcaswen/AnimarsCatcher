using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Audio;
using DG.Tweening;
using AnimarsCatcher.Presentation.Account;
using AnimarsCatcher.Presentation.InputLock;
using AnimarsCatcher.Presentation.Selection;
using AnimarsCatcher.Presentation.Resource;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 主界面当前展示的 Ani 信息类别
    /// </summary>
    public enum AniInfoType
    {
        Picker,
        Blaster
    }

    /// <summary>
    /// 将本地玩家资源和全局比赛状态显示在游戏主界面
    /// 同时负责 Picker 与 Blaster 信息页签的视觉切换
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.UI", "AnimarsCatcher.Presentation", "GameMainInterfaceController")]
    public class GameplayHudController : MonoBehaviour
    {
        [FormerlySerializedAs("Text_GameTime")]
        [SerializeField] private TextMeshProUGUI _gameTimeText;
        [FormerlySerializedAs("Text_Food")]
        [SerializeField] private TextMeshProUGUI _foodText;
        [FormerlySerializedAs("Text_Crystal")]
        [SerializeField] private TextMeshProUGUI _crystalText;
        [FormerlySerializedAs("Text_InTeamAniCount")]
        [SerializeField] private TextMeshProUGUI _inTeamAniCountText;
        [FormerlySerializedAs("Text_TotalAniCount")]
        [SerializeField] private TextMeshProUGUI _totalAniCountText;
        [FormerlySerializedAs("Text_SelectedAniCount")]
        [SerializeField] private TextMeshProUGUI _selectedAniCountText;
        [FormerlySerializedAs("Text_PlayerName")]
        [SerializeField] private TextMeshProUGUI _playerNameText;

        [FormerlySerializedAs("RobotIcon")]
        [SerializeField] private Button _robotIconButton;
        [FormerlySerializedAs("PickerAniIcon")]
        [SerializeField] private Button _pickerAniIconButton;
        [FormerlySerializedAs("BlasterAniIcon")]
        [SerializeField] private Button _blasterAniIconButton;

        [FormerlySerializedAs("MenuPanel")]
        [SerializeField] private GameObject _menuPanel;

        private Vector3 _bigIconPosition;
        private Vector3 _smallIconPosition;
        private Vector2 _bigIconSizeDelta;
        private Vector2 _smallIconSizeDelta;

        [SerializeField] private AniInfoType _aniInfoType = AniInfoType.Picker;

        [FormerlySerializedAs("_panelAnimDuration")]
        [SerializeField] private float _panelAnimationDuration = 0.25f;

        // 缓存两种图标布局尺寸供页签切换复用
        private void Awake()
        {
            _bigIconPosition = _pickerAniIconButton.GetComponent<RectTransform>().position;
            _smallIconPosition = _blasterAniIconButton.GetComponent<RectTransform>().position;
            _bigIconSizeDelta = _pickerAniIconButton.GetComponent<RectTransform>().sizeDelta;
            _smallIconSizeDelta = _blasterAniIconButton.GetComponent<RectTransform>().sizeDelta;
        }

        // 绑定菜单入口和两类 Ani 图标按钮
        void Start()
        {
            _robotIconButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.EnterMenu();
                SmoothPanelView.ShowPanel(_menuPanel, _panelAnimationDuration);
                UIInputEvents.RaiseLocked();
                Time.timeScale = 0;
            });

            _playerNameText.text = PlayerSession.CurrentUserName;
        }

        // 从 ECS 世界读取只读快照并刷新本帧 HUD
        void Update()
        {
            bool success = ResourceStateReader.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogWarning("[GameplayHudController] Failed to get local player resource state");
                return;
            }

            _foodText.text = playerResourceState.FoodAmount.ToString();
            _crystalText.text = playerResourceState.CrystalAmount.ToString();

            switch (_aniInfoType)
            {
                case AniInfoType.Picker:
                    _inTeamAniCountText.text = playerResourceState.InTeamPickerAniCount.ToString();
                    _totalAniCountText.text = playerResourceState.TotalPickerAniCount.ToString();
                    _selectedAniCountText.text = playerResourceState.SelectedPickerAniCount.ToString();
                    break;
                case AniInfoType.Blaster:
                    _inTeamAniCountText.text = playerResourceState.InTeamBlasterAniCount.ToString();
                    _totalAniCountText.text = playerResourceState.TotalBlasterAniCount.ToString();
                    _selectedAniCountText.text = playerResourceState.SelectedBlasterAniCount.ToString();
                    break;
                default:
                    break;
            }
            bool successTime = ResourceStateReader.TryGetGlobalGameResourceState(out var globalGameResourceState);
            if (!successTime)
            {
                Debug.LogWarning("[GameplayHudController] Failed to get global game resource state");
                return;
            }

            int matchTimeSeconds = globalGameResourceState.MatchTimeSeconds;
            int minutes = matchTimeSeconds / 60;
            int seconds = matchTimeSeconds % 60;
            _gameTimeText.text = $"{minutes:D2}:{seconds:D2}";

            if (Input.GetKeyDown(KeyCode.LeftShift))
            {
                if (_aniInfoType == AniInfoType.Picker)
                {
                    HandleAniIconButtonClick(_pickerAniIconButton, _blasterAniIconButton);
                }
                else if (_aniInfoType == AniInfoType.Blaster)
                {
                    HandleAniIconButtonClick(_blasterAniIconButton, _pickerAniIconButton);
                }

            }

        }

        // 交换主次图标的尺寸和位置并更新当前信息类别
        private void HandleAniIconButtonClick(Button primaryButton, Button secondaryButton)
        {
            AudioManager.Instance.PlaySwitchButtonAudio();
            bool success = ResourceStateReader.TryGetLocalPlayerResourceState(out var playerResourceState);

            if (!success)
            {
                Debug.LogError("[GameplayHudController] Failed to get local player resource state");
                return;
            }

            switch (_aniInfoType)
            {
                case AniInfoType.Picker:
                    _aniInfoType = AniInfoType.Blaster;
                    _inTeamAniCountText.text = playerResourceState.InTeamBlasterAniCount.ToString();
                    _totalAniCountText.text = playerResourceState.TotalBlasterAniCount.ToString();
                    _selectedAniCountText.text = playerResourceState.SelectedBlasterAniCount.ToString();
                    AniSelectionEvents.RaiseModeChanged(AniSelectionMode.Blaster);
                    Debug.Log("[GameplayHudController] Switched to Blaster Mode");

                    break;
                case AniInfoType.Blaster:
                    _aniInfoType = AniInfoType.Picker;
                    _inTeamAniCountText.text = playerResourceState.InTeamPickerAniCount.ToString();
                    _totalAniCountText.text = playerResourceState.TotalPickerAniCount.ToString();
                    _selectedAniCountText.text = playerResourceState.SelectedPickerAniCount.ToString();
                    AniSelectionEvents.RaiseModeChanged(AniSelectionMode.Picker);
                    Debug.Log("[GameplayHudController] Switched to Picker Mode");
                    break;
                default:
                    break;
            }

            primaryButton.GetComponent<RectTransform>().DOMove(_smallIconPosition, 0.3f);
            primaryButton.GetComponent<RectTransform>().DOSizeDelta(_smallIconSizeDelta, 0.3f);
            primaryButton.enabled = false;
            secondaryButton.GetComponent<RectTransform>().DOMove(_bigIconPosition, 0.3f);
            secondaryButton.GetComponent<RectTransform>().DOSizeDelta(_bigIconSizeDelta, 0.3f);
            secondaryButton.enabled = true;
        }

    }
}
