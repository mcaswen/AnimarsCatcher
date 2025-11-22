using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.UI
{
    public class MainMenuPanelController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject _mainMenuPanel;

        [Header("Buttons")]
        [SerializeField] private Button _createRoomButton;
        [SerializeField] private Button _joinRoomButton;

        [Header("Feedback")]
        [SerializeField] private FloatingMessageView _messageText;

        private void Awake()
        {
            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(true);
            }

            if (_createRoomButton != null)
            {
                _createRoomButton.onClick.AddListener(OnCreateRoomClicked);
            }

            if (_joinRoomButton != null)
            {
                _joinRoomButton.onClick.AddListener(OnJoinRoomClicked);
            }

            if (_messageText != null)
            {
                _messageText.MessageText.text = string.Empty;
            }
        }

        private void OnCreateRoomClicked()
        {
            EventBus.Instance.Publish(new GameRoomCreatedEventData());
            _messageText.ShowMessage("Room created successfully");

            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
            }
        }

        private void OnJoinRoomClicked()
        {
            EventBus.Instance.Publish(new JoinGameRoomRequestEventData());
        }
    }
}