using System;

namespace AnimarsCatcher.Mono.Global
{
    // 来源世界枚举，方便调试
    public enum NetUIEventSource
    {
        Unknown = 0,
        ServerWorld,
        ClientWorld
    }

#region Lobby 相关事件载体

    // 某个玩家进入 Lobby / 房间
    public readonly struct LobbyClientJoinedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;         // -1 表示拿不到
        public readonly string PlayerName;
        public readonly bool IsLocalPlayer;    // 是否当前这台机器上的玩家

        public LobbyClientJoinedEventData(
            NetUIEventSource source,
            int networkId,
            string playerName,
            bool isLocalPlayer)
        {
            Source = source;
            NetworkId = networkId;
            PlayerName = playerName;
            IsLocalPlayer = isLocalPlayer;
        }
    }

    // 某个玩家离开 Lobby / 掉线
    public readonly struct LobbyClientLeftEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;
        public readonly string PlayerName;

        public LobbyClientLeftEventData(NetUIEventSource source, int networkId, string playerName)
        {
            Source = source;
            NetworkId = networkId;
            PlayerName = playerName;
        }
    }

#endregion

#region Match 相关事件载体

    public readonly struct MatchStartedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int LocalPlayerNetworkId;

        public MatchStartedEventData(NetUIEventSource source, int localId)
        {
            Source = source;
            LocalPlayerNetworkId = localId;
        }
    }

    public readonly struct MatchEndedEventData
    {
        public readonly NetUIEventSource Source;
        public readonly string Reason; // 比如 "HostExit" / "AllDead" / "Timeout"

        public MatchEndedEventData(NetUIEventSource source, string reason)
        {
            Source = source;
            Reason = reason;
        }
    }
#endregion

#region Connection 相关事件载体

    public readonly struct ConnectionLostEventData
    {
        public readonly NetUIEventSource Source;
        public readonly int NetworkId;
        public readonly string Reason;

        public ConnectionLostEventData(NetUIEventSource source, int networkId, string reason)
        {
            Source = source;
            NetworkId = networkId;
            Reason = reason;
        }
    }

#endregion

}
