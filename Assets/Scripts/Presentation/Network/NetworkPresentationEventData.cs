namespace AnimarsCatcher.Presentation.Network
{
    /// <summary>
    /// 标识事件来自客户端世界还是服务端世界
    /// </summary>
    public enum NetworkEventSource
    {
        Unknown = 0,
        ServerWorld,
        ClientWorld
    }

    /// <summary>
    /// 描述加入房间的客户端
    /// </summary>
    public readonly struct LobbyClientJoinedEvent
    {
        public readonly NetworkEventSource Source;
        public readonly int NetworkId;
        public readonly string PlayerName;
        public readonly bool IsLocalPlayer;

        public LobbyClientJoinedEvent(
            NetworkEventSource source,
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

    /// <summary>
    /// 描述离开房间或断线的客户端
    /// </summary>
    public readonly struct LobbyClientLeftEvent
    {
        public readonly NetworkEventSource Source;
        public readonly int NetworkId;
        public readonly string PlayerName;

        public LobbyClientLeftEvent(NetworkEventSource source, int networkId, string playerName)
        {
            Source = source;
            NetworkId = networkId;
            PlayerName = playerName;
        }
    }

    /// <summary>
    /// 描述客户端收到的权威对局开始通知
    /// </summary>
    public readonly struct MatchStartedEvent
    {
        public readonly NetworkEventSource Source;
        public readonly int LocalPlayerNetworkId;

        public MatchStartedEvent(NetworkEventSource source, int localPlayerNetworkId)
        {
            Source = source;
            LocalPlayerNetworkId = localPlayerNetworkId;
        }
    }

    /// <summary>
    /// 描述权威对局结束通知
    /// </summary>
    public readonly struct MatchEndedEvent
    {
        public readonly NetworkEventSource Source;
        public readonly string Reason;

        public MatchEndedEvent(NetworkEventSource source, string reason)
        {
            Source = source;
            Reason = reason;
        }
    }

    /// <summary>
    /// 描述网络连接中断
    /// </summary>
    public readonly struct ConnectionLostEvent
    {
        public readonly NetworkEventSource Source;
        public readonly int NetworkId;
        public readonly string Reason;

        public ConnectionLostEvent(NetworkEventSource source, int networkId, string reason)
        {
            Source = source;
            NetworkId = networkId;
            Reason = reason;
        }
    }
}
