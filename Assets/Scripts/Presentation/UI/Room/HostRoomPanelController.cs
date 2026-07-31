using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Net;
using System.Net.Sockets;
using UnityEngine.Events;
using AnimarsCatcher.Presentation.Lan;
using AnimarsCatcher.Networking;
using AnimarsCatcher.Presentation.Account;
using AnimarsCatcher.Presentation.Network;
using AnimarsCatcher.Presentation.Room;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 驱动 Host 创建房间、本机回连、局域网广播和成员展示流程
    /// </summary>
    public class HostRoomPanelController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject _hostRoomPanel;
        [SerializeField] private GameObject _mainMenuPanel;
        [SerializeField] private GameObject _clientDisplay;

        [Header("Room UI")]
        [SerializeField] private TMP_Text _hostNameText;
        [SerializeField] private TMP_Text _clientNameText;

        [SerializeField] private TMP_Text _roomInfoText;
        [SerializeField] private TMP_Text _noClientConnectedPromptText;

        [SerializeField] private Button _startGameButton;
        [SerializeField] private Button _backToMainMenuButton;

        [Header("Net Settings")]
        [SerializeField] private string _localIpAddress = "127.0.0.1";

        [SerializeField] private ushort _gamePort = NetworkPorts.Game;
        [SerializeField] private LanDiscoveryHost _lanDiscoveryHost;

        [Header("Game Settings")]
        [SerializeField] private string _startGameSceneName = "SCN_GameLevel";

        private UnityAction<CreateRoomRequestedEvent> _onCreateRoomHandler;


        private void Awake()
        {
            _hostRoomPanel?.SetActive(false);

            _startGameButton?.onClick.AddListener(OnStartGameClicked);

            _backToMainMenuButton?.onClick.AddListener(OnBackToMainMenuClicked);

            _clientDisplay?.SetActive(false);

            _noClientConnectedPromptText?.gameObject.SetActive(true);

        }

            // 订阅创建房间、成员加入和对局开始事件
        private void Start()
        {
            _onCreateRoomHandler = data => OnCreateRoomRequested();
            PresentationEventBus.Instance?.Subscribe(_onCreateRoomHandler);
            NetworkPresentationEvents.LobbyClientJoined.AddListener(OnLobbyClientJoined);
            NetworkPresentationEvents.MatchStarted.AddListener(OnMatchStarted);
        }

            // 对称解除静态事件监听，防止场景切换后重复回调
        private void OnDestroy()
        {
           PresentationEventBus.Instance?.Unsubscribe(_onCreateRoomHandler);
           NetworkPresentationEvents.LobbyClientJoined.RemoveListener(OnLobbyClientJoined);
           NetworkPresentationEvents.MatchStarted.RemoveListener(OnMatchStarted);
        }

            // 启动服务端监听、本机客户端回连和局域网广播
        private void OnCreateRoomRequested()
        {
            // 先启动服务端监听再发起本机连接
            ServerNetCodeController.StartListen(_gamePort);

            // Host 进程中的客户端通过回环地址加入自身房间
            ClientNetCodeConnector.RequestConnect(_localIpAddress, _gamePort);

            // 广播玩家名称和游戏端口供局域网客户端发现
            string hostName = PlayerSession.CurrentUserName;
            _lanDiscoveryHost?.StartBroadcast(hostName, _gamePort);

            // 网络入口全部启动后再显示房间信息
            _hostRoomPanel?.SetActive(true);
            UpdateRoomInfo();
        }

        // 刷新房主名称和可供其他设备连接的局域网地址
        private void UpdateRoomInfo()
        {
            if (_hostNameText != null)
            {
                _hostNameText.text = $"{PlayerSession.CurrentUserName}";
            }

            if (_roomInfoText != null)
            {
                string localIp = GetLocalIPv4Address();
                _roomInfoText.text = $"{localIp}:{_gamePort}";
            }
        }

        private void OnStartGameClicked()
        {
            HostStartMatchRequestSender.SendStartMatchRequest(_startGameSceneName);
        }

        private void OnBackToMainMenuClicked()
        {
            _hostRoomPanel?.SetActive(false);
            _mainMenuPanel?.SetActive(true);

            // 停止局域网广播
            _lanDiscoveryHost?.StopBroadcast();
        }

            // 只展示远端成员，本机 Host 客户端不占用访客槽位
        private void OnLobbyClientJoined(LobbyClientJoinedEvent eventData)
        {
            if (eventData.IsLocalPlayer)
                return;

            _clientDisplay?.SetActive(true);

            _noClientConnectedPromptText?.gameObject.SetActive(false);

            _clientNameText.text = eventData.PlayerName;

            Debug.Log($"[HostRoomPanel] Remote client joined lobby: {eventData.PlayerName} (NetworkId={eventData.NetworkId}, Source={eventData.Source})");
        }

        private void OnMatchStarted(MatchStartedEvent info)
        {
            _hostRoomPanel?.SetActive(false);
            _mainMenuPanel?.SetActive(true);

            Debug.Log("[HostRoomPanel] Match started, hide lobby UI.");
        }

        private string GetLocalIPv4Address()
        {
            string result = "127.0.0.1";

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        result = ip.ToString();
                        break;
                    }
                }
            }
            catch
            {
                // 地址查询失败时继续使用回环地址
            }

            return result;
        }
    }
}
