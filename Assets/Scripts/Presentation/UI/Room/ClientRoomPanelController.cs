using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Entities;
using Unity.NetCode;
using AnimarsCatcher.Presentation.Lan;
using UnityEngine.Events;
using AnimarsCatcher.Networking;
using AnimarsCatcher.Presentation.Account;
using AnimarsCatcher.Presentation.Network;
using AnimarsCatcher.Presentation.Room;

namespace AnimarsCatcher.Presentation.UI
{
    /// <summary>
    /// 驱动客户端房间发现、连接和连接结果提示流程
    /// 优先使用局域网广播，超时后可按配置尝试备用地址
    /// </summary>
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
        [SerializeField] private ushort _gamePort = NetworkPorts.Game;
        [SerializeField] private string _fallbackHostIp = "192.168.0.101";
        [SerializeField] private LanDiscoveryClient _lanDiscoveryClient;

        [Tooltip("超时后尝试备用 IP 或判定未发现主机，单位秒")]
        [SerializeField] private float _discoveryTimeoutSeconds = 5f;

        [Tooltip("服务器列表轮询间隔，单位秒")]
        [SerializeField] private float _discoveryPollInterval = 0.5f;

        [SerializeField] private float _connectTimeoutSeconds = 5f;

        [SerializeField] private bool _isSearchingServer;
        [SerializeField] private bool _isConnecting;
        [SerializeField] private bool _connectionFailed;
        [SerializeField] private bool _connectionSucceeded;
        [SerializeField] private bool _connectionInfoUpdated;

        private float _discoveryStartTime;
        private float _lastDiscoveryPollTime;
        private float _connectStartTime;

        private UnityAction<JoinRoomRequestedEvent> _onJoinRoomRequestedHandler;

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

        // 订阅加入房间和对局开始事件
        private void Start()
        {
            _onJoinRoomRequestedHandler = data => OnJoinRoomRequested();
            PresentationEventBus.Instance.Subscribe(_onJoinRoomRequestedHandler);
            NetworkPresentationEvents.MatchStarted.AddListener(OnMatchStarted);
        }

        // 对称解除事件监听并停止局域网发现
        private void OnDestroy()
        {
            PresentationEventBus.Instance.Unsubscribe(_onJoinRoomRequestedHandler);
            NetworkPresentationEvents.MatchStarted.RemoveListener(OnMatchStarted);
        }

            // 清空一次连接尝试的计时器、状态标记和提示界面
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

        // 按当前阶段分别推进房间发现或连接状态检测
        private void Update()
        {
            // 搜索阶段轮询局域网房间列表
            if (_isSearchingServer)
            {
                UpdateDiscovery();
            }

            // 连接阶段等待 NetworkId 单例出现
            if (_isConnecting)
            {
                CheckConnectionStatus();
            }
            else if (!_isConnecting && !_isSearchingServer && !_connectionInfoUpdated)
            {
                // 流程结束后只更新一次最终提示
                UpdateConnectionInfo();
                _connectionInfoUpdated = true;
            }
        }

        // 打开客户端房间面板并开始监听局域网广播
        private void OnJoinRoomRequested()
        {
            ResetState();
            _mainMenuPanel.gameObject.SetActive(false);
            _clientRoomPanel?.SetActive(true);

            _connectingPromptText?.gameObject.SetActive(true);

            _clientNameText.text = PlayerSession.CurrentUserName;

            // 记录搜索开始时间用于超时回退
            _lanDiscoveryClient?.StartListening();

            _isSearchingServer = true;
            _discoveryStartTime = Time.time;
            _lastDiscoveryPollTime = 0f;
        }

        private void OnMatchStarted(MatchStartedEvent eventData)
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

            // 周期刷新房间列表，找到主机后立即发起连接
        private void UpdateDiscovery()
        {
            var now = Time.time;

            // 搜索超时后尝试备用地址或显示未发现主机
            if (now - _discoveryStartTime > _discoveryTimeoutSeconds)
            {
                _isSearchingServer = false;
                _lanDiscoveryClient?.StopListening();

                // 未配置备用地址时直接结束本次连接流程
                if (string.IsNullOrEmpty(_fallbackHostIp))
                {
                    _connectionFailed = true;
                    _connectingPromptText.gameObject.SetActive(false);
                    _connectionFailedPromptText.gameObject.SetActive(true);

                    _findingHostPromptText.gameObject.SetActive(false);
                    _hostNotFoundPromptText.gameObject.SetActive(true);

                    return;
                }

                // 使用备用 IP 发起最后一次连接尝试
                StartConnectToServer(_fallbackHostIp, _gamePort, "默认主机");
                return;
            }

            if (now - _lastDiscoveryPollTime < _discoveryPollInterval)
            {
                return;
            }

            _lastDiscoveryPollTime = now;

            // 获取独立快照以避免遍历时缓存被更新
            var servers = _lanDiscoveryClient?.GetCurrentServers();
            if (servers == null || servers.Count == 0)
            {
                return;
            }

            // 当前产品只支持展示并连接首个发现的房间
            var server = servers[0];

            // 先展示主机信息再进入连接阶段
            string ip = server.IPAddress;
            ushort port = server.GamePort;
            string hostName = server.HostName;

            _roomAddressText.text = $"{ip}:{port}";
            _hostNameText.text = hostName;

            _hostDisplay.gameObject.SetActive(true);
            _findingHostPromptText.gameObject.SetActive(false);
            _hostNotFoundPromptText.gameObject.SetActive(false);

            StartConnectToServer(ip, port, hostName);
        }

        // 停止发现并向 NetCode 客户端连接器提交目标地址
        private void StartConnectToServer(string ip, ushort port, string hostName)
        {
            Debug.Log($"[ClientRoomPanel] Connecting to discovered server {hostName} at {ip}:{port}");

            _lanDiscoveryClient?.StopListening();

            // 连接请求会在客户端世界异步建立连接实体
            ClientNetCodeConnector.RequestConnect(ip, port);

            _isSearchingServer = false;
            _isConnecting = true;
            _connectionInfoUpdated = false;
            _connectStartTime = Time.time;

            _connectingPromptText?.gameObject.SetActive(true);
        }

        // 以 NetworkId 单例作为握手完成标志并处理连接超时
        private void CheckConnectionStatus()
        {
            if (!_isConnecting) return;

            var clientWorld = NetworkWorldLocator.FindClientWorld();
            if (clientWorld == null) return;

            var entityManager = clientWorld.EntityManager;

            // NetCode 在握手成功后为连接创建 NetworkId 单例
            if (!entityManager.CreateEntityQuery(typeof(NetworkId)).IsEmpty)
            {
                _isConnecting = false;
                _connectionSucceeded = true;
                Debug.Log("[ClientRoomPanel] Detected successful connection (NetworkId present).");

            // 连接建立后再发送玩家身份，避免 RPC 早于连接可用
                ClientLobbyIntroRpcSender.SendIntro(clientWorld, PlayerSession.CurrentUserName);

                return;
            }

            // 超过配置时限仍无 NetworkId 时标记连接失败
            if (Time.time - _connectStartTime > _connectTimeoutSeconds)
            {
                _isConnecting = false;
                _connectionFailed = true;
            }
        }
        // 根据最终连接结果切换互斥提示文本
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
}
