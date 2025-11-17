using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using AnimarsCatcher.Mono.Global;

namespace AnimarsCatcher.Mono.Lan
{
    // Host 端的局域网广播：创建房间后定期广播
    public class LanDiscoveryHost : MonoBehaviour
    {
        [Header("Discovery Settings")]
        [SerializeField] private int _discoveryPort = 47777;
        [SerializeField] private ushort _gamePort = NetPorts.Game;
        [SerializeField] private float _broadcastInterval = 1.0f; // 每秒一次

        [Header("Debug")]
        [SerializeField] private bool _autoStartOnAwake = false;

        private UdpClient _udpClient;
        private IPEndPoint _broadcastEndPoint;
        private float _timeSinceLastBroadcast;
        private bool _isBroadcasting;
        private string _hostName = "UnknownHost";

        private void Awake()
        {
            // 广播目标 255.255.255.255:discoveryPort
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

        // 开始广播
        public void StartBroadcast(string hostName, ushort gamePort)
        {
            if (_isBroadcasting || NetRuntimeRole.Current != NetworkRunRole.Host) 
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
            catch (System.Exception e)
            {
                Debug.LogError($"[LanDiscoveryHost] Failed to start broadcast: {e}");
                _isBroadcasting = false;
            }
        }

        // 停止广播
        public void StopBroadcast()
        {
            if (!_isBroadcasting || NetRuntimeRole.Current != NetworkRunRole.Host) 
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
            if (!_isBroadcasting || _udpClient == null || NetRuntimeRole.Current != NetworkRunRole.Host)
                return;

            _timeSinceLastBroadcast += Time.deltaTime;
            if (_timeSinceLastBroadcast >= _broadcastInterval)
            {
                _timeSinceLastBroadcast = 0f;
                SendBroadcast();
            }
        }

        private void SendBroadcast()
        {
            try
            {
                // 简单字符串格式：ACATCH|1|HostName|GamePort
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
