using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Audio;
using DG.Tweening;
using AnimarsCatcher.Presentation.Global;
using AnimarsCatcher.Presentation.Account;
using AnimarsCatcher.Presentation.Selection;
using AnimarsCatcher.Presentation.Resource;

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
    public class GameMainInterfaceController : MonoBehaviour
    {
        public TextMeshProUGUI Text_GameTime;
        public TextMeshProUGUI Text_Food;
        public TextMeshProUGUI Text_Crystal;
        public TextMeshProUGUI Text_InTeamAniCount;
        public TextMeshProUGUI Text_TotalAniCount;
        public TextMeshProUGUI Text_SelectedAniCount;
        public TextMeshProUGUI Text_PlayerName;

        public Button RobotIcon;
        public Button PickerAniIcon;
        public Button BlasterAniIcon;

        public GameObject MenuPanel;

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
            _bigIconPosition = PickerAniIcon.GetComponent<RectTransform>().position;
            _smallIconPosition = BlasterAniIcon.GetComponent<RectTransform>().position;
            _bigIconSizeDelta = PickerAniIcon.GetComponent<RectTransform>().sizeDelta;
            _smallIconSizeDelta = BlasterAniIcon.GetComponent<RectTransform>().sizeDelta;
        }

        // 绑定菜单入口和两类 Ani 图标按钮
        void Start()
        {
            RobotIcon.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.EnterMenu();
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimationDuration);
                NetworkUIEventBridge.RaiseUIPanelInputLocked();
                Time.timeScale = 0;
            });

            Text_PlayerName.text = PlayerSession.CurrentUserName;
        }

        // 从 ECS 世界读取只读快照并刷新本帧 HUD
        void Update()
        {
            bool success = GameResourceGetter.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogWarning("[GameMenuInterfaceController] Failed to get local player resource state.");
                return;
            }

            Text_Food.text = playerResourceState.FoodSum.ToString();
            Text_Crystal.text = playerResourceState.CrystalSum.ToString();

            switch (_aniInfoType)
            {
                case AniInfoType.Picker:
                    Text_InTeamAniCount.text = playerResourceState.InTeamPickerAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalPickerAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedPickerAniCount.ToString();
                    break;
                case AniInfoType.Blaster:
                    Text_InTeamAniCount.text = playerResourceState.InTeamBlasterAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalBlasterAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedBlasterAniCount.ToString();
                    break;
                default:
                    break;
            }
            bool successTime = GameResourceGetter.TryGlobalGameResourceState(out var globalGameResourceState);
            if (!successTime)
            {
                Debug.LogWarning("[GameMenuInterfaceController] Failed to get global game resource state.");
                return;
            }

            int matchTimeSeconds = globalGameResourceState.MatchTimeSeconds;
            int minutes = matchTimeSeconds / 60;
            int seconds = matchTimeSeconds % 60;
            Text_GameTime.text = $"{minutes:D2}:{seconds:D2}";

            if (Input.GetKeyDown(KeyCode.LeftShift))
            {
                if (_aniInfoType == AniInfoType.Picker)
                {
                    HandleAniIconButtonClick(PickerAniIcon, BlasterAniIcon);
                }
                else if (_aniInfoType == AniInfoType.Blaster)
                {
                    HandleAniIconButtonClick(BlasterAniIcon, PickerAniIcon);
                }

            }

        }

        // 交换主次图标的尺寸和位置并更新当前信息类别
        private void HandleAniIconButtonClick(Button primaryButton, Button secondaryButton)
        {
            AudioManager.Instance.PlaySwitchButtonAudio();
            bool success = GameResourceGetter.TryGetLocalPlayerResourceState(out var playerResourceState);

            if (!success)
            {
                Debug.LogError("[GameMenuInterfaceController] Failed to get local player resource state.");
                return;
            }

            switch (_aniInfoType)
            {
                case AniInfoType.Picker:
                    _aniInfoType = AniInfoType.Blaster;
                    Text_InTeamAniCount.text = playerResourceState.InTeamBlasterAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalBlasterAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedBlasterAniCount.ToString();
                    NetworkUIEventBridge.RaiseAniSelectionModeChanged(AniSelectionMode.Blaster);
                    Debug.Log("[GameMainInterfaceController] Switched to Blaster Mode");

                    break;
                case AniInfoType.Blaster:
                    _aniInfoType = AniInfoType.Picker;
                    Text_InTeamAniCount.text = playerResourceState.InTeamPickerAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalPickerAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedPickerAniCount.ToString();
                    NetworkUIEventBridge.RaiseAniSelectionModeChanged(AniSelectionMode.Picker);
                    Debug.Log("[GameMainInterfaceController] Switched to Picker Mode");
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
