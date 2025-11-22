using AnimarsCatcher.Mono.Global;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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

    private void OnAddFoodClicked()
    {
        NetUIEventBridge.RaiseResourceChangedRequestedEvent(
            NetUIEventSource.ClientWorld,
            ResourceType.Food,
            _debugAddAmount
        );
    }

    private void OnAddCrystalClicked()
    {
        NetUIEventBridge.RaiseResourceChangedRequestedEvent(
            NetUIEventSource.ClientWorld,
            ResourceType.Crystal,
            _debugAddAmount
        );
    }
}
