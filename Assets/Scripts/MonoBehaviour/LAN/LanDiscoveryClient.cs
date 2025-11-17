using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnimarsCatcher.Mono.Global;
using UnityEngine;

namespace AnimarsCatcher.Mono.Lan
{
    [Serializable]
    public class LanDiscoveredServer
    {
        public string HostName;
        public string IpAddress;
        public ushort GamePort;
        public float LastSeenTime;
    }

    // 客户端局域网发现模块：
    // 监听 UDP 广播端口
    // 维护“发现到的房间”列表
    // 提供查询接口给 UI 使用
    public class LanDiscoveryClient : MonoBehaviour
    {
        [Header("Discovery Settings")]
        [SerializeField] private int _discoveryPort = 47777;
        [SerializeField] private float _serverTimeoutSeconds = 5f;

        [Header("Debug")]
        [SerializeField] private bool _autoStartOnAwake = true;

        private UdpClient _udpClient;
        private readonly Dictionary<string, LanDiscoveredServer> _serversByIp =
            new Dictionary<string, LanDiscoveredServer>();

        private bool _isListening;

        private void Awake()
        {
            if (_autoStartOnAwake)
            {
                StartListening();
            }
        }

        private void OnDestroy()
        {
            StopListening();
        }

        public void StartListening()
        {
            if (_isListening || NetRuntimeRole.Current != NetworkRunRole.Client) 
                return;

            try
            {
                _udpClient = new UdpClient(_discoveryPort);
                _udpClient.EnableBroadcast = true;
                _udpClient.Client.Blocking = false; // 避免 Receive 阻塞主线程

                _isListening = true;

                Debug.Log($"[LanDiscoveryClient] Start listening on discovery port {_discoveryPort}.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LanDiscoveryClient] Failed to start listening: {ex}");
                _isListening = false;
            }
        }

        public void StopListening()
        {
            if (!_isListening || NetRuntimeRole.Current != NetworkRunRole.Client) 
                return;

            _isListening = false;

            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient = null;
            }

            _serversByIp.Clear();

            Debug.Log("[LanDiscoveryClient] Stop listening and clear server list.");
        }

        private void Update()
        {
            if (!_isListening || _udpClient == null || NetRuntimeRole.Current != NetworkRunRole.Client) 
                return;

            ReceivePackets();
            CleanupExpiredServers();
        }

        private void ReceivePackets()
        {
            try
            {
                while (_udpClient.Available > 0)
                {
                    IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpClient.Receive(ref remoteEndPoint);

                    string message = Encoding.UTF8.GetString(data);
                    ParseAndRegisterServer(message, remoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LanDiscoveryClient] Receive error: {ex.Message}");
            }
        }

        private void ParseAndRegisterServer(string message, IPEndPoint remoteEndPoint)
        {
            // 期望格式：ACATCH|1|HostName|GamePort
            var parts = message.Split('|');
            if (parts.Length < 4)
                return;

            if (parts[0] != "ACATCH")
                return;

            string hostName = parts[2];
            if (!ushort.TryParse(parts[3], out var gamePort))
                return;

            string ip = remoteEndPoint.Address.ToString();
            float now = Time.time;

            if (_serversByIp.TryGetValue(ip, out var existing))
            {
                existing.HostName = hostName;
                existing.GamePort = gamePort;
                existing.LastSeenTime = now;
            }
            else
            {
                _serversByIp[ip] = new LanDiscoveredServer
                {
                    HostName = hostName,
                    IpAddress = ip,
                    GamePort = gamePort,
                    LastSeenTime = now
                };
            }

            Debug.Log($"[LanDiscoveryClient] Discovered server: {hostName} at {ip}:{gamePort}");
        }

        private void CleanupExpiredServers()
        {
            if (_serversByIp.Count == 0)
                return;

            float now = Time.time;
            var toRemove = new List<string>();

            foreach (var kvp in _serversByIp)
            {
                if (now - kvp.Value.LastSeenTime > _serverTimeoutSeconds)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _serversByIp.Remove(key);
            }
        }

        // 返回当前活跃的服务器列表快照。
        public List<LanDiscoveredServer> GetCurrentServers()
        {
            return new List<LanDiscoveredServer>(_serversByIp.Values);
        }

        // 尝试拿到“最近看到的第一个服务器”
        public bool TryGetFirstServer(out LanDiscoveredServer server)
        {
            foreach (var kvp in _serversByIp)
            {
                server = kvp.Value;
                return true;
            }

            server = null;
            return false;
        }
    }
}
