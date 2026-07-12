using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.UI
{
    /// <summary>
    /// 将主菜单创建房间和加入房间命令发布到对应流程
    /// </summary>
    public class MainMenuPanelController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject _mainMenuPanel;

        [Header("Buttons")]
        [SerializeField] private Button _createRoomButton;
        [SerializeField] private Button _joinRoomButton;

        [Header("Feedback")]
        [SerializeField] private FloatingMessageView _messageText;

        // 初始化面板反馈并绑定主菜单按钮
        private void Awake()
        {
            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
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

        // 发布创建房间事件并隐藏主菜单
        private void OnCreateRoomClicked()
        {
            EventBus.Instance.Publish(new GameRoomCreatedEventData());
            _messageText.ShowMessage("Room created successfully");

            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
            }
        }

        // 发布加入房间事件并隐藏主菜单
        private void OnJoinRoomClicked()
        {
            EventBus.Instance.Publish(new JoinGameRoomRequestEventData());
        }
    }
}
