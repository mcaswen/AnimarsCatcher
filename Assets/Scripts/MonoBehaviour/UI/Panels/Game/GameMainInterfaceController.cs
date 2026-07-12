using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Audio;
using DG.Tweening;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.UI
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

        private Vector3 _bigIconPos;
        private Vector3 _smallIconPos;
        private Vector2 _bigIconSizeDelta;
        private Vector2 _smallIconSizeDelta;

        [SerializeField] private AniInfoType _aniInfoType = AniInfoType.Picker;

        [SerializeField] private float _panelAnimDuration = 0.25f;

        // 缓存两种图标布局尺寸供页签切换复用
        private void Awake()
        {
            _bigIconPos = PickerAniIcon.GetComponent<RectTransform>().position;
            _smallIconPos = BlasterAniIcon.GetComponent<RectTransform>().position;
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
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimDuration);
                NetUIEventBridge.RaiseUIPanelInputLocked();
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
                    AniIconBtnClick(PickerAniIcon, BlasterAniIcon);
                }
                else if (_aniInfoType == AniInfoType.Blaster)
                {
                    AniIconBtnClick(BlasterAniIcon, PickerAniIcon);
                }
                
            }

        }

        // 交换主次图标的尺寸和位置并更新当前信息类别
        private void AniIconBtnClick(Button button1, Button button2)
        {
            AudioManager.Instance.PlaySwitchBtnAudio();
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
                    NetUIEventBridge.RaiseAniSelectionModeChanged(AniSelectionMode.Blaster);
                    Debug.Log("[GameMainInterfaceController] Switched to Blaster Mode");

                    break;
                case AniInfoType.Blaster:
                    _aniInfoType = AniInfoType.Picker;
                    Text_InTeamAniCount.text = playerResourceState.InTeamPickerAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalPickerAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedPickerAniCount.ToString();
                    NetUIEventBridge.RaiseAniSelectionModeChanged(AniSelectionMode.Picker);
                    Debug.Log("[GameMainInterfaceController] Switched to Picker Mode");
                    break;
                default:
                    break;
            }

            button1.GetComponent<RectTransform>().DOMove(_smallIconPos, 0.3f);
            button1.GetComponent<RectTransform>().DOSizeDelta(_smallIconSizeDelta, 0.3f);
            button1.enabled = false;
            button2.GetComponent<RectTransform>().DOMove(_bigIconPos, 0.3f);
            button2.GetComponent<RectTransform>().DOSizeDelta(_bigIconSizeDelta, 0.3f);
            button2.enabled = true;
        }

    }
}
