using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Global;
using AnimarsCatcher.Presentation.Audio;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Presentation.Gameplay;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 管理本地玩家准备生成的 Picker 和 Blaster 数量
    /// 所有增减操作都会先按当前资源快照校验成本
    /// </summary>
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

        // 当前面板中的临时选择值 确认前不会提交到服务端
        private int _spawningBlasterAniCount = 0;
        private int _spawningPickerAniCount = 0;
        [SerializeField] private int _pickerAniFoodCostCount = 2;
        [SerializeField] private int _pickerAniCrystalCostCount = 0;
        [SerializeField] private int _blasterAniFoodCostCount = 2;
        [SerializeField] private int _blasterAniCrystalCostCount = 1;

        [FormerlySerializedAs("_panelAnimDuration")]
        [SerializeField] private float _panelAnimationDuration = 0.25f;

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

                _spawningPickerAniCount = 0;
                _spawningBlasterAniCount = 0;

                Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();
                Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();

                NetworkUIEventBridge.RaiseUIPanelInputUnlocked();
                SmoothPanelView.HidePanel(SelectionPanel, _panelAnimationDuration);
            });
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E))
            {
                SmoothPanelView.ShowPanel(SelectionPanel, _panelAnimationDuration);
                NetworkUIEventBridge.RaiseUIPanelInputLocked();
            }
        }

        // 校验食物和水晶成本后增加 Picker 计划数量
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

                NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetworkUIEventSource.ClientWorld,
                    ResourceItemKind.Food,
                    -_pickerAniFoodCostCount);

                NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                        NetworkUIEventSource.ClientWorld,
                        ResourceItemKind.Crystal,
                        -_pickerAniCrystalCostCount);

            Debug.Log("[AniSelectionPanelController] Added Picker Ani, resource deducted: " +
                           $"Food -{_pickerAniFoodCostCount}, Crystal -{_pickerAniCrystalCostCount}");
            }
        }

        // 在不低于零的前提下减少 Picker 计划数量
        private void CheckDeductPickerAni()
        {
            if (_spawningPickerAniCount <= 0)
                return;

            _spawningPickerAniCount--;
            Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();

            NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                NetworkUIEventSource.ClientWorld,
                ResourceItemKind.Food,
                _pickerAniFoodCostCount);

            NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetworkUIEventSource.ClientWorld,
                    ResourceItemKind.Crystal,
                    _pickerAniCrystalCostCount);

            Debug.Log("[AniSelectionPanelController] Deducted Picker Ani, resource refunded: " +
                           $"Food +{_pickerAniFoodCostCount}, Crystal +{_pickerAniCrystalCostCount}");
        }

        // 校验食物和水晶成本后增加 Blaster 计划数量
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

                NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetworkUIEventSource.ClientWorld,
                    ResourceItemKind.Food,
                    -_blasterAniFoodCostCount);

                NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                        NetworkUIEventSource.ClientWorld,
                        ResourceItemKind.Crystal,
                        -_blasterAniCrystalCostCount);

                Debug.Log("[AniSelectionPanelController] Added Blaster Ani, resource deducted: " +
                           $"Food -{_blasterAniFoodCostCount}, Crystal -{_blasterAniCrystalCostCount}");
            }
        }

        // 在不低于零的前提下减少 Blaster 计划数量
        private void CheckDeductBlasterAni()
        {
            if (_spawningBlasterAniCount <= 0)
                return;

            _spawningBlasterAniCount--;
            Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();

            NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                NetworkUIEventSource.ClientWorld,
                ResourceItemKind.Food,
                _blasterAniFoodCostCount);

            NetworkUIEventBridge.RaiseResourceChangedRequestedEvent(
                    NetworkUIEventSource.ClientWorld,
                    ResourceItemKind.Crystal,
                    _blasterAniCrystalCostCount);

            Debug.Log("[AniSelectionPanelController] Deducted Blaster Ani, resource refunded: " +
                           $"Food +{_blasterAniFoodCostCount}, Crystal +{_blasterAniCrystalCostCount}");
        }

        // 发布最终选择并关闭面板输入锁
        private void OnSelectionMenuConfirmed()
        {
            SmoothPanelView.HidePanel(SelectionPanel, _panelAnimationDuration);
            NetworkUIEventBridge.RaiseUIPanelInputUnlocked();

            AniSpawnRequestSender.RequestSpawnAnis(_spawningBlasterAniCount, _spawningPickerAniCount);

            _spawningPickerAniCount = 0;
            _spawningBlasterAniCount = 0;

            Text_Selection_SpawningBlasterAniCount.text = _spawningBlasterAniCount.ToString();
            Text_Selection_SpawningPickerAniCount.text = _spawningPickerAniCount.ToString();
        }
    }
}
