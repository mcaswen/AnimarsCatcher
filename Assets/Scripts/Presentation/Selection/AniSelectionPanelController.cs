using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using AnimarsCatcher.Presentation.Audio;
using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Presentation.Anis;
using AnimarsCatcher.Presentation.InputLock;
using AnimarsCatcher.Presentation.Resource;
using AnimarsCatcher.Presentation.UI;
using UnityEngine.Scripting.APIUpdating;

namespace AnimarsCatcher.Presentation.Selection
{
    /// <summary>
    /// 管理本地玩家准备生成的 Picker 和 Blaster 数量
    /// 所有增减操作都会先按当前资源快照校验成本
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.UI", "AnimarsCatcher.Presentation", "AniSelectionPanelController")]
    public class AniSelectionPanelController : MonoBehaviour
    {
        [FormerlySerializedAs("Text_Selection_SpawningPickerAniCount")]
        [SerializeField] private TextMeshProUGUI _spawningPickerAniCountText;
        [FormerlySerializedAs("Text_Selection_SpawningBlasterAniCount")]
        [SerializeField] private TextMeshProUGUI _spawningBlasterAniCountText;

        [FormerlySerializedAs("Selection_AddPickerAniButton")]
        [SerializeField] private Button _addPickerAniButton;
        [FormerlySerializedAs("Selection_DeductPickerAniButton")]
        [SerializeField] private Button _deductPickerAniButton;

        [FormerlySerializedAs("Selection_AddBlasterAniButton")]
        [SerializeField] private Button _addBlasterAniButton;
        [FormerlySerializedAs("Selection_DeductBlasterAniButton")]
        [SerializeField] private Button _deductBlasterAniButton;

        [FormerlySerializedAs("Selection_ConfirmButton")]
        [SerializeField] private Button _confirmButton;
        [FormerlySerializedAs("Selection_ReturnButton")]
        [SerializeField] private Button _returnButton;

        [FormerlySerializedAs("SelectionPanel")]
        [SerializeField] private GameObject _selectionPanel;

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
            _selectionPanel?.SetActive(false);

            _addPickerAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddPickerAni();
            });

            _deductPickerAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckDeductPickerAni();
            });

            _addBlasterAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckAddBlasterAni();
            });

            _deductBlasterAniButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();
                CheckDeductBlasterAni();
            });

            _confirmButton?.onClick.AddListener(OnSelectionMenuConfirmed);
            _returnButton?.onClick.AddListener(() =>
            {
                AudioManager.Instance.PlayMenuButtonAudio();

                _spawningPickerAniCount = 0;
                _spawningBlasterAniCount = 0;

                _spawningBlasterAniCountText.text = _spawningBlasterAniCount.ToString();
                _spawningPickerAniCountText.text = _spawningPickerAniCount.ToString();

                UIInputEvents.RaiseUnlocked();
                SmoothPanelView.HidePanel(_selectionPanel, _panelAnimationDuration);
            });
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.E))
            {
                SmoothPanelView.ShowPanel(_selectionPanel, _panelAnimationDuration);
                UIInputEvents.RaiseLocked();
            }
        }

        // 校验食物和水晶成本后增加 Picker 计划数量
        private void CheckAddPickerAni()
        {
            var success = ResourceStateReader.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogError("[AniSelectionPanelController] Failed to get local player resource state.");
                return;
            }
            int foodSum = playerResourceState.FoodAmount;
            int crystalSum = playerResourceState.CrystalAmount;

            if (foodSum >= _pickerAniFoodCostCount &&
                crystalSum >= _pickerAniCrystalCostCount)
            {
                _spawningPickerAniCount++;
                _spawningPickerAniCountText.text = _spawningPickerAniCount.ToString();

                ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Food,
                    -_pickerAniFoodCostCount);

                ResourceRequestEvents.RaiseAdjustmentRequested(
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
            _spawningPickerAniCountText.text = _spawningPickerAniCount.ToString();

            ResourceRequestEvents.RaiseAdjustmentRequested(
                ResourceItemKind.Food,
                _pickerAniFoodCostCount);

            ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Crystal,
                    _pickerAniCrystalCostCount);

            Debug.Log("[AniSelectionPanelController] Deducted Picker Ani, resource refunded: " +
                           $"Food +{_pickerAniFoodCostCount}, Crystal +{_pickerAniCrystalCostCount}");
        }

        // 校验食物和水晶成本后增加 Blaster 计划数量
        private void CheckAddBlasterAni()
        {
            var success = ResourceStateReader.TryGetLocalPlayerResourceState(out var playerResourceState);
            if (!success)
            {
                Debug.LogError("[AniSelectionPanelController] Failed to get local player resource state.");
                return;
            }

            int foodSum = playerResourceState.FoodAmount;
            int crystalSum = playerResourceState.CrystalAmount;

            if (foodSum >= _blasterAniFoodCostCount &&
                crystalSum >= _blasterAniCrystalCostCount)
            {
                _spawningBlasterAniCount++;
                _spawningBlasterAniCountText.text = _spawningBlasterAniCount.ToString();

                ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Food,
                    -_blasterAniFoodCostCount);

                ResourceRequestEvents.RaiseAdjustmentRequested(
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
            _spawningBlasterAniCountText.text = _spawningBlasterAniCount.ToString();

            ResourceRequestEvents.RaiseAdjustmentRequested(
                ResourceItemKind.Food,
                _blasterAniFoodCostCount);

            ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Crystal,
                    _blasterAniCrystalCostCount);

            Debug.Log("[AniSelectionPanelController] Deducted Blaster Ani, resource refunded: " +
                           $"Food +{_blasterAniFoodCostCount}, Crystal +{_blasterAniCrystalCostCount}");
        }

        // 发布最终选择并关闭面板输入锁
        private void OnSelectionMenuConfirmed()
        {
            SmoothPanelView.HidePanel(_selectionPanel, _panelAnimationDuration);
            UIInputEvents.RaiseUnlocked();

            ClientAniSpawnRequestSender.RequestSpawnAnis(_spawningBlasterAniCount, _spawningPickerAniCount);

            _spawningPickerAniCount = 0;
            _spawningBlasterAniCount = 0;

            _spawningBlasterAniCountText.text = _spawningBlasterAniCount.ToString();
            _spawningPickerAniCountText.text = _spawningPickerAniCount.ToString();
        }
    }
}
