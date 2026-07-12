using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Audio;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }
        private GameModel _gameModel;
        private AniInfoType _aniInfoType = AniInfoType.Picker;
        
        //Text
        public TextMeshProUGUI Text_Day;
        public TextMeshProUGUI Text_LevelTime;
        public TextMeshProUGUI Text_Food;
        public TextMeshProUGUI Text_Crystal;
        public TextMeshProUGUI Text_InTeamAniCount;
        public TextMeshProUGUI Text_OnGroundAniCount;
        public TextMeshProUGUI Text_BlueprintCount;
        public TextMeshProUGUI Text_Selection_SpawningPickerAniCount;
        public TextMeshProUGUI Text_Selection_SpawningBlasterAniCount;
        
        //Button
        public Button RobotIcon;
        public Button Button_ReturnGame;
        public Button Button_AdjustVolume;
        public Button Button_QuitGame;
        public Button Button_VolumeConfirm;
        public Button PickerAniIcon;
        public Button BlasterAniIcon;
        public Button Selection_AddPickerAniButton;
        public Button Selection_AddBlasterAniButton;
        public Button Selection_ConfirmButton;

        //Panel
        public GameObject MenuPanel;
        public GameObject SelectionPanel;
        public GameObject VolumeAdjustPanel;

        private Vector3 _bigIconPosition;
        private Vector3 _smallIconPosition;
        private Vector2 _bigIconSizeDelta;
        private Vector2 _smallIconSizeDelta;

        //Count
        private int _spawningBlasterAniCount = 0;
        private int _spawningPickerAniCount = 0;
        private int _pickerAniFoodCostCount = 0;
        private int _pickerAniCrystalCostCount = 0;
        private int _blasterAniFoodCostCount = 0;
        private int _blasterAniCrystalCostCount = 0;

        [FormerlySerializedAs("_panelAnimDuration")]
        [SerializeField] private float _panelAnimationDuration = 0.25f;

        private void Awake()
        {
            Instance = this;
            _bigIconPosition = PickerAniIcon.GetComponent<RectTransform>().position;
            _smallIconPosition = BlasterAniIcon.GetComponent<RectTransform>().position;
            _bigIconSizeDelta = PickerAniIcon.GetComponent<RectTransform>().sizeDelta;
            _smallIconSizeDelta = BlasterAniIcon.GetComponent<RectTransform>().sizeDelta;
        }

        public void Initialize(GameModel gameModel, ReactiveProperty<int> levelTime, int pickerAniFoodCostCount, int pickerAniCrystalCostCount,
            int blasterAniFoodCostCount, int blasterAniCrystalCostCount)
        {
            _gameModel = gameModel;

            _pickerAniFoodCostCount = pickerAniFoodCostCount;
            _pickerAniCrystalCostCount = pickerAniCrystalCostCount;
            _blasterAniFoodCostCount = blasterAniFoodCostCount;
            _blasterAniCrystalCostCount = blasterAniCrystalCostCount;

            Text_Day.text = gameModel.Day.Value.ToString();
            Text_Food.text = gameModel.FoodSum.Value.ToString();
            Text_Crystal.text = gameModel.CrystalSum.Value.ToString();
            Text_LevelTime.text = levelTime.Value.ToString();
            Text_InTeamAniCount.text = gameModel.InTeamPickerAniCount.Value.ToString();
            Text_OnGroundAniCount.text = (gameModel.PickerAniCount.Value -
                                          gameModel.InTeamPickerAniCount.Value).ToString();
            levelTime.Subscribe(time =>
            {
                Text_LevelTime.text = time.ToString();
            });
            
            RobotIcon.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.EnterMenu();
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimationDuration);
                Time.timeScale = 0;
            });

            Button_ReturnGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.ExitMenu();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimationDuration);
                Time.timeScale = 1;
            });

            Button_AdjustVolume.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(MenuPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(VolumeAdjustPanel, _panelAnimationDuration);
            });
            
            Button_VolumeConfirm.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(VolumeAdjustPanel, _panelAnimationDuration);
                SmoothPanelView.ShowPanel(MenuPanel, _panelAnimationDuration);
            });
            
            Button_QuitGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                Debug.LogWarning("Quit Game");
                Application.Quit();
            });
            
            SubscribeAniInfo(gameModel.PickerAniCount,gameModel.InTeamPickerAniCount,
                AniInfoType.Picker);
            SubscribeAniInfo(gameModel.BlasterAniCount,gameModel.InTeamBlasterAniCount,
                AniInfoType.Blaster);
            
            PickerAniIcon.onClick.AddListener(() =>
            {
                HandleAniIconButtonClick(PickerAniIcon,BlasterAniIcon);
            });

            BlasterAniIcon.onClick.AddListener(() =>
            {
                HandleAniIconButtonClick(BlasterAniIcon, PickerAniIcon);
            });

            Selection_AddPickerAniButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddPickerAni();
            });

            Selection_AddBlasterAniButton.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddBlasterAni();
            });

            Selection_ConfirmButton.onClick.AddListener(() => OnSelectionMenuConfirmed());
        }

        private void Start()
        {
            _gameModel.Day.Subscribe(day =>
            {
                Text_Day.text = day.ToString();
            });
            _gameModel.FoodSum.Subscribe(count =>
            {
                Text_Food.text = count.ToString();
            });
            _gameModel.CrystalSum.Subscribe(count =>
            {
                Text_Crystal.text = count.ToString();
            });

            EventBus.Instance.Subscribe<BlueprintCountUpdatedEventData>(OnBlueprintCountUpdated);
            EventBus.Instance.Subscribe<LevelDayEndedEventData>(OnLevelDayEnded);
        }

        private void OnDestroy()
        {
            EventBus.Instance.Unsubscribe<BlueprintCountUpdatedEventData>(OnBlueprintCountUpdated);
            EventBus.Instance.Unsubscribe<LevelDayEndedEventData>(OnLevelDayEnded);
        }

        private void SubscribeAniInfo(ReactiveProperty<int> sumCount, ReactiveProperty<int> inTeamCount,
            AniInfoType type)
        {
            inTeamCount.Subscribe(count =>
            {
                if (_aniInfoType != type) return;
                Text_InTeamAniCount.text = count.ToString();
                Text_OnGroundAniCount.text = (sumCount.Value - count).ToString();
            });
            sumCount.Subscribe(count =>
            {
                if (_aniInfoType != type) return;
                Text_OnGroundAniCount.text = (count - inTeamCount.Value).ToString();
            });
        }

        private void HandleAniIconButtonClick(Button primaryButton, Button secondaryButton)
        {
            AudioManager.Instance.PlaySwitchButtonAudio();
            switch (_aniInfoType)
            {
                case AniInfoType.Picker:
                    _aniInfoType = AniInfoType.Blaster;
                    Text_InTeamAniCount.text = _gameModel.InTeamBlasterAniCount.Value.ToString();
                    Text_OnGroundAniCount.text = (_gameModel.BlasterAniCount.Value -
                                                  _gameModel.InTeamBlasterAniCount.Value).ToString();
                    break;
                case AniInfoType.Blaster:
                    _aniInfoType = AniInfoType.Picker;
                    Text_InTeamAniCount.text = _gameModel.InTeamPickerAniCount.Value.ToString();
                    Text_OnGroundAniCount.text = (_gameModel.PickerAniCount.Value -
                                                  _gameModel.InTeamPickerAniCount.Value).ToString();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            primaryButton.GetComponent<RectTransform>().DOMove(_smallIconPosition, 0.3f);
            primaryButton.GetComponent<RectTransform>().DOSizeDelta(_smallIconSizeDelta, 0.3f);
            primaryButton.enabled = false;
            secondaryButton.GetComponent<RectTransform>().DOMove(_bigIconPosition, 0.3f);
            secondaryButton.GetComponent<RectTransform>().DOSizeDelta(_bigIconSizeDelta, 0.3f);
            secondaryButton.enabled = true;
        }

        private void OnLevelDayEnded(LevelDayEndedEventData eventData)
        {
            SmoothPanelView.ShowPanel(SelectionPanel, _panelAnimationDuration);
            Time.timeScale = 0;
            _spawningPickerAniCount = 0;
            _spawningBlasterAniCount = 0;
        }

        private void OnBlueprintCountUpdated(BlueprintCountUpdatedEventData eventData)
        {
            Text_BlueprintCount.text = eventData.BlueprintCount.ToString() + "/6";
        }

        private void OnSelectionMenuConfirmed()
        {
            SmoothPanelView.HidePanel(SelectionPanel, _panelAnimationDuration);
            Time.timeScale = 1;
                
            EventBus.Instance.Publish(new LevelDayStartedEventData(_spawningBlasterAniCount,
                _spawningPickerAniCount));
        }

        private void CheckAddPickerAni()
        {
            if ( _gameModel.FoodSum.Value >= _pickerAniFoodCostCount &&
                _gameModel.CrystalSum.Value >= _pickerAniCrystalCostCount)
            {
                _spawningPickerAniCount++;
                Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();
                _gameModel.FoodSum.Value -= _pickerAniFoodCostCount;
                _gameModel.CrystalSum.Value -= _pickerAniCrystalCostCount;
            }
        }

        private void CheckAddBlasterAni()
        {
            if (_gameModel.FoodSum.Value >= _blasterAniFoodCostCount &&
                _gameModel.CrystalSum.Value >= _blasterAniCrystalCostCount)
            {
                _spawningBlasterAniCount++;
                Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();
                _gameModel.FoodSum.Value -= _blasterAniFoodCostCount;
                _gameModel.CrystalSum.Value -= _blasterAniCrystalCostCount;
            }
        }
    }
}
