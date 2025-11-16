using System;
using UnityEngine.Events;

namespace AnimarsCatcher.Mono.Global
{
    public static class NetUIEventBridge
    {
        // Lobby 相关事件
        public static UnityEvent<LobbyClientJoinedEventData> LobbyClientJoinedEvent = new UnityEvent<LobbyClientJoinedEventData>();
        public static UnityEvent<LobbyClientLeftEventData> LobbyClientLeftEvent = new UnityEvent<LobbyClientLeftEventData>();

        // 对局相关
        public static UnityEvent<MatchStartedEventData> MatchStartedEvent = new UnityEvent<MatchStartedEventData>();
        public static UnityEvent<MatchEndedEventData> MatchEndedEvent = new UnityEvent<MatchEndedEventData>();
        // 连接相关
        public static UnityEvent<ConnectionLostEventData> ConnectionLostEvent = new UnityEvent<ConnectionLostEventData>();

#region 对外 Raise 封装

        public static void RaiseLobbyClientJoinedEvent(
            NetUIEventSource source,
            int networkId,
            string playerName,
            bool isLocalPlayer)
        {
            LobbyClientJoinedEvent?.Invoke(
                new LobbyClientJoinedEventData(source, networkId, playerName, isLocalPlayer)
            );
        }

        public static void RaiseLobbyClientLeftEvent(
            NetUIEventSource source,
            int networkId,
            string playerName)
        {
            LobbyClientLeftEvent?.Invoke(
                new LobbyClientLeftEventData(source, networkId, playerName)
            );
        }

        public static void RaiseMatchStartedEvent(
            NetUIEventSource source,
            int localPlayerNetworkId)
        {
            MatchStartedEvent?.Invoke(
                new MatchStartedEventData(source, localPlayerNetworkId)
            );
        }

        public static void RaiseMatchEndedEvent(
            NetUIEventSource source, 
            string reason)
        {
            MatchEndedEvent?.Invoke(
                new MatchEndedEventData(source, reason)
            );
        }

        public static void RaiseConnectionLostEvent(
            NetUIEventSource source,
            int networkId,
            string reason)
        {
            ConnectionLostEvent?.Invoke(
                new ConnectionLostEventData(source, networkId, reason)
            );
        }
    }

#endregion

}