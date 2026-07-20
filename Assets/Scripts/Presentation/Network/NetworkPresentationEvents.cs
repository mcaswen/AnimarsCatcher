using UnityEngine.Events;

namespace AnimarsCatcher.Presentation.Network
{
    /// <summary>
    /// 将 NetCode 世界产生的状态变化发布给主线程表现层
    /// </summary>
    public static class NetworkPresentationEvents
    {
        public static readonly UnityEvent<LobbyClientJoinedEvent> LobbyClientJoined = new();
        public static readonly UnityEvent<LobbyClientLeftEvent> LobbyClientLeft = new();
        public static readonly UnityEvent<MatchStartedEvent> MatchStarted = new();
        public static readonly UnityEvent<MatchEndedEvent> MatchEnded = new();
        public static readonly UnityEvent<ConnectionLostEvent> ConnectionLost = new();

        /// <summary>
        /// 发布房间成员加入事件
        /// </summary>
        public static void RaiseLobbyClientJoined(
            NetworkEventSource source,
            int networkId,
            string playerName,
            bool isLocalPlayer)
        {
            LobbyClientJoined.Invoke(
                new LobbyClientJoinedEvent(source, networkId, playerName, isLocalPlayer));
        }

        /// <summary>
        /// 发布房间成员离开事件
        /// </summary>
        public static void RaiseLobbyClientLeft(
            NetworkEventSource source,
            int networkId,
            string playerName)
        {
            LobbyClientLeft.Invoke(new LobbyClientLeftEvent(source, networkId, playerName));
        }

        /// <summary>
        /// 发布对局开始事件
        /// </summary>
        public static void RaiseMatchStarted(NetworkEventSource source, int localPlayerNetworkId)
        {
            MatchStarted.Invoke(new MatchStartedEvent(source, localPlayerNetworkId));
        }

        /// <summary>
        /// 发布对局结束事件
        /// </summary>
        public static void RaiseMatchEnded(NetworkEventSource source, string reason)
        {
            MatchEnded.Invoke(new MatchEndedEvent(source, reason));
        }

        /// <summary>
        /// 发布连接中断事件
        /// </summary>
        public static void RaiseConnectionLost(
            NetworkEventSource source,
            int networkId,
            string reason)
        {
            ConnectionLost.Invoke(new ConnectionLostEvent(source, networkId, reason));
        }
    }
}
