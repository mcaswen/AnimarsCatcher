using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Audio;
using DG.Tweening;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.UI
{
    public enum AniInfoType
    {
        Picker,
        Blaster
    }

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

        private AniInfoType _aniInfoType = AniInfoType.Picker;

        [SerializeField] private float _panelAnimDuration = 0.25f;

        private void Awake()
        {
            _bigIconPos = PickerAniIcon.GetComponent<RectTransform>().position;
            _smallIconPos = BlasterAniIcon.GetComponent<RectTransform>().position;
            _bigIconSizeDelta = PickerAniIcon.GetComponent<RectTransform>().sizeDelta;
            _smallIconSizeDelta = BlasterAniIcon.GetComponent<RectTransform>().sizeDelta;
        }

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

            PickerAniIcon.onClick.AddListener(() =>
            {
                AniIconBtnClick(PickerAniIcon,BlasterAniIcon);
            });

            BlasterAniIcon.onClick.AddListener(() =>
            {
                AniIconBtnClick(BlasterAniIcon, PickerAniIcon);
            });

            Text_PlayerName.text = PlayerSession.CurrentUserName;
        }

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
            Text_InTeamAniCount.text = playerResourceState.InTeamPickerAniCount.ToString();
            Text_TotalAniCount.text = playerResourceState.TotalPickerAniCount.ToString();
            Text_SelectedAniCount.text = playerResourceState.SelectedPickerAniCount.ToString();

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
            Debug.Log($"[GameMainInterfaceController] Game Time Updated: {Text_GameTime.text}");
        }

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

                    break;
                case AniInfoType.Blaster:
                    _aniInfoType = AniInfoType.Picker;
                    Text_InTeamAniCount.text = playerResourceState.InTeamPickerAniCount.ToString();
                    Text_TotalAniCount.text = playerResourceState.TotalPickerAniCount.ToString();
                    Text_SelectedAniCount.text = playerResourceState.SelectedPickerAniCount.ToString();
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
