using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Audio;
using AnimarsCatcher.Mono.Utilities;

namespace AnimarsCatcher.Mono.UI
{
    public class AniSelectionPanelController : MonoBehaviour
    {
        public TextMeshProUGUI Text_Selection_SpawningPickerAniCount;
        public TextMeshProUGUI Text_Selection_SpawningBlasterAniCount;

        public Button Selection_AddPickerAniButton;
        public Button Selection_DeductPickerAniButton;

        public Button Selection_AddBlasterAniButton;
        public Button Selection_DeductBlasterAniButton;

        public Button Selection_ConfirmButton;
        public Button Selection_ReturnButton;

        public GameObject SelectionPanel;

        //Count
        private int _spawningBlasterAniCount = 0;
        private int _spawningPickerAniCount = 0;
        [SerializeField] private int _pickerAniFoodCostCount = 2;
        [SerializeField] private int _pickerAniCrystalCostCount = 0;
        [SerializeField] private int _blasterAniFoodCostCount = 2;
        [SerializeField] private int _blasterAniCrystalCostCount = 1;

        [SerializeField] private float _panelAnimDuration = 0.25f;

        private void Awake()
        {
            SelectionPanel?.SetActive(false);

            Selection_AddPickerAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddPickerAni();
            });

            Selection_DeductPickerAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckDeductPickerAni();
            });

            Selection_AddBlasterAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddBlasterAni();
            });

            Selection_DeductBlasterAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckDeductBlasterAni();
            });

            Selection_ConfirmButton?.onClick.AddListener(() => OnSelectionMenuConfirmed());
            Selection_ReturnButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                SmoothPanelView.HidePanel(SelectionPanel, _panelAnimDuration);
            });
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E))
            {
                SmoothPanelView.ShowPanel(SelectionPanel, _panelAnimDuration);
            }
        }

        private void CheckAddPickerAni()
        {
            var success = GameResourceGetter.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogError("[AniSelectionPanelController] Failed to get local player resource state.");
                return;
            }
            int foodSum = playerResourceState.FoodSum;
            int crystalSum = playerResourceState.CrystalSum;

            if (foodSum >= _pickerAniFoodCostCount &&
                crystalSum >= _pickerAniCrystalCostCount)
            {
                _spawningPickerAniCount++;
                Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();

                NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetUIEventSource.ClientWorld,
                    ResourceType.Food,
                    -_pickerAniFoodCostCount);
            
                NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                        NetUIEventSource.ClientWorld,
                        ResourceType.Crystal,
                        -_pickerAniCrystalCostCount);
            }
        }

        private void CheckDeductPickerAni()
        {
            if (_spawningPickerAniCount <= 0)
                return;

            _spawningPickerAniCount--;
            Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();

            NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                NetUIEventSource.ClientWorld,
                ResourceType.Food,
                _pickerAniFoodCostCount);
            
            NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetUIEventSource.ClientWorld,
                    ResourceType.Crystal,
                    _pickerAniCrystalCostCount);
        }

        private void CheckAddBlasterAni()
        {
            var success = GameResourceGetter.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogError("[AniSelectionPanelController] Failed to get local player resource state.");
                return;
            }

            int foodSum = playerResourceState.FoodSum;
            int crystalSum = playerResourceState.CrystalSum;

            if (foodSum >= _blasterAniFoodCostCount &&
                crystalSum >= _blasterAniCrystalCostCount)
            {
                _spawningBlasterAniCount++;
                Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();

                NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetUIEventSource.ClientWorld,
                    ResourceType.Food,
                    -_blasterAniFoodCostCount);
            
                NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                        NetUIEventSource.ClientWorld,
                        ResourceType.Crystal,
                        -_blasterAniCrystalCostCount);
            }
        }

        private void CheckDeductBlasterAni()
        {
            if (_spawningBlasterAniCount <= 0)
                return;

            _spawningBlasterAniCount--;
            Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();

            NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                NetUIEventSource.ClientWorld,
                ResourceType.Food,
                _blasterAniFoodCostCount);
            
            NetUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetUIEventSource.ClientWorld,
                    ResourceType.Crystal,
                    _blasterAniCrystalCostCount);
        }

        private void OnSelectionMenuConfirmed()
        {
            SmoothPanelView.HidePanel(SelectionPanel, _panelAnimDuration);
            AniSpawnRequestSender.RequestSpawnAnis(_spawningPickerAniCount, _spawningBlasterAniCount);
        }
    }   
}