using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Entities;
using Unity.NetCode;
using AnimarsCatcher.Mono.Global;
using AnimarsCatcher.Mono.Lan;
using UnityEngine.Events;


// 订阅 JoinRoomRequested 事件
// 显示 IP 输入 UI
// 调用 NetCodeClientConnector.RequestConnect 进行连接
public class ClientRoomPanelController : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] private GameObject _clientRoomPanel;
    [SerializeField] private GameObject _mainMenuPanel;
    [SerializeField] private GameObject _hostDisplay;

    [Header("Join Room UI")]
    [SerializeField] private Button _returnFromClientRoomButton;
    [SerializeField] private TMP_Text _connectionFailedPromptText;
    [SerializeField] private TMP_Text _connectingPromptText;
    [SerializeField] private TMP_Text _connectionSucceededPromptText;

    [SerializeField] private TMP_Text _hostNameText;
    [SerializeField] private TMP_Text _clientNameText;
    [SerializeField] private TMP_Text _roomAddressText;
    [SerializeField] private TMP_Text _findingHostPromptText;
    [SerializeField] private TMP_Text _hostNotFoundPromptText;

    [Header("Net Settings")]
    [SerializeField] private ushort _gamePort = NetPorts.Game;
    [SerializeField] private string _fallbackHostIp = "192.168.0.101";
    [SerializeField] private LanDiscoveryClient _lanDiscoveryClient;

    [Tooltip("发现房间的最大等待时间")]
    [SerializeField] private float _discoveryTimeoutSeconds = 5f;

    [Tooltip("轮询服务器列表的间隔时间")]
    [SerializeField] private float _discoveryPollInterval = 0.5f;

    [Tooltip("连接超时时间")]
    [SerializeField] private float _connectTimeoutSeconds = 5f;

    [SerializeField] private bool _isSearchingServer;
    [SerializeField] private bool _isConnecting;
    [SerializeField] private bool _connectionFailed;
    [SerializeField] private bool _connectionSucceeded;
    [SerializeField] private bool _connectionInfoUpdated;

    private float _discoveryStartTime;
    private float _lastDiscoveryPollTime;
    private float _connectStartTime;

    private UnityAction<JoinGameRoomRequestEventData> _onJoinRoomRequestedHandler;

    private void Awake()
    {
        if (_clientRoomPanel != null)
        {
            _clientRoomPanel.SetActive(false);
        }

        if (_returnFromClientRoomButton != null)
        {
            _returnFromClientRoomButton.onClick.AddListener(OnBackButtonClicked);
        }
        ResetState();
    }

    private void Start()
    {
        _onJoinRoomRequestedHandler = data => OnJoinRoomRequested();
        EventBus.Instance.Subscribe(_onJoinRoomRequestedHandler);
        NetUIEventBridge.MatchStartedEvent.AddListener(OnMatchStarted);
    }

    private void OnDestroy()
    {
        EventBus.Instance.Unsubscribe(_onJoinRoomRequestedHandler);
        NetUIEventBridge.MatchStartedEvent.RemoveListener(OnMatchStarted);
    }

    private void ResetState()
    {
        _isSearchingServer = false;
        _isConnecting = false;
        _connectionFailed = false;
        _connectionSucceeded = false;
        _connectionInfoUpdated = false;
        _discoveryStartTime = 0f;
        _lastDiscoveryPollTime = 0f;
        _connectStartTime = 0f;
        _hostDisplay.gameObject.SetActive(false);
        _findingHostPromptText.gameObject.SetActive(true);
        _hostNotFoundPromptText.gameObject.SetActive(false);
        _connectingPromptText.gameObject.SetActive(false);
        _connectionFailedPromptText.gameObject.SetActive(false);
        _connectionSucceededPromptText.gameObject.SetActive(false);
    }

    private void Update()
    {
        // 更新 LAN 发现
        if (_isSearchingServer)
        {
            UpdateDiscovery();
        }

        // 如果已经发起连接，请求 NetCode 连接状态
        if (_isConnecting)
        {
            CheckConnectionStatus();
        }
        else if (!_isConnecting && !_isSearchingServer && !_connectionInfoUpdated)
        {
            // 连接流程结束，更新 UI 提示一次
            UpdateConnectionInfo();
            _connectionInfoUpdated = true;
        }
    }

    private void OnJoinRoomRequested()
    {
        ResetState();
        _mainMenuPanel.gameObject.SetActive(false);
        _clientRoomPanel?.SetActive(true);
        
        _connectingPromptText?.gameObject.SetActive(true);
        
        _clientNameText.text = PlayerSession.CurrentUserName;

        // 开始监听 LAN 广播
        _lanDiscoveryClient?.StartListening();

        _isSearchingServer = true;
        _discoveryStartTime = Time.time;
        _lastDiscoveryPollTime = 0f;
    }

    private void OnMatchStarted(MatchStartedEventData eventData)
    {
        _clientRoomPanel?.SetActive(false);
        _mainMenuPanel?.SetActive(true);

        Debug.Log("[ClientRoomPanel] Match started, hide lobby UI.");
    }

    private void OnBackButtonClicked()
    {
        ResetState();
        _mainMenuPanel.gameObject.SetActive(true);

        _lanDiscoveryClient?.StopListening();

        _clientRoomPanel?.SetActive(false);

        _mainMenuPanel?.SetActive(true);
    }

    // 周期性刷新 LAN 服务器列表，如有房间则发起连接；超时则提示失败或兜底。
    private void UpdateDiscovery()
    {
        var now = Time.time;

        // 超时：一直没发现任何房间
        if (now - _discoveryStartTime > _discoveryTimeoutSeconds)
        {
            _isSearchingServer = false;
            _lanDiscoveryClient?.StopListening();

            // 尝试保底ip连接
            if (string.IsNullOrEmpty(_fallbackHostIp))
            {
                _connectionFailed = true;
                _connectingPromptText.gameObject.SetActive(false);
                _connectionFailedPromptText.gameObject.SetActive(true);

                _findingHostPromptText.gameObject.SetActive(false);
                _hostNotFoundPromptText.gameObject.SetActive(true);

                return;
            }

            // 尝试连默认 IP
            StartConnectToServer(_fallbackHostIp, _gamePort, "默认主机");
            return;
        }

        if (now - _lastDiscoveryPollTime < _discoveryPollInterval)
        {
            return;
        }

        _lastDiscoveryPollTime = now;

        // 拿当前发现的服务器列表
        var servers = _lanDiscoveryClient?.GetCurrentServers();
        if (servers == null || servers.Count == 0)
        {
            return; // 还没发现房间
        }

        // 取第一个
        var server = servers[0];

        // 设置提示文本
        string ip = server.IpAddress;
        ushort port = server.GamePort;
        string hostName = server.HostName;

        _roomAddressText.text = $"{ip}:{port}";
        _hostNameText.text = hostName;

        _hostDisplay.gameObject.SetActive(true);
        _findingHostPromptText.gameObject.SetActive(false);
        _hostNotFoundPromptText.gameObject.SetActive(false);

        StartConnectToServer(ip, port, hostName);
    }

    private void StartConnectToServer(string ip, ushort port, string hostName)
    {
        Debug.Log($"[ClientRoomPanel] Connecting to discovered server {hostName} at {ip}:{port}");

        _lanDiscoveryClient?.StopListening();

        // 发起 NetCode 连接请求
        NetCodeClientConnector.RequestConnect(ip, port);

        _isSearchingServer = false;
        _isConnecting = true;
        _connectionInfoUpdated = false;
        _connectStartTime = Time.time;

        _connectingPromptText?.gameObject.SetActive(true);
    }

    // 每帧检查 NetCode 的连接状态：
    // 有 NetworkId 了：连接成功
    // 超时还没连上：提示失败
    private void CheckConnectionStatus()
    {
        if (!_isConnecting) return;

        var clientWorld = WorldManager.FindClientWorld();
        if (clientWorld == null) return;

        var entityManager = clientWorld.EntityManager;

        // 成功条件：已经有 NetworkId singleton
        if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
        {
            _isConnecting = false;
            _connectionSucceeded = true;
            Debug.Log("[ClientRoomPanel] Detected successful connection (NetworkId present).");
            
            // 发送玩家信息Rpc
            ClientLobbyIntroSender.SendIntro(clientWorld, PlayerSession.CurrentUserName);

            return;
        }

        // 超时判定
        if (Time.time - _connectStartTime > _connectTimeoutSeconds)
        {
            _isConnecting = false;
            _connectionFailed = true;
        }
    }
    private void UpdateConnectionInfo()
    { 
        if (_connectionFailed)
        {
            Debug.Log("[ClientRoomPanel] Connection failed. Updating UI.");
            _connectionFailedPromptText.gameObject.SetActive(true);
            _connectingPromptText.gameObject.SetActive(false);
            _connectionSucceededPromptText.gameObject.SetActive(false);
        }
        else if (_connectionSucceeded)
        {
            Debug.Log("[ClientRoomPanel] Connection succeeded. Updating UI.");
            _connectionSucceededPromptText.gameObject.SetActive(true);
            _connectingPromptText.gameObject.SetActive(false);
            _connectionFailedPromptText.gameObject.SetActive(false);
        }
    }
}
