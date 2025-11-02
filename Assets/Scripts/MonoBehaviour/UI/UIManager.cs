using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Audio;

namespace AnimarsCatcher.Mono.UI
{
    public enum AniInfoType
    {
        Picker,
        Blaster
    }

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

        private Vector3 _bigIconPos;
        private Vector3 _smallIconPos;
        private Vector2 _bigIconSizeDelta;
        private Vector2 _smallIconSizeDelta;

        //Count
        private int _spawningBlasterAniCount = 0;
        private int _spawningPickerAniCount = 0;
        private int _pickerAniFoodCostCount = 0;
        private int _pickerAniCrystalCostCount = 0;
        private int _blasterAniFoodCostCount = 0;
        private int _blasterAniCrystalCostCount = 0;

        [SerializeField] private float panelAnimDuration = 0.25f;

        private void Awake()
        {
            Instance = this;
            _bigIconPos = PickerAniIcon.GetComponent<RectTransform>().position;
            _smallIconPos = BlasterAniIcon.GetComponent<RectTransform>().position;
            _bigIconSizeDelta = PickerAniIcon.GetComponent<RectTransform>().sizeDelta;
            _smallIconSizeDelta = BlasterAniIcon.GetComponent<RectTransform>().sizeDelta;
        }

        public void Init(GameModel gameModel, ReactiveProperty<int> levelTime, int pickerAniFoodCostCount, int pickerAniCrystalCostCount,
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
                ShowPanel(MenuPanel);
                Time.timeScale = 0;
            });

            Button_ReturnGame.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                AudioManager.Instance.ExitMenu();
                HidePanel(MenuPanel);
                Time.timeScale = 1;
            });

            Button_AdjustVolume.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                HidePanel(MenuPanel);
                ShowPanel(VolumeAdjustPanel);
            });
            
            Button_VolumeConfirm.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                HidePanel(VolumeAdjustPanel);
                ShowPanel(MenuPanel);
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
                AniIconBtnClick(PickerAniIcon,BlasterAniIcon);
            });

            BlasterAniIcon.onClick.AddListener(() =>
            {
                AniIconBtnClick(BlasterAniIcon, PickerAniIcon);
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

            EventBus.Instance.Subscribe<BlueprintCountUpdatedEventData>(eventData =>
            {
                Text_BlueprintCount.text = eventData.BlueprintCount.ToString() + "/6";
            });

            EventBus.Instance.Subscribe<LevelDayEndedEventData>(eventData => OnLevelDayEnded(eventData));
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

        private void AniIconBtnClick(Button button1, Button button2)
        {
            AudioManager.Instance.PlaySwitchBtnAudio();
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

            button1.GetComponent<RectTransform>().DOMove(_smallIconPos, 0.3f);
            button1.GetComponent<RectTransform>().DOSizeDelta(_smallIconSizeDelta, 0.3f);
            button1.enabled = false;
            button2.GetComponent<RectTransform>().DOMove(_bigIconPos, 0.3f);
            button2.GetComponent<RectTransform>().DOSizeDelta(_bigIconSizeDelta, 0.3f);
            button2.enabled = true;
        }

        private void OnLevelDayEnded(LevelDayEndedEventData eventData)
        {
            ShowPanel(SelectionPanel);
            Time.timeScale = 0;
            _spawningPickerAniCount = 0;
            _spawningBlasterAniCount = 0;
        }

        private void OnSelectionMenuConfirmed()
        {
            AudioManager.Instance.PlayMenuButtonAudio();
            HidePanel(SelectionPanel);
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

        private CanvasGroup GetOrAddCanvasGroup(GameObject panel)
        {
            var cg = panel.GetComponent<CanvasGroup>();
            if (!cg) cg = panel.AddComponent<CanvasGroup>();
            return cg;
        }

        private void ShowPanel(GameObject panel)
        {
            var canvasGroup = GetOrAddCanvasGroup(panel);
            var rectTransform = panel.transform as RectTransform;

            rectTransform.DOKill(true); canvasGroup.DOKill(true);

            panel.SetActive(true);
            rectTransform.localScale = Vector3.one * 0.8f;
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            DOTween.Sequence().SetUpdate(true)
                .Append(rectTransform.DOScale(1f, panelAnimDuration).SetEase(Ease.OutBack))
                .Join(DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 1f, panelAnimDuration))
                .OnComplete(() =>
                {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                });
        }

        private void HidePanel(GameObject panel)
        {
            var canvasGroup = GetOrAddCanvasGroup(panel);
            var rectTransform = panel.transform as RectTransform;

            rectTransform.DOKill(true); canvasGroup.DOKill(true);

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            DOTween.Sequence().SetUpdate(true)
                .Append(rectTransform.DOScale(0.85f, panelAnimDuration * 0.8f).SetEase(Ease.InSine))
                .Join(DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 0f, panelAnimDuration * 0.8f))
                .OnComplete(() => panel.SetActive(false));
        }
    }
}