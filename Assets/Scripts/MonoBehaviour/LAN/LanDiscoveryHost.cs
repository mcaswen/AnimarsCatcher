using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.Lan
{
    /// <summary>
    /// 由 Host 周期广播可加入房间的名称和游戏端口
    /// </summary>
    public class LanDiscoveryHost : MonoBehaviour
    {
        [Header("Discovery Settings")]
        [SerializeField] private int _discoveryPort = 47777;
        [SerializeField] private ushort _gamePort = NetworkPorts.Game;
        [SerializeField] private float _broadcastInterval = 1.0f;

        [Header("Debug")]
        [SerializeField] private bool _autoStartOnAwake = false;

        private UdpClient _udpClient;
        private IPEndPoint _broadcastEndPoint;
        private float _timeSinceLastBroadcast;
        private bool _isBroadcasting;
        private string _hostName = "UnknownHost";

        private void Awake()
        {
            // 使用全网段广播地址让同一局域网客户端收到房间信息
            _broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

            if (_autoStartOnAwake)
            {
                StartBroadcast("DebugHost", _gamePort);
            }
        }

        private void OnDestroy()
        {
            StopBroadcast();
        }

        /// <summary>
        /// 创建 UDP 广播套接字并开始发布房间信息
        /// </summary>
        public void StartBroadcast(string hostName, ushort gamePort)
        {
            if (_isBroadcasting || NetworkRuntimeRole.Current != NetworkRunRole.Host)
                return;

            _hostName = string.IsNullOrEmpty(hostName) ? "UnknownHost" : hostName;
            _gamePort = gamePort;

            try
            {
                _udpClient = new UdpClient();
                _udpClient.EnableBroadcast = true;
                _isBroadcasting = true;
                _timeSinceLastBroadcast = 0f;

                Debug.Log($"[LanDiscoveryHost] Start broadcasting on port {_discoveryPort}, gamePort={_gamePort}.");
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[LanDiscoveryHost] Failed to start broadcast: {exception}");
                _isBroadcasting = false;
            }
        }

        /// <summary>
        /// 停止发布房间并释放套接字
        /// </summary>
        public void StopBroadcast()
        {
            if (!_isBroadcasting || NetworkRuntimeRole.Current != NetworkRunRole.Host)
                return;

            _isBroadcasting = false;

            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient = null;
            }

            Debug.Log("[LanDiscoveryHost] Broadcast stopped.");
        }

        private void Update()
        {
            if (!_isBroadcasting || _udpClient == null || NetworkRuntimeRole.Current != NetworkRunRole.Host)
                return;

            _timeSinceLastBroadcast += Time.deltaTime;
            if (_timeSinceLastBroadcast >= _broadcastInterval)
            {
                _timeSinceLastBroadcast = 0f;
                SendBroadcast();
            }
        }

        // 按约定协议编码并发送单个房间广播数据报
        private void SendBroadcast()
        {
            try
            {
                // 广播协议格式为 ACATCH|版本|主机名|游戏端口
                string message = $"ACATCH|1|{_hostName}|{_gamePort}";
                byte[] data = Encoding.UTF8.GetBytes(message);

                _udpClient.Send(data, data.Length, _broadcastEndPoint);
                Debug.Log($"[LanDiscoveryHost] Broadcast: {message}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[LanDiscoveryHost] Failed to send broadcast: {ex.Message}");
            }
        }

        
    }
}
