using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Presentation.Resource;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 将调试按钮转换为本地资源事件
    /// 仅用于编辑和联调界面
    /// </summary>
    [MovedFrom(true, "AnimarsCatcher.Presentation.UI", "AnimarsCatcher.Presentation", "DebugUIManager")]
    public class LegacyDebugResourceButtonsController : MonoBehaviour
    {
        [FormerlySerializedAs("AddCrystalButton")]
        [SerializeField] private Button _addCrystalButton;
        [FormerlySerializedAs("AddFoodButton")]
        [SerializeField] private Button _addFoodButton;

        private void Awake()
        {
            _addCrystalButton.onClick.AddListener(() =>
            {
                ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Crystal,
                    2);
            });

            _addFoodButton.onClick.AddListener(() =>
            {
                ResourceRequestEvents.RaiseAdjustmentRequested(
                    ResourceItemKind.Food,
                    2);
            });
        }
    }
}
