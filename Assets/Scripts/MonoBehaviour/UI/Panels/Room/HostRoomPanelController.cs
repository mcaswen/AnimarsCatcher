using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Net;
using System.Net.Sockets;
using UnityEngine.Events;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Lan;


// 订阅“创建房间”事件
// 收到事件时：开启服务器监听、本机客户端连接、显示房主昵称和房间地址
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

    [SerializeField] private ushort _gamePort = NetPorts.Game;
    [SerializeField] private LanDiscoveryHost _lanDiscoveryHost;

    [Header("Game Settings")]
    [SerializeField] private string _startGameSceneName = "GameLevel";

    private UnityAction<GameRoomCreatedEventData> _onCreateRoomHandler;


    private void Awake()
    {
        _hostRoomPanel?.SetActive(false);

        _startGameButton?.onClick.AddListener(OnStartGameClicked);

        _backToMainMenuButton?.onClick.AddListener(OnBackToMainMenuClicked);

        _clientDisplay?.SetActive(false);

        _noClientConnectedPromptText?.gameObject.SetActive(true);

    }

    private void Start()
    {
        _onCreateRoomHandler = data => OnCreateRoomRequested();
        EventBus.Instance?.Subscribe(_onCreateRoomHandler);
        NetUIEventBridge.LobbyClientJoinedEvent.AddListener(OnLobbyClientJoined);
        NetUIEventBridge.MatchStartedEvent.AddListener(OnMatchStarted);
    }

    private void OnDestroy()
    {
       EventBus.Instance?.Unsubscribe(_onCreateRoomHandler);
       NetUIEventBridge.LobbyClientJoinedEvent.RemoveListener(OnLobbyClientJoined);
       NetUIEventBridge.MatchStartedEvent.RemoveListener(OnMatchStarted);
    }

    private void OnCreateRoomRequested()
    {
        // 让 Server World 开始监听
        NetCodeServerController.StartListen(_gamePort);

        // 让本机 Client 连接到本机服务器
        NetCodeClientConnector.RequestConnect(_localIpAddress, _gamePort);

        // 启动局域网广播
        string hostName = PlayerSession.CurrentUserName;
        _lanDiscoveryHost?.StartBroadcast(hostName, _gamePort);

        // 显示房间面板，更新展示信息
        _hostRoomPanel?.SetActive(true);
        UpdateRoomInfo();
    }

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
        HostStartGameHelper.SendStartGameRpc(_startGameSceneName);
    }

    private void OnBackToMainMenuClicked()
    {
        _hostRoomPanel?.SetActive(false);
        _mainMenuPanel?.SetActive(true);

        // 停止局域网广播
        _lanDiscoveryHost?.StopBroadcast();
    }

    private void OnLobbyClientJoined(LobbyClientJoinedEventData eventData)
    {
        // 对于 HostRoom 来说：关心的是“远端客户端连进来”的那次
        if (eventData.IsLocalPlayer)
            return;

        _clientDisplay?.SetActive(true);
    
        _noClientConnectedPromptText?.gameObject.SetActive(false);

        _clientNameText.text = eventData.PlayerName;

        Debug.Log($"[HostRoomPanel] Remote client joined lobby: {eventData.PlayerName} (NetId={eventData.NetworkId}, Source={eventData.Source})");
    }

    private void OnMatchStarted(MatchStartedEventData info)
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
            // 忽略异常，用默认值
        }

        return result;
    }
}
