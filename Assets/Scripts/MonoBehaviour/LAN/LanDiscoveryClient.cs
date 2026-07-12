using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnimarsCatcher.Mono.Global;
using UnityEngine;
using UnityEngine.Serialization;

namespace AnimarsCatcher.Mono.Lan
{
    /// <summary>
    /// 最近一次通过局域网广播发现的房间信息
    /// </summary>
    [Serializable]
    public class LanDiscoveredServer
    {
        public string HostName;
        [FormerlySerializedAs("IpAddress")]
        public string IPAddress;
        public ushort GamePort;
        public float LastSeenTime;
    }

    /// <summary>
    /// 在客户端监听 UDP 广播并维护可加入房间的活动快照
    /// 所有套接字读取均使用非阻塞模式避免卡住 Unity 主线程
    /// </summary>
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

        /// <summary>
        /// 开始监听项目约定的局域网发现端口
        /// </summary>
        public void StartListening()
        {
            if (_isListening || NetworkRuntimeRole.Current != NetworkRunRole.Client)
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

        /// <summary>
        /// 关闭发现套接字并清空过期房间缓存
        /// </summary>
        public void StopListening()
        {
            if (!_isListening || NetworkRuntimeRole.Current != NetworkRunRole.Client)
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
            if (!_isListening || _udpClient == null || NetworkRuntimeRole.Current != NetworkRunRole.Client)
                return;

            ReceivePackets();
            CleanupExpiredServers();
        }

        // 消费当前帧已经到达的全部数据报
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

        // 校验协议头和端口后按来源 IP 更新房间快照
        private void ParseAndRegisterServer(string message, IPEndPoint remoteEndPoint)
        {
            // 广播协议格式为 ACATCH|版本|主机名|游戏端口
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
                    IPAddress = ip,
                    GamePort = gamePort,
                    LastSeenTime = now
                };
            }

            Debug.Log($"[LanDiscoveryClient] Discovered server: {hostName} at {ip}:{gamePort}");
        }

        // 移除超过存活窗口仍未收到广播的主机
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

        /// <summary>
        /// 返回当前活动房间的独立列表快照
        /// </summary>
        public List<LanDiscoveredServer> GetCurrentServers()
        {
            return new List<LanDiscoveredServer>(_serversByIp.Values);
        }

        /// <summary>
        /// 尝试取得当前缓存中的第一个房间
        /// </summary>
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
