using AnimarsCatcher.Gameplay.Contracts;
using AnimarsCatcher.Presentation.Resource;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 通过与正式玩法相同的请求流程提交调试资源变化
    /// </summary>
    public class DebugResourcePanelController : MonoBehaviour
    {
        [Header("Buttons")]
        [SerializeField] private Button _addFoodButton;
        [SerializeField] private Button _addCrystalButton;

        [Header("Config")]
        [SerializeField] private int _debugAddAmount = 2;

        private void Awake()
        {
            _addFoodButton?.onClick.AddListener(OnAddFoodClicked);
            _addCrystalButton?.onClick.AddListener(OnAddCrystalClicked);
        }

        // 请求服务端增加配置数量的食物
        private void OnAddFoodClicked()
        {
            ResourceRequestEvents.RaiseAdjustmentRequested(
                ResourceItemKind.Food,
                _debugAddAmount
            );
        }

        // 请求服务端增加配置数量的水晶
        private void OnAddCrystalClicked()
        {
            ResourceRequestEvents.RaiseAdjustmentRequested(
                ResourceItemKind.Crystal,
                _debugAddAmount
            );
        }
    }
}
